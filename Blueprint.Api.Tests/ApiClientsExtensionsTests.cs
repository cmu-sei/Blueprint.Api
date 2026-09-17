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
using Blueprint.Api.Tests.Infrastructure;
using IdentityModel.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Player.Api.Client;
using Xunit;

// IdentityModel declares a ClientOptions of its own, so the name is ambiguous in the one file that reaches
// for both it and blueprint's sibling-api urls.
using ClientOptions = Blueprint.Api.Infrastructure.Options.ClientOptions;

namespace Blueprint.Api.Tests;

/// <summary>
/// <c>ApiClientsExtensions</c> - how blueprint gets a token for a sibling API and how it builds the client
/// that carries it.
/// </summary>
/// <remarks>
/// <para>
/// Three methods, seventy-four lines, and every call blueprint makes to Player, Gallery, CITE or
/// Steamfitter goes through them. The four <c>Integration*Extensions</c> classes each open by calling
/// <c>GetHttpClient</c>, and <c>IntegrationService</c> (twice), <c>JoinService</c> and
/// <c>AddApplicationService</c> each open by calling <c>GetToken</c>. Nothing here touches the database, so
/// these are unit tests with no host and no fixture; <see cref="TestHttpHandler"/> stands in for the socket
/// and everything above it - IdentityModel's discovery and token requests, the generated clients, the
/// <c>HttpClient</c> pipeline - runs for real.
/// </para>
/// <para>
/// <strong>A rejected credential is not treated as an error, and surfaces two layers away as a header
/// format complaint.</strong> <c>RequestTokenAsync</c> checks <c>disco.IsError</c> and throws, then returns
/// the token response <em>without</em> checking <c>IsError</c> on it. A refused password gives back a
/// response whose <c>TokenType</c> and <c>AccessToken</c> are both null, and <c>GetHttpClient</c>
/// interpolates those into <c>$"{TokenType} {AccessToken}"</c> - a single space - and hands it to
/// <c>HttpHeaders.Add</c>, which throws <c>FormatException: The format of value ' ' is invalid.</c> That
/// exception is raised inside whichever integration step ran next, is caught by that step's own
/// <c>catch (Exception)</c>, and is logged as a failure of that step. So the operator whose service account
/// password is wrong is told that Steamfitter could not be reached. See
/// <see cref="GetHttpClient_ForARejectedTokenResponse_ThrowsAFormatException"/>.
/// </para>
/// <para>
/// <strong>Nothing caches a token.</strong> Every call is a fresh discovery document, a fresh JWKS fetch and
/// a fresh password grant - three round trips to the identity provider per operation, and a full integration
/// push does it twice. <c>ResourceOwnerAuthorizationOptions.TokenExpirationBufferSeconds</c> is bound and
/// shipped as <c>900</c> in <c>appsettings.json</c>, and is read nowhere in the repository: it is the
/// remains of the caching this code does not do. See
/// <see cref="GetToken_CachesNothing_SoEveryCallCostsThreeRoundTrips"/>.
/// </para>
/// <para>
/// <strong>Both failure paths throw a bare <c>System.Exception</c></strong> carrying a message and nothing
/// else, so no caller can tell a discovery failure from a policy violation from anything thrown deeper -
/// which is why every caller catches <c>Exception</c> and why none of them retries selectively.
/// </para>
/// <para>
/// <strong><c>ValidateDiscoveryDocument</c> is absent from <c>appsettings.json</c></strong>, so it binds
/// false and both <c>ValidateIssuerName</c> and <c>ValidateEndpoints</c> are off: blueprint accepts a
/// discovery document whose issuer and endpoints belong to somebody else, and posts the service account's
/// user name and password to whatever <c>token_endpoint</c> that document names - including one on an
/// unrelated host. The one check that stays on is IdentityModel's own <c>RequireHttps</c>, and it is doing
/// more work than the flag: it is why the shipped <c>http://localhost:8080</c> authority works at all
/// (loopback is exempt), why the same setting pointed at a real hostname over <c>http</c> fails before a
/// single request is made, and why a hostile endpoint has to be <c>https</c> to be used. See
/// <see cref="RequestTokenAsync_AsShipped_AcceptsAMismatchedIssuer"/>,
/// <see cref="RequestTokenAsync_AsShipped_PostsThePasswordToAnEndpointOnAnotherHost"/> and
/// <see cref="RequestTokenAsync_ForANonLoopbackHttpAuthority_FailsBeforeMakingAnyRequest"/>.
/// </para>
/// <para>
/// <strong><c>PlayerApiUrl</c> is the one of the four sibling API urls shipped without a trailing
/// slash.</strong> <c>GetHttpClient</c> assigns it straight to <c>BaseAddress</c>, and a base address with
/// no trailing slash loses its last path segment when a generated client resolves a relative route against
/// it. At <c>http://localhost:4300</c> there is no path to lose, so this is latent rather than live - but it
/// is one reverse proxy away from dropping <c>/player-api</c> off every request blueprint makes. See
/// <see cref="ABaseAddressWithoutATrailingSlash_LosesItsLastPathSegment"/>.
/// </para>
/// <para>
/// Per this branch's rule, every test above characterizes rather than fixes, and says what fixing it will do
/// to the test.
/// </para>
/// </remarks>
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
            new TestHttpHandler().AsFactory(), "http://player.example/", tokenResponse: null);

        Assert.False(client.DefaultRequestHeaders.Contains("authorization"));
    }

    /// <remarks>
    /// <para>
    /// The defect described in this class's remarks, in one test. The identity provider refuses the
    /// credential, <c>RequestTokenAsync</c> hands the refusal back as a <c>TokenResponse</c> rather than
    /// throwing, and <c>GetHttpClient</c> turns the two nulls into the header value <c>" "</c>,
    /// which <c>HttpHeaders.Add</c> refuses.
    /// </para>
    /// <para>
    /// Checking <c>IsError</c> on the token response - anywhere between the two - turns this test red, which
    /// is the point of it. The right answer is probably to throw where the refusal is known, so the message
    /// names the identity provider and the account rather than a space.
    /// </para>
    /// </remarks>
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

    /// <remarks>
    /// An unreachable identity provider takes the same route as a refused one: an error response rather than
    /// an exception, and then the same <c>FormatException</c>. Only the discovery step throws.
    /// </remarks>
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
            new TestHttpHandler().AsFactory(), apiUrl, tokenResponse: null);

        Assert.Equal(expected, client.BaseAddress.ToString());
    }

    /// <remarks>
    /// Neither is guarded, and both are reached from configuration: a <c>ClientSettings</c> section missing
    /// one of the four urls gives a null, and an empty string in the file gives the other. The exception is
    /// thrown when the first integration step runs rather than at startup, so a missing url looks like a
    /// failure of the application it names.
    /// </remarks>
    [Fact]
    public void GetHttpClient_WithNoApiUrl_Throws()
    {
        var factory = new TestHttpHandler().AsFactory();

        Assert.Throws<ArgumentNullException>(() =>
            ApiClientsExtensions.GetHttpClient(factory, null, tokenResponse: null));
        Assert.Throws<UriFormatException>(() =>
            ApiClientsExtensions.GetHttpClient(factory, string.Empty, tokenResponse: null));
    }

    /// <remarks>
    /// <para>
    /// <c>Uri</c> resolution, not a blueprint decision - but a blueprint consequence, because
    /// <c>GetHttpClient</c> is the only place the configured url is turned into a base address and nothing
    /// normalizes it first. The generated clients build every route as a relative uri
    /// (<c>api/views/{id}</c>), so the last segment of a base address with no trailing slash is treated as a
    /// file name and replaced.
    /// </para>
    /// <para>
    /// Appending a slash where it is missing - here, or in <c>ClientOptions</c> binding - turns this test
    /// red and <see cref="ABaseAddressWithATrailingSlash_KeepsItsPathPrefix"/> stays green.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ABaseAddressWithoutATrailingSlash_LosesItsLastPathSegment()
    {
        var handler = new TestHttpHandler().Answers("*", HttpStatusCode.NoContent);
        var client = ApiClientsExtensions.GetHttpClient(
            handler.AsFactory(), "http://player.example/player-api", tokenResponse: null);

        await new PlayerApiClient(client).DeleteViewAsync(ViewId, Ct);

        Assert.Equal($"api/views/{ViewId}", Assert.Single(handler.Paths));
    }

    [Fact]
    public async Task ABaseAddressWithATrailingSlash_KeepsItsPathPrefix()
    {
        var handler = new TestHttpHandler().Answers("*", HttpStatusCode.NoContent);
        var client = ApiClientsExtensions.GetHttpClient(
            handler.AsFactory(), "http://player.example/player-api/", tokenResponse: null);

        await new PlayerApiClient(client).DeleteViewAsync(ViewId, Ct);

        Assert.Equal($"player-api/api/views/{ViewId}", Assert.Single(handler.Paths));
    }

    /// <remarks>
    /// The shipped configuration, read from the same <c>appsettings.json</c> the API runs on. Three of the
    /// four sibling urls end in a slash and <c>PlayerApiUrl</c> does not, which is invisible today because
    /// the value has no path. Adding the slash turns this test red and is the fix.
    /// </remarks>
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

    /// <remarks>
    /// Three round trips, and the middle one is the surprise: <c>GetDiscoveryDocumentAsync</c> also fetches
    /// the key set named by <c>jwks_uri</c>, which blueprint never uses - it validates no token here, it
    /// only asks for one. Every operation that talks to a sibling API pays for all three.
    /// </remarks>
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

    /// <remarks>
    /// <para>
    /// An empty secret in configuration is the same as none at all - which is what a public client wants and
    /// what blueprint ships, <c>ResourceOwnerAuthorization</c> carrying no <c>ClientSecret</c> key.
    /// </para>
    /// <para>
    /// Production spells this <c>string.IsNullOrEmpty(ClientSecret) ? null : ClientSecret</c>, and that
    /// ternary is provably redundant: replacing the whole expression with the bare field leaves this test
    /// green, because IdentityModel omits an empty secret from the form just as it omits a null one. The test
    /// stays because what it pins is the wire contract rather than the expression - no <c>client_secret</c>
    /// is sent - which is what a deployment reading its identity provider's access log needs to be true.
    /// </para>
    /// </remarks>
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
        var handler = new TestHttpHandler().AnswersJson(DiscoveryPath, "not found", HttpStatusCode.NotFound);

        var thrown = await Assert.ThrowsAsync<Exception>(() =>
            ApiClientsExtensions.RequestTokenAsync(Options(), Client(handler)));

        Assert.Equal(typeof(Exception), thrown.GetType());
        Assert.Contains(DiscoveryPath, thrown.Message);
        Assert.Contains("Not Found", thrown.Message);
    }

    [Fact]
    public async Task RequestTokenAsync_WhenDiscoveryIsUnreachable_ThrowsABareException()
    {
        var handler = new TestHttpHandler().Throws(DiscoveryPath);

        var thrown = await Assert.ThrowsAsync<Exception>(() =>
            ApiClientsExtensions.RequestTokenAsync(Options(), Client(handler)));

        Assert.Equal(typeof(Exception), thrown.GetType());
    }

    /// <remarks>
    /// The asymmetry that makes the header defect possible: the discovery step's <c>IsError</c> is checked
    /// and throws, the token step's is not checked at all. Adding the check turns this test red, and
    /// <see cref="GetHttpClient_ForARejectedTokenResponse_ThrowsAFormatException"/> with it.
    /// </remarks>
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

    /// <remarks>
    /// <para>
    /// <c>DiscoveryPolicy.RequireHttps</c> is IdentityModel's default and blueprint sets only
    /// <c>ValidateIssuerName</c> and <c>ValidateEndpoints</c>, so it stays on: an <c>http</c> authority that
    /// is not loopback is refused by policy, before any request is attempted. The refusal arrives as the
    /// same bare <c>Exception</c> as every other discovery failure, so a misconfigured authority reads as an
    /// unreachable identity provider.
    /// </para>
    /// <para>
    /// The assertion that nothing was sent is the interesting half. A test that only checked the message
    /// would pass just as well against a version that tried the request first.
    /// </para>
    /// </remarks>
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

    /// <remarks>
    /// <para>
    /// The shipped configuration omits <c>ValidateDiscoveryDocument</c>, so it is false and this is what
    /// runs: the document's issuer is not checked, its endpoints are not checked, and the password grant
    /// goes to the <c>token_endpoint</c> the document named. The request below is answered by an endpoint
    /// under a realm the authority does not name, and blueprint takes the token.
    /// </para>
    /// <para>
    /// Setting the flag - in configuration, or by defaulting it to true - turns this test red and
    /// <see cref="RequestTokenAsync_WithValidationOn_RefusesAMismatchedIssuer"/> is what it becomes.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task RequestTokenAsync_AsShipped_AcceptsAMismatchedIssuer()
    {
        var handler = Idp(discovery: Discovery("http://localhost:8080/realms/somebody-else"));

        var response = await ApiClientsExtensions.RequestTokenAsync(Options(), Client(handler));

        Assert.False(response.IsError);
        Assert.Equal("abc123", response.AccessToken);
    }

    /// <remarks>
    /// <para>
    /// The sharper form of the finding above, and the one <c>ValidateEndpoints</c> exists to prevent. The
    /// document answers for the configured authority and names a token endpoint on a host that authority has
    /// nothing to do with; blueprint posts the service account's user name and password to it and takes what
    /// comes back. The issuer matches here, so this is the endpoint check alone rather than both flags at
    /// once.
    /// </para>
    /// <para>
    /// Turning the flag on turns this test red and
    /// <see cref="RequestTokenAsync_WithValidationOn_RefusesAnEndpointOnAnotherHost"/> is what replaces it.
    /// </para>
    /// </remarks>
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
        services.AddSingleton(new TestHttpHandler().AsFactory());
        using var scope = services.BuildServiceProvider().CreateScope();

        await Assert.ThrowsAsync<InvalidOperationException>(() => ApiClientsExtensions.GetToken(scope));
    }

    /// <remarks>
    /// <para>
    /// Two calls, six requests. <c>TokenExpirationBufferSeconds</c> is configured as 900 and read nowhere,
    /// so there is no cache for it to be the buffer of - and an integration push that fetches a token, does
    /// its work and fetches another one is paying three round trips each time.
    /// </para>
    /// <para>
    /// Caching a token for its lifetime less the buffer turns this test red, which is the point of it.
    /// </para>
    /// </remarks>
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
    /// <c>GetToken</c> wraps its client in a <c>using</c>, which is why <see cref="TestHttpHandler"/> hands
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
    private static TestHttpHandler Idp(
        string discovery = null,
        string token = """{"access_token":"abc123","token_type":"Bearer","expires_in":300}""",
        HttpStatusCode tokenStatus = HttpStatusCode.OK) =>
        new TestHttpHandler()
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
    private static TestHttpHandler IdpNamingAnEndpointOnAnotherHost() =>
        new TestHttpHandler()
            .AnswersJson(DiscoveryPath, Discovery(tokenEndpoint: "https://elsewhere.example/collect/token"))
            .AnswersJson(JwksPath, """{"keys":[]}""")
            .AnswersJson("collect/token", """{"access_token":"harvested","token_type":"Bearer"}""");

    /// <summary>
    /// The same identity provider, with the token endpoint unreachable rather than answering. Built from
    /// scratch rather than by appending a rule to <see cref="Idp"/>: rules are matched in the order they were
    /// added, so a later rule for a path an earlier one already answers never fires.
    /// </summary>
    private static TestHttpHandler IdpWithAnUnreachableTokenEndpoint() =>
        new TestHttpHandler()
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

    private static HttpClient Client(TestHttpHandler handler) => new(handler, disposeHandler: false);

    private static async Task<TokenResponse> Token(TestHttpHandler handler) =>
        await ApiClientsExtensions.RequestTokenAsync(Options(), Client(handler));

    private static IServiceScope Scope(TestHttpHandler handler, ResourceOwnerAuthorizationOptions options)
    {
        var services = new ServiceCollection();
        services.AddSingleton(options);
        services.AddSingleton(handler.AsFactory());

        return services.BuildServiceProvider().CreateScope();
    }
}
