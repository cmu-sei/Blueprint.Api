// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Blueprint.Api.Infrastructure.Extensions;
using Blueprint.Api.Infrastructure.Options;
using Blueprint.Api.Tests.Support;
using IdentityModel.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Player.Api.Client;
using Xunit;

// IdentityModel declares a ClientOptions of its own, so the name is ambiguous in the one file that reaches
// for both it and blueprint's sibling-api urls.
using ClientOptions = Blueprint.Api.Infrastructure.Options.ClientOptions;

namespace Blueprint.Api.Tests.Infrastructure.Extensions;

/// <summary><c>ApiClientsExtensions</c> - how blueprint gets a token for a sibling API and how it builds
/// the client that carries it.</summary>
public class ApiClientsExtensionsTests
{
    // ---------------------------------------------------------------------------------------------
    // GetHttpClient - the authorization header
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task GetHttpClient_AddsTheTokenAsAnAuthorizationHeader()
    {
        var handler = Idp();
        var token = await Token(handler);

        var client = ApiClientsExtensions.GetHttpClient(handler.AsFactory(), "http://player.example/", token);

        Assert.Equal("Bearer abc123", Assert.Single(client.DefaultRequestHeaders.GetValues("authorization")));
    }

    /// <remarks>
    /// The header is written by name rather than through <c>HttpRequestHeaders.Authorization</c>, so this
    /// also pins that it survives as a well-formed credential on the wire rather than only in the
    /// collection.
    /// </remarks>
    [Fact]
    public async Task GetHttpClient_TheAuthorizationHeaderReachesTheWire()
    {
        var handler = Idp().Answers("api/views/*", HttpStatusCode.NoContent);
        var token = await Token(handler);

        var client = ApiClientsExtensions.GetHttpClient(handler.AsFactory(), "http://player.example/", token);
        await client.GetAsync("api/views/1", Ct);

        Assert.Equal("Bearer abc123", handler.Sent[^1].Authorization);
    }

    /// <remarks>
    /// The documented path: <c>// Only add the header if the token was passed</c>. Every caller in the
    /// repository passes one, so this is the branch nothing takes - and the reason a null token is a quiet
    /// 401 from the sibling API rather than a complaint here.
    /// </remarks>
    [Fact]
    public void GetHttpClient_WithoutATokenResponse_AddsNoAuthorizationHeader()
    {
        var client = ApiClientsExtensions.GetHttpClient(
            new SiblingApiHandler().AsFactory(), "http://player.example/", tokenResponse: null);

        Assert.False(client.DefaultRequestHeaders.Contains("authorization"));
    }

    /// <summary>Get http client for a rejected token response throws a format exception.</summary>
    [Fact]
    public async Task GetHttpClient_ForARejectedTokenResponse_ThrowsAFormatException()
    {
        var handler = Idp(token: """{"error":"invalid_grant","error_description":"Invalid user credentials"}""",
            tokenStatus: HttpStatusCode.BadRequest);
        var refused = await ApiClientsExtensions.RequestTokenAsync(Options(), Client(handler));

        Assert.True(refused.IsError);
        Assert.Null(refused.TokenType);
        Assert.Null(refused.AccessToken);

        var thrown = Assert.Throws<FormatException>(() =>
            ApiClientsExtensions.GetHttpClient(handler.AsFactory(), "http://player.example/", refused));

        Assert.Equal("The format of value ' ' is invalid.", thrown.Message);
    }

    /// <summary>An unreachable token endpoint ends in the same <c>FormatException</c>.</summary>
    [Fact]
    public async Task GetHttpClient_ForAnUnreachableTokenEndpoint_ThrowsTheSameFormatException()
    {
        var handler = IdpWithAnUnreachableTokenEndpoint();
        var failed = await ApiClientsExtensions.RequestTokenAsync(Options(), Client(handler));

        Assert.True(failed.IsError);
        Assert.Equal(ResponseErrorType.Exception, failed.ErrorType);

        Assert.Throws<FormatException>(() =>
            ApiClientsExtensions.GetHttpClient(handler.AsFactory(), "http://player.example/", failed));
    }

    // ---------------------------------------------------------------------------------------------
    // GetHttpClient - the base address
    // ---------------------------------------------------------------------------------------------

    /// <remarks>
    /// Through the three-argument overload, because that is the only one production calls: the two-argument
    /// <c>GetHttpClient(factory, apiUrl)</c> has no caller anywhere in the repository.
    /// </remarks>
    [Theory]
    [InlineData("http://player.example", "http://player.example/")]
    [InlineData("http://player.example/", "http://player.example/")]
    [InlineData("http://player.example/player-api/", "http://player.example/player-api/")]
    public void GetHttpClient_SetsTheApiUrlAsTheBaseAddress(string apiUrl, string expected)
    {
        var client = ApiClientsExtensions.GetHttpClient(
            new SiblingApiHandler().AsFactory(), apiUrl, tokenResponse: null);

        Assert.Equal(expected, client.BaseAddress.ToString());
    }

    /// <summary>A null or empty api url throws when the client is built.</summary>
    [Fact]
    public void GetHttpClient_WithNoApiUrl_Throws()
    {
        var factory = new SiblingApiHandler().AsFactory();

        Assert.Throws<ArgumentNullException>(() =>
            ApiClientsExtensions.GetHttpClient(factory, null, tokenResponse: null));
        Assert.Throws<UriFormatException>(() =>
            ApiClientsExtensions.GetHttpClient(factory, string.Empty, tokenResponse: null));
    }

    /// <summary>A base address without a trailing slash loses its last path segment.</summary>
    [Fact]
    public async Task ABaseAddressWithoutATrailingSlash_LosesItsLastPathSegment()
    {
        var handler = new SiblingApiHandler().Answers("*", HttpStatusCode.NoContent);
        var client = ApiClientsExtensions.GetHttpClient(
            handler.AsFactory(), "http://player.example/player-api", tokenResponse: null);

        await new PlayerApiClient(client).DeleteViewAsync(ViewId, Ct);

        Assert.Equal($"api/views/{ViewId}", Assert.Single(handler.Paths));
    }

    [Fact]
    public async Task ABaseAddressWithATrailingSlash_KeepsItsPathPrefix()
    {
        var handler = new SiblingApiHandler().Answers("*", HttpStatusCode.NoContent);
        var client = ApiClientsExtensions.GetHttpClient(
            handler.AsFactory(), "http://player.example/player-api/", tokenResponse: null);

        await new PlayerApiClient(client).DeleteViewAsync(ViewId, Ct);

        Assert.Equal($"player-api/api/views/{ViewId}", Assert.Single(handler.Paths));
    }

    /// <summary>The shipped <c>PlayerApiUrl</c> is the one sibling url without a trailing slash.</summary>
    [Fact]
    public void TheShippedPlayerApiUrl_IsTheOnlyOneWithoutATrailingSlash()
    {
        var clientSettings = new ConfigurationBuilder()
            .AddJsonFile("appsettings.json")
            .Build()
            .GetSection("ClientSettings")
            .Get<ClientOptions>();

        Assert.EndsWith("/", clientSettings.CiteApiUrl);
        Assert.EndsWith("/", clientSettings.GalleryApiUrl);
        Assert.EndsWith("/", clientSettings.SteamfitterApiUrl);
        Assert.False(clientSettings.PlayerApiUrl.EndsWith('/'));
    }

    // ---------------------------------------------------------------------------------------------
    // RequestTokenAsync - the happy path
    // ---------------------------------------------------------------------------------------------

    /// <summary>A token costs three round trips: discovery, the key set, then the token.</summary>
    [Fact]
    public async Task RequestTokenAsync_FetchesDiscoveryTheJwksAndThenTheToken()
    {
        var handler = Idp();

        await ApiClientsExtensions.RequestTokenAsync(Options(), Client(handler));

        Assert.Equal([DiscoveryPath, JwksPath, TokenPath], handler.Paths);
    }

    /// <remarks>
    /// The endpoint comes from the discovery document rather than from configuration, which is the one thing
    /// blueprint gets from those first two round trips that it could not have been told.
    /// </remarks>
    [Fact]
    public async Task RequestTokenAsync_PostsThePasswordGrantToTheDiscoveredTokenEndpoint()
    {
        var handler = Idp();

        await ApiClientsExtensions.RequestTokenAsync(Options(), Client(handler));

        var sent = handler.Sent[^1];

        Assert.Equal(TokenPath, sent.Path);
        Assert.Equal(HttpMethod.Post, sent.Method);
        Assert.Contains("grant_type=password", sent.Body);
    }

    [Fact]
    public async Task RequestTokenAsync_SendsTheConfiguredUserName()
    {
        var handler = Idp();

        await ApiClientsExtensions.RequestTokenAsync(Options(), Client(handler));

        Assert.Contains("username=blueprint-admin", handler.Sent[^1].Body);
    }

    [Fact]
    public async Task RequestTokenAsync_SendsTheConfiguredClientId()
    {
        var handler = Idp();

        await ApiClientsExtensions.RequestTokenAsync(Options(), Client(handler));

        Assert.Contains("client_id=blueprint-admin", handler.Sent[^1].Body);
    }

    /// <remarks>
    /// All six scopes, form-encoded, which is what makes one token usable against all four sibling APIs -
    /// and what makes the shipped <c>ResourceOwnerAuthorization:Scope</c> a list a deployment has to keep in
    /// step with each API's audience.
    /// </remarks>
    [Fact]
    public async Task RequestTokenAsync_SendsTheConfiguredScope()
    {
        var handler = Idp();

        await ApiClientsExtensions.RequestTokenAsync(Options(), Client(handler));

        Assert.Contains("scope=player+player-vm+cite+gallery+steamfitter", handler.Sent[^1].Body);
    }

    /// <remarks>
    /// The shipped <c>Password</c> is an empty string, so the deployed instance sends <c>password=</c> and
    /// relies on the secret arriving from the environment. A missing secret is therefore not a startup
    /// failure but a refused grant, which is the case
    /// <see cref="GetHttpClient_ForARejectedTokenResponse_ThrowsAFormatException"/> describes.
    /// </remarks>
    [Fact]
    public async Task RequestTokenAsync_WithAnEmptyPassword_SendsItAnyway()
    {
        var handler = Idp();

        await ApiClientsExtensions.RequestTokenAsync(Options(), Client(handler));

        Assert.Contains("password=&", handler.Sent[^1].Body);
    }

    [Fact]
    public async Task RequestTokenAsync_WithAClientSecret_SendsItInTheBody()
    {
        var handler = Idp();
        var options = Options();
        options.ClientSecret = "s3cret";

        await ApiClientsExtensions.RequestTokenAsync(options, Client(handler));

        Assert.Contains("client_secret=s3cret", handler.Sent[^1].Body);
    }

    /// <summary>An empty client secret sends no <c>client_secret</c>.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task RequestTokenAsync_WithNoClientSecret_SendsNone(string secret)
    {
        var handler = Idp();
        var options = Options();
        options.ClientSecret = secret;

        await ApiClientsExtensions.RequestTokenAsync(options, Client(handler));

        Assert.DoesNotContain("client_secret", handler.Sent[^1].Body);
    }

    // ---------------------------------------------------------------------------------------------
    // RequestTokenAsync - the failures
    // ---------------------------------------------------------------------------------------------

    /// <remarks>
    /// A bare <c>System.Exception</c>, so nothing above can tell this from any other failure. The message is
    /// IdentityModel's, and it does at least name the url it could not read.
    /// </remarks>
    [Fact]
    public async Task RequestTokenAsync_WhenDiscoveryFails_ThrowsABareException()
    {
        var handler = new SiblingApiHandler().AnswersJson(DiscoveryPath, "not found", HttpStatusCode.NotFound);

        var thrown = await Assert.ThrowsAsync<Exception>(() =>
            ApiClientsExtensions.RequestTokenAsync(Options(), Client(handler)));

        Assert.Equal(typeof(Exception), thrown.GetType());
        Assert.Contains(DiscoveryPath, thrown.Message);
        Assert.Contains("Not Found", thrown.Message);
    }

    [Fact]
    public async Task RequestTokenAsync_WhenDiscoveryIsUnreachable_ThrowsABareException()
    {
        var handler = new SiblingApiHandler().Throws(DiscoveryPath);

        var thrown = await Assert.ThrowsAsync<Exception>(() =>
            ApiClientsExtensions.RequestTokenAsync(Options(), Client(handler)));

        Assert.Equal(typeof(Exception), thrown.GetType());
    }

    /// <summary>A rejected credential is returned as an error response.</summary>
    [Fact]
    public async Task RequestTokenAsync_WhenTheCredentialIsRejected_ReturnsAnErrorResponseRatherThanThrowing()
    {
        var handler = Idp(token: """{"error":"invalid_grant","error_description":"Invalid user credentials"}""",
            tokenStatus: HttpStatusCode.BadRequest);

        var response = await ApiClientsExtensions.RequestTokenAsync(Options(), Client(handler));

        Assert.True(response.IsError);
        Assert.Equal("invalid_grant", response.Error);
        Assert.Equal(ResponseErrorType.Protocol, response.ErrorType);
        Assert.Equal(HttpStatusCode.BadRequest, response.HttpStatusCode);
    }

    /// <summary>A non-loopback <c>http</c> authority is refused by policy before any request is sent.</summary>
    [Fact]
    public async Task RequestTokenAsync_ForANonLoopbackHttpAuthority_FailsBeforeMakingAnyRequest()
    {
        var handler = Idp();
        var options = Options();
        options.Authority = "http://idp.example.test/realms/crucible";

        var thrown = await Assert.ThrowsAsync<Exception>(() =>
            ApiClientsExtensions.RequestTokenAsync(options, Client(handler)));

        Assert.Contains("HTTPS required", thrown.Message);
        Assert.Empty(handler.Sent);
    }

    /// <remarks>
    /// Which is why the shipped <c>http://localhost:8080/realms/crucible</c> works in development.
    /// <c>DiscoveryPolicy.AllowHttpOnLoopback</c> is what makes the difference, not anything blueprint sets.
    /// </remarks>
    [Fact]
    public async Task RequestTokenAsync_ForALoopbackHttpAuthority_IsAllowed()
    {
        var handler = Idp();

        var response = await ApiClientsExtensions.RequestTokenAsync(Options(), Client(handler));

        Assert.False(response.IsError);
        Assert.Equal("abc123", response.AccessToken);
    }

    // ---------------------------------------------------------------------------------------------
    // RequestTokenAsync - what ValidateDiscoveryDocument actually gates
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task RequestTokenAsync_WithValidationOn_AcceptsTheMatchingIssuer()
    {
        var handler = Idp();
        var options = Options();
        options.ValidateDiscoveryDocument = true;

        var response = await ApiClientsExtensions.RequestTokenAsync(options, Client(handler));

        Assert.False(response.IsError);
    }

    [Fact]
    public async Task RequestTokenAsync_WithValidationOn_RefusesAMismatchedIssuer()
    {
        var handler = Idp(discovery: Discovery("http://localhost:8080/realms/somebody-else"));
        var options = Options();
        options.ValidateDiscoveryDocument = true;

        var thrown = await Assert.ThrowsAsync<Exception>(() =>
            ApiClientsExtensions.RequestTokenAsync(options, Client(handler)));

        Assert.Contains("Issuer name does not match authority", thrown.Message);
    }

    /// <summary>Request token async as shipped accepts a mismatched issuer.</summary>
    [Fact]
    public async Task RequestTokenAsync_AsShipped_AcceptsAMismatchedIssuer()
    {
        var handler = Idp(discovery: Discovery("http://localhost:8080/realms/somebody-else"));

        var response = await ApiClientsExtensions.RequestTokenAsync(Options(), Client(handler));

        Assert.False(response.IsError);
        Assert.Equal("abc123", response.AccessToken);
    }

    /// <summary>As shipped, the password grant goes to a token endpoint on another host.</summary>
    [Fact]
    public async Task RequestTokenAsync_AsShipped_PostsThePasswordToAnEndpointOnAnotherHost()
    {
        var handler = IdpNamingAnEndpointOnAnotherHost();

        var response = await ApiClientsExtensions.RequestTokenAsync(Options(), Client(handler));

        Assert.Equal("harvested", response.AccessToken);

        var sent = handler.Sent[^1];

        Assert.Equal("collect/token", sent.Path);
        Assert.Contains("username=blueprint-admin", sent.Body);
    }

    [Fact]
    public async Task RequestTokenAsync_WithValidationOn_RefusesAnEndpointOnAnotherHost()
    {
        var handler = IdpNamingAnEndpointOnAnotherHost();
        var options = Options();
        options.ValidateDiscoveryDocument = true;

        await Assert.ThrowsAsync<Exception>(() =>
            ApiClientsExtensions.RequestTokenAsync(options, Client(handler)));

        Assert.DoesNotContain("collect/token", handler.Paths);
    }

    // ---------------------------------------------------------------------------------------------
    // GetToken - the same thing, resolved from a scope
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task GetToken_ResolvesTheOptionsAndTheFactoryFromTheScope()
    {
        var handler = Idp();
        using var scope = Scope(handler, Options());

        var response = await ApiClientsExtensions.GetToken(scope);

        Assert.Equal("abc123", response.AccessToken);
        Assert.Equal([DiscoveryPath, JwksPath, TokenPath], handler.Paths);
    }

    /// <remarks>
    /// <c>GetRequiredService</c>, so a <c>ResourceOwnerAuthorization</c> section that failed to bind is an
    /// <c>InvalidOperationException</c> from the first integration step rather than from startup.
    /// </remarks>
    [Fact]
    public async Task GetToken_WithoutTheOptionsRegistered_Throws()
    {
        var services = new ServiceCollection();
        services.AddSingleton(new SiblingApiHandler().AsFactory());
        using var scope = services.BuildServiceProvider().CreateScope();

        await Assert.ThrowsAsync<InvalidOperationException>(() => ApiClientsExtensions.GetToken(scope));
    }

    /// <summary>Get token caches nothing so every call costs three round trips.</summary>
    [Fact]
    public async Task GetToken_CachesNothing_SoEveryCallCostsThreeRoundTrips()
    {
        var handler = Idp();
        using var scope = Scope(handler, Options());

        var first = await ApiClientsExtensions.GetToken(scope);
        var second = await ApiClientsExtensions.GetToken(scope);

        Assert.Equal(first.AccessToken, second.AccessToken);
        Assert.Equal(
            [DiscoveryPath, JwksPath, TokenPath, DiscoveryPath, JwksPath, TokenPath],
            handler.Paths);
    }

    /// <remarks>
    /// <c>GetToken</c> wraps its client in a <c>using</c>, which is why <see cref="SiblingApiHandler"/> hands
    /// out clients that do not own the handler. Against production's <c>IHttpClientFactory</c> disposing a
    /// client is correct and cheap; it is worth pinning that two calls in one scope do not interfere.
    /// </remarks>
    [Fact]
    public async Task GetToken_DisposesItsClientWithoutDisturbingTheNextCall()
    {
        var handler = Idp();
        using var scope = Scope(handler, Options());

        await ApiClientsExtensions.GetToken(scope);
        var second = await ApiClientsExtensions.GetToken(scope);

        Assert.False(second.IsError);
    }

    // ---------------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------------

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly Guid ViewId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private const string Realm = "realms/crucible";
    private const string DiscoveryPath = $"{Realm}/.well-known/openid-configuration";
    private const string JwksPath = $"{Realm}/protocol/openid-connect/certs";
    private const string TokenPath = $"{Realm}/protocol/openid-connect/token";

    /// <summary>
    /// A discovery document with the members IdentityModel insists on, and nothing else. Keycloak's real one
    /// is some sixty keys; none of the rest is read on this path.
    /// </summary>
    private static string Discovery(
        string issuer = "http://localhost:8080/realms/crucible",
        string tokenEndpoint = "http://localhost:8080/realms/crucible/protocol/openid-connect/token") => $$"""
        {
          "issuer": "{{issuer}}",
          "authorization_endpoint": "http://localhost:8080/{{Realm}}/protocol/openid-connect/auth",
          "token_endpoint": "{{tokenEndpoint}}",
          "jwks_uri": "http://localhost:8080/{{JwksPath}}",
          "response_types_supported": ["code"],
          "subject_types_supported": ["public"],
          "id_token_signing_alg_values_supported": ["RS256"]
        }
        """;

    /// <summary>An identity provider that answers all three requests a token costs.</summary>
    private static SiblingApiHandler Idp(
        string discovery = null,
        string token = """{"access_token":"abc123","token_type":"Bearer","expires_in":300}""",
        HttpStatusCode tokenStatus = HttpStatusCode.OK) =>
        new SiblingApiHandler()
            .AnswersJson(DiscoveryPath, discovery ?? Discovery())
            .AnswersJson(JwksPath, """{"keys":[]}""")
            .AnswersJson(TokenPath, token, tokenStatus);

    /// <summary>
    /// A discovery document that answers for the configured authority but names a token endpoint on an
    /// unrelated host, and that host answering the grant. This is what <c>ValidateEndpoints</c> exists to
    /// refuse; the issuer still matches, so the two flags can be told apart.
    /// </summary>
    /// <remarks>
    /// The endpoint is <c>https</c> deliberately. <c>RequireHttps</c> checks the endpoints in the document
    /// as well as the address it was fetched from, so an <c>http</c> endpoint elsewhere is refused whatever
    /// the validation flag says - which makes it the uninteresting case.
    /// </remarks>
    private static SiblingApiHandler IdpNamingAnEndpointOnAnotherHost() =>
        new SiblingApiHandler()
            .AnswersJson(DiscoveryPath, Discovery(tokenEndpoint: "https://elsewhere.example/collect/token"))
            .AnswersJson(JwksPath, """{"keys":[]}""")
            .AnswersJson("collect/token", """{"access_token":"harvested","token_type":"Bearer"}""");

    /// <summary>
    /// The same identity provider, with the token endpoint unreachable rather than answering. Built from
    /// scratch rather than by appending a rule to <see cref="Idp"/>: rules are matched in the order they were
    /// added, so a later rule for a path an earlier one already answers never fires.
    /// </summary>
    private static SiblingApiHandler IdpWithAnUnreachableTokenEndpoint() =>
        new SiblingApiHandler()
            .AnswersJson(DiscoveryPath, Discovery())
            .AnswersJson(JwksPath, """{"keys":[]}""")
            .Throws(TokenPath);

    /// <summary>The shipped <c>ResourceOwnerAuthorization</c> section, as a bound options object.</summary>
    private static ResourceOwnerAuthorizationOptions Options() => new()
    {
        Authority = "http://localhost:8080/realms/crucible",
        ClientId = "blueprint-admin",
        UserName = "blueprint-admin",
        Password = string.Empty,
        Scope = "player player-vm cite gallery steamfitter",
        TokenExpirationBufferSeconds = 900,
        ValidateDiscoveryDocument = false,
    };

    private static HttpClient Client(SiblingApiHandler handler) => new(handler, disposeHandler: false);

    private static async Task<TokenResponse> Token(SiblingApiHandler handler) =>
        await ApiClientsExtensions.RequestTokenAsync(Options(), Client(handler));

    private static IServiceScope Scope(SiblingApiHandler handler, ResourceOwnerAuthorizationOptions options)
    {
        var services = new ServiceCollection();
        services.AddSingleton(options);
        services.AddSingleton(handler.AsFactory());

        return services.BuildServiceProvider().CreateScope();
    }
}
