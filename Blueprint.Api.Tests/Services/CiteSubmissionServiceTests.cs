// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Claims;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Blueprint.Api.Data.Enumerations;
using Blueprint.Api.Data.Models;
using Blueprint.Api.Infrastructure.Options;
using Blueprint.Api.Services;
using Blueprint.Api.Tests.Support;
using Blueprint.Api.ViewModels;

namespace Blueprint.Api.Tests.Services;

/// <summary>
/// <c>CiteSubmissionService</c> when something before CITE fails: Blueprint's CITE API URL, or the identity
/// provider the service account signs in with. The message names that system rather than CITE, and a
/// timeout is <c>Unavailable</c> like any other failure, while the caller giving up still cancels.
/// </summary>
/// <remarks>
/// Here rather than in <c>CiteTeamResponsesEndpointTests</c> because each test owns its transport: the
/// endpoint tests share one identity provider per class, and a token route that failed for one test would
/// fail all the others. CITE's own failures are read end to end there.
/// </remarks>
public class CiteSubmissionServiceTests(DatabaseFixture fixture) : DatabaseTestBase(fixture)
{
    private const string Authority = "http://localhost:8080/realms/crucible";
    private const string DiscoveryUrl = Authority + "/.well-known/openid-configuration";
    private const string JwksUrl = Authority + "/protocol/openid-connect/certs";
    private const string TokenUrl = Authority + "/protocol/openid-connect/token";
    private const string CiteApiUrl = "http://cite.test/";

    private const string Discovery = """
        {
          "issuer": "http://localhost:8080/realms/crucible",
          "authorization_endpoint": "http://localhost:8080/realms/crucible/protocol/openid-connect/auth",
          "token_endpoint": "http://localhost:8080/realms/crucible/protocol/openid-connect/token",
          "jwks_uri": "http://localhost:8080/realms/crucible/protocol/openid-connect/certs",
          "response_types_supported": ["code"],
          "subject_types_supported": ["public"],
          "id_token_signing_alg_values_supported": ["RS256"]
        }
        """;

    private readonly StubHttpMessageHandler _http = new StubHttpMessageHandler().RefusesUnmatched();

    private RecordingLogger<CiteSubmissionService> Log { get; } = new();

    // ---------------------------------------------------------------------------------------------
    // Blueprint's own configuration
    // ---------------------------------------------------------------------------------------------

    /// <summary>Without a CITE API URL nothing is sent anywhere, not even the token request.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a url")]
    [InlineData("/cite/")]
    [InlineData("ftp://cite.test/")]
    public async Task NoUsableCiteApiUrl_IsUnavailableNamingTheSetting(string citeApiUrl)
    {
        var (msel, team) = await SeedPushed();

        var responses = await Read(msel, team, Service(citeApiUrl));

        Assert.Equal(CiteTeamResponsesStatus.Unavailable, responses.Status);
        Assert.Equal("Blueprint has no valid CITE API URL configured.", responses.Message);
        Assert.Empty(_http.Requests);
    }

    // ---------------------------------------------------------------------------------------------
    // The identity provider
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheDiscoveryDocumentTimingOut_IsUnavailableNamingTheIdentityProvider()
    {
        var (msel, team) = await SeedPushed();
        _http.RespondByThrowing(DiscoveryUrl, HttpClientTimeout());

        var responses = await Read(msel, team);

        AssertUnavailable(responses, "The identity provider did not answer in time.");
    }

    [Fact]
    public async Task TheDiscoveryDocumentRefused_IsUnavailableNamingTheIdentityProvider()
    {
        var (msel, team) = await SeedPushed();
        _http.RespondWithStatus(DiscoveryUrl, HttpStatusCode.InternalServerError);

        var responses = await Read(msel, team);

        AssertUnavailable(responses, "The identity provider's discovery document could not be read.");
    }

    [Fact]
    public async Task TheTokenEndpointTimingOut_IsUnavailableNamingTheIdentityProvider()
    {
        var (msel, team) = await SeedPushed();
        ArrangeDiscovery();
        _http.RespondByThrowing(TokenUrl, HttpClientTimeout());

        var responses = await Read(msel, team);

        AssertUnavailable(responses, "The identity provider did not answer in time.");
    }

    [Fact]
    public async Task TheTokenEndpointUnreachable_IsUnavailableNamingTheIdentityProvider()
    {
        var (msel, team) = await SeedPushed();
        ArrangeDiscovery();
        _http.RespondByThrowing(TokenUrl, new HttpRequestException("Connection refused"));

        var responses = await Read(msel, team);

        AssertUnavailable(responses, "The identity provider could not be reached.");
    }

    /// <summary>An expired service account password, say: Keycloak answers 400 invalid_grant.</summary>
    [Fact]
    public async Task TheServiceAccountRefused_IsUnavailableNamingTheIdentityProvider()
    {
        var (msel, team) = await SeedPushed();
        ArrangeDiscovery();
        _http.Respond(
            TokenUrl,
            Encoding.UTF8.GetBytes("""{"error":"invalid_grant","error_description":"Invalid user credentials"}"""),
            "application/json",
            HttpStatusCode.BadRequest);

        var responses = await Read(msel, team);

        AssertUnavailable(responses, "The identity provider refused Blueprint's service account.");
        Assert.Contains(Log.Entries, e => e.Message.Contains("invalid_grant"));
    }

    // ---------------------------------------------------------------------------------------------
    // The caller giving up
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Cancelled by the caller rather than by a timeout, the request is not answered as a failure: the
    /// cancellation goes on up, wherever it lands.
    /// </summary>
    [Theory]
    [InlineData(DiscoveryUrl)]
    [InlineData(TokenUrl)]
    [InlineData(CiteApiUrl)]
    public async Task TheCallerGivingUp_Cancels(string cancelledAt)
    {
        var (msel, team) = await SeedPushed();
        ArrangeDiscovery();
        ArrangeToken();
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var handler = new CancelsTheCallerAt(cancelledAt, caller, _http);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Service(handler: handler).GetTeamResponsesAsync(msel.Id, team.Id, 1, hasSystemPermission: true, caller.Token));
    }

    // ---------------------------------------------------------------------------------------------
    // Arrangement
    // ---------------------------------------------------------------------------------------------

    private async Task<(MselEntity Msel, TeamEntity Team)> SeedPushed()
    {
        var msel = TestData.Msel(status: MselItemStatus.Deployed);
        msel.UseCite = true;
        msel.CiteEvaluationId = Guid.NewGuid();
        await Seed(msel);
        var team = TestData.Team(msel.Id);
        team.CiteTeamTypeId = Guid.NewGuid();
        await Seed(team);

        return (msel, team);
    }

    private void ArrangeDiscovery() =>
        _http
            .Respond(DiscoveryUrl, Encoding.UTF8.GetBytes(Discovery), "application/json")
            .Respond(JwksUrl, Encoding.UTF8.GetBytes("""{"keys":[]}"""), "application/json");

    private void ArrangeToken() =>
        _http.Respond(
            TokenUrl,
            Encoding.UTF8.GetBytes("""{"access_token":"service-account-token","token_type":"Bearer","expires_in":300}"""),
            "application/json");

    /// <summary>What HttpClient throws when its own <c>Timeout</c> elapses, which no caller asked for.</summary>
    private static TaskCanceledException HttpClientTimeout() =>
        new("The request was canceled due to the configured HttpClient.Timeout of 100 seconds elapsing.",
            new TimeoutException("The operation was canceled."));

    private void AssertUnavailable(CiteTeamResponses responses, string message)
    {
        Assert.Equal(CiteTeamResponsesStatus.Unavailable, responses.Status);
        Assert.Equal(message, responses.Message);
        Assert.Null(responses.TeamSubmission);
        Assert.DoesNotContain(_http.Requests, r => r.StartsWith(CiteApiUrl, StringComparison.Ordinal));
    }

    private Task<CiteTeamResponses> Read(MselEntity msel, TeamEntity team, CiteSubmissionService service = null) =>
        (service ?? Service()).GetTeamResponsesAsync(msel.Id, team.Id, 1, hasSystemPermission: true, Ct);

    private CiteSubmissionService Service(string citeApiUrl = CiteApiUrl, HttpMessageHandler handler = null) =>
        new(Db,
            new ClaimsPrincipal(new ClaimsIdentity()),
            new StubHttpClientFactory(handler ?? _http),
            new ClientOptions { CiteApiUrl = citeApiUrl },
            new ResourceOwnerAuthorizationOptions
            {
                Authority = Authority,
                ClientId = "blueprint-admin",
                UserName = "blueprint-admin",
                Password = string.Empty,
                Scope = "cite",
                ValidateDiscoveryDocument = false,
            },
            Log);

    /// <summary>Cancels the caller's token as the first request for <paramref name="url"/> goes out.</summary>
    private sealed class CancelsTheCallerAt(string url, CancellationTokenSource caller, HttpMessageHandler inner)
        : DelegatingHandler(inner)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri.ToString().StartsWith(url, StringComparison.Ordinal))
            {
                caller.Cancel();
                // HttpClient hands the handler the caller's token linked with its own timeout
                cancellationToken.ThrowIfCancellationRequested();
            }

            return base.SendAsync(request, cancellationToken);
        }
    }
}
