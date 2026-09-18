// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Blueprint.Api.Data;
using Blueprint.Api.Data.Enumerations;
using Blueprint.Api.Data.Models;
using Blueprint.Api.Hubs;
using Blueprint.Api.Infrastructure.Options;
using Blueprint.Api.Services;
using Blueprint.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

using ClientOptions = Blueprint.Api.Infrastructure.Options.ClientOptions;

namespace Blueprint.Api.Tests;

/// <summary>
/// <c>IntegrationService.CancelPush</c> and the cleanup behind it - what happens to a MSEL when somebody
/// cancels a deployment.
/// </summary>
/// <remarks>
/// <para>
/// The first test in this suite to drive a background worker. <c>IntegrationService</c> is a hosted
/// service the harness removes from the application's container, so it is constructed here directly with
/// the six things it takes: a null logger, a scope factory over a container holding the test's own
/// <c>BlueprintContext</c>, the real <c>IntegrationQueue</c>, <see cref="TestHttpHandler"/> as the
/// transport, the <c>ClientSettings</c> urls, and <see cref="HubRecorder"/> for the broadcasts.
/// </para>
/// <para>
/// <strong><c>CancelPush</c> returns before it has done anything.</strong> With no push in flight it calls
/// <c>PerformCancelCleanupAsync</c> as <c>_ = PerformCancelCleanupAsync(mselId)</c> - an un-awaited task
/// that outlives the request that started it. So the endpoint answers 200 while four DELETEs to four
/// sibling APIs are still to come, and nothing anywhere can tell the caller whether they happened. Every
/// test here therefore waits on the database for the outcome rather than on the call.
/// </para>
/// <para>
/// <strong>A cancel that fails leaves the MSEL mid-pull with its integration ids intact.</strong> The
/// cleanup sets the status to <c>Pulling</c> first and clears the integration ids last, and everything in
/// between is wrapped in one <c>try</c>. So a failure anywhere - an identity provider that will not issue
/// a token is the likely one - leaves a MSEL whose status says it is pulling, whose
/// <c>IntegrationStatus</c> says <c>ERROR: ...</c>, and which still points at a Player view, a Gallery
/// collection, a CITE evaluation and a Steamfitter scenario that may or may not still exist. There is no
/// retry and no path back other than pushing again. See
/// <see cref="CancelPush_WhenNoTokenCanBeFetched_LeavesTheMselPullingWithItsIdsIntact"/>.
/// </para>
/// <para>
/// <strong>Player is deleted twice.</strong> Lines 175-176 call <c>PullFromPlayerAsync</c> twice in a row
/// with the same view id. The copy of this code in <c>PullIntegrations</c> at lines 534-535 carries the
/// comment <c>// TODO: Player requires two deletes?</c>; this one carries nothing, so a reader here cannot
/// tell it from a copy-paste. Either way the second DELETE is a 404 that
/// <c>PullFromPlayerAsync</c>'s empty <c>catch</c> swallows. See
/// <see cref="CancelPush_SendsTwoDeletesToPlayer"/>.
/// </para>
/// <para>
/// <strong>Each pull has its own <c>try</c>, so one unreachable API does not stop the others</strong> -
/// which is right, and means a cancel reports success having deleted nothing. The four are attempted in a
/// fixed order: Steamfitter, CITE, Gallery, Player.
/// </para>
/// <para>
/// The cleanup deliberately avoids <c>SaveChangesAsync</c> for its progress updates, using
/// <c>ExecuteUpdateAsync</c> and a direct <c>SendAsync</c> instead - the comment says why: to keep the
/// <c>EntityEventInterceptor</c> → MediatR → SignalR chain out of a path that runs while browsers may be
/// saturated. The final clearing does use a fresh context and <c>SaveChangesAsync</c>, so that one
/// <c>MselUpdated</c> does go out.
/// </para>
/// <para>
/// Per this branch's rule, every test above characterizes rather than fixes, and says what fixing it will do
/// to the test.
/// </para>
/// </remarks>
public class IntegrationServiceCancelTests(DatabaseFixture fixture) : DatabaseTestBase(fixture)
{
    [Fact]
    public async Task CancelPush_PullsEveryIntegrationTheMselHas()
    {
        var msel = await SeedDeployedMsel();
        var handler = Idp().AllFourPullsSucceed();

        Service(handler).CancelPush(msel.Id);

        await WaitForApproval(msel.Id);

        Assert.Equal(
            [
                $"api/scenarios/{SteamfitterScenarioId}",
                $"api/evaluations/{CiteEvaluationId}",
                $"api/collections/{GalleryCollectionId}",
                $"api/views/{PlayerViewId}",
                $"api/views/{PlayerViewId}"
            ],
            handler.Paths.Where(x => !x.StartsWith("realms/")).ToList());
    }

    /// <remarks>
    /// The order is Steamfitter, CITE, Gallery, Player - the reverse of the order a push creates them in,
    /// which is what you would want if the later ones referred to the earlier ones. They do: a Gallery
    /// exhibit names a Steamfitter scenario, and a CITE evaluation names a Gallery exhibit.
    /// </remarks>
    [Fact]
    public async Task CancelPush_SkipsAnIntegrationTheMselDoesNotHave()
    {
        var msel = await SeedDeployedMsel(withCite: false, withGallery: false);
        var handler = Idp().AllFourPullsSucceed();

        Service(handler).CancelPush(msel.Id);

        await WaitForApproval(msel.Id);

        var pulls = handler.Paths.Where(x => !x.StartsWith("realms/")).ToList();

        Assert.Equal(
            [
                $"api/scenarios/{SteamfitterScenarioId}",
                $"api/views/{PlayerViewId}",
                $"api/views/{PlayerViewId}"
            ],
            pulls);
    }

    /// <remarks>
    /// Two identical DELETEs to the same view. The copy of this block in <c>PullIntegrations</c> asks
    /// <c>// TODO: Player requires two deletes?</c> and this one says nothing, so whether it is deliberate
    /// cannot be told from here. Deleting either line turns this test red, which is the point: it records
    /// that today there are two.
    /// </remarks>
    [Fact]
    public async Task CancelPush_SendsTwoDeletesToPlayer()
    {
        var msel = await SeedDeployedMsel(withCite: false, withGallery: false, withSteamfitter: false);
        var handler = Idp().AllFourPullsSucceed();

        Service(handler).CancelPush(msel.Id);

        await WaitForApproval(msel.Id);

        Assert.Equal(2, handler.Sent.Count(x => x.Path == $"api/views/{PlayerViewId}"));
        Assert.All(
            handler.Sent.Where(x => x.Path == $"api/views/{PlayerViewId}"),
            x => Assert.Equal(HttpMethod.Delete, x.Method));
    }

    [Fact]
    public async Task CancelPush_ClearsEveryIntegrationIdAndApprovesTheMsel()
    {
        var msel = await SeedDeployedMsel();
        var handler = Idp().AllFourPullsSucceed();

        Service(handler).CancelPush(msel.Id);

        await WaitForApproval(msel.Id);

        await using var context = NewContext();
        var cleared = await context.Msels.SingleAsync(x => x.Id == msel.Id, Ct);

        Assert.Null(cleared.PlayerViewId);
        Assert.Null(cleared.GalleryCollectionId);
        Assert.Null(cleared.GalleryExhibitId);
        Assert.Null(cleared.CiteEvaluationId);
        Assert.Null(cleared.SteamfitterScenarioId);
        Assert.Null(cleared.IntegrationStatus);
        Assert.Equal(MselItemStatus.Approved, cleared.Status);
    }

    /// <remarks>
    /// The first thing the cleanup does, before it has a token or has reached any sibling API: write the
    /// status and tell the browsers. So a user sees "Cancelling" immediately and then either an approved
    /// MSEL or nothing more at all.
    /// </remarks>
    [Fact]
    public async Task CancelPush_AnnouncesItselfBeforeDoingAnything()
    {
        var msel = await SeedDeployedMsel();
        var handler = Idp().AllFourPullsSucceed();

        Service(handler).CancelPush(msel.Id);

        await WaitForApproval(msel.Id);

        var statuses = Hub.Of(MainHubMethods.IntegrationStatusUpdated);

        Assert.Equal(
            "Cancelling - removing partial integrations",
            Assert.IsType<string>(statuses[0].Args[1]));
        Assert.Equal(msel.Id, statuses[0].Payload);
    }

    [Fact]
    public async Task CancelPush_BroadcastsEveryStatusToTheMselGroupAndTheAdminGroup()
    {
        var msel = await SeedDeployedMsel();
        var handler = Idp().AllFourPullsSucceed();

        Service(handler).CancelPush(msel.Id);

        await WaitForApproval(msel.Id);

        var recipients = Hub.Of(MainHubMethods.IntegrationStatusUpdated)
            .Select(x => x.Group)
            .Distinct()
            .ToList();

        Assert.Equal(2, recipients.Count);
        Assert.Contains(msel.Id.ToString(), recipients);
        Assert.Contains(MainHub.ADMIN_DATA_GROUP, recipients);
    }

    /// <remarks>
    /// One status per integration as it is pulled, so the progress a user sees names the application being
    /// removed. Each is sent to both groups, which is why the count is doubled.
    /// </remarks>
    [Fact]
    public async Task CancelPush_AnnouncesEachIntegrationAsItPullsIt()
    {
        var msel = await SeedDeployedMsel();
        var handler = Idp().AllFourPullsSucceed();

        Service(handler).CancelPush(msel.Id);

        await WaitForApproval(msel.Id);

        var announced = Hub.Of(MainHubMethods.IntegrationStatusUpdated)
            .Where(x => x.Group == msel.Id.ToString())
            .Select(x => (string)x.Args[1])
            .ToList();

        Assert.Equal(
            [
                "Cancelling - removing partial integrations",
                "Cancelling - pulling Steamfitter Scenario",
                "Cancelling - pulling CITE Evaluation",
                "Cancelling - pulling Gallery Collection",
                "Cancelling - pulling Player View"
            ],
            announced);
    }

    /// <remarks>
    /// Each pull sits in its own <c>try</c>, so an unreachable Steamfitter does not stop CITE, Gallery and
    /// Player from being pulled, and the MSEL is still approved and cleared at the end. That is the right
    /// shape - and it means a cancel reports success having deleted nothing at all, since
    /// <c>PullFromSteamfitterAsync</c> and its three siblings each swallow their own failures too.
    /// </remarks>
    [Fact]
    public async Task CancelPush_WhenOnePullFails_StillPullsTheRestAndApprovesTheMsel()
    {
        var msel = await SeedDeployedMsel();
        var handler = Idp()
            .Throws($"api/scenarios/{SteamfitterScenarioId}")
            .AllFourPullsSucceed();

        Service(handler).CancelPush(msel.Id);

        await WaitForApproval(msel.Id);

        Assert.Contains($"api/evaluations/{CiteEvaluationId}", handler.Paths);
        Assert.Contains($"api/collections/{GalleryCollectionId}", handler.Paths);
        Assert.Contains($"api/views/{PlayerViewId}", handler.Paths);
    }

    /// <remarks>
    /// <para>
    /// The defect in this class's remarks. <c>ApiClientsExtensions.GetToken</c> throws when the identity
    /// provider's discovery document cannot be read, and it is called <em>outside</em> the four per-pull
    /// <c>try</c> blocks - so nothing is pulled, the outer <c>catch</c> writes an <c>ERROR:</c> status, and
    /// the block that clears the integration ids never runs.
    /// </para>
    /// <para>
    /// What is left is a MSEL that says it is <c>Pulling</c>, still points at four things in four other
    /// applications, and has no path forward but a fresh push. Clearing the ids before the pulls, or
    /// retrying, or setting the status back - any of those turns this test red.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task CancelPush_WhenNoTokenCanBeFetched_LeavesTheMselPullingWithItsIdsIntact()
    {
        var msel = await SeedDeployedMsel();
        var handler = new TestHttpHandler()
            .AnswersJson(DiscoveryPath, "not found", HttpStatusCode.NotFound)
            .AllFourPullsSucceed();

        Service(handler).CancelPush(msel.Id);

        var status = await WaitFor(
            msel.Id, x => x.IntegrationStatus is not null && x.IntegrationStatus.StartsWith("ERROR"));

        Assert.StartsWith("ERROR: Cancellation cleanup failed", status.IntegrationStatus);
        Assert.Equal(MselItemStatus.Pulling, status.Status);
        Assert.Equal(PlayerViewId, status.PlayerViewId);
        Assert.Equal(CiteEvaluationId, status.CiteEvaluationId);
        Assert.Equal(GalleryCollectionId, status.GalleryCollectionId);
        Assert.Equal(SteamfitterScenarioId, status.SteamfitterScenarioId);
        Assert.DoesNotContain(handler.Paths, x => x.StartsWith("api/"));
    }

    /// <remarks>
    /// And the error status is broadcast, so the user is told - which is the one thing this path does do.
    /// </remarks>
    [Fact]
    public async Task CancelPush_WhenTheCleanupFails_BroadcastsTheError()
    {
        var msel = await SeedDeployedMsel();
        var handler = new TestHttpHandler()
            .AnswersJson(DiscoveryPath, "not found", HttpStatusCode.NotFound)
            .AllFourPullsSucceed();

        Service(handler).CancelPush(msel.Id);

        await WaitFor(msel.Id, x => x.IntegrationStatus is not null && x.IntegrationStatus.StartsWith("ERROR"));

        var errors = Hub.Of(MainHubMethods.IntegrationStatusUpdated)
            .Where(x => x.Args[1] is string status && status.StartsWith("ERROR"))
            .ToList();

        Assert.Equal(2, errors.Count);
    }

    /// <remarks>
    /// The status write is an <c>ExecuteUpdateAsync</c> that matches nothing, the broadcast goes out anyway,
    /// and the MSEL lookup then finds nothing and returns. So cancelling a MSEL that does not exist tells
    /// every connected client that one is being cancelled, and contacts no sibling API.
    /// </remarks>
    [Fact]
    public async Task CancelPush_ForAMselThatDoesNotExist_BroadcastsAnywayAndPullsNothing()
    {
        var unknown = Guid.NewGuid();
        var handler = Idp().AllFourPullsSucceed();

        Service(handler).CancelPush(unknown);

        await WaitForHub(x => x.Group == unknown.ToString());

        Assert.DoesNotContain(handler.Paths, x => x.StartsWith("api/"));
        Assert.Equal(
            "Cancelling - removing partial integrations",
            (string)Hub.Of(MainHubMethods.IntegrationStatusUpdated)[0].Args[1]);
    }

    /// <remarks>
    /// A token is fetched once for the whole cleanup and shared by all four clients, which is the one place
    /// in the integration layer that does not pay for a token per operation. Three round trips, not twelve.
    /// </remarks>
    [Fact]
    public async Task CancelPush_FetchesOneTokenForAllFourPulls()
    {
        var msel = await SeedDeployedMsel();
        var handler = Idp().AllFourPullsSucceed();

        Service(handler).CancelPush(msel.Id);

        await WaitForApproval(msel.Id);

        Assert.Equal(1, handler.Paths.Count(x => x == TokenPath));
    }

    // ---------------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------------

    private static readonly Guid PlayerViewId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid GalleryCollectionId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid GalleryExhibitId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid CiteEvaluationId = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly Guid SteamfitterScenarioId = Guid.Parse("55555555-5555-5555-5555-555555555555");

    private const string Realm = "realms/crucible";
    private const string DiscoveryPath = $"{Realm}/.well-known/openid-configuration";
    private const string TokenPath = $"{Realm}/protocol/openid-connect/token";

    /// <summary>The broadcasts the service made, cleared for each test by construction.</summary>
    private HubRecorder Hub { get; } = new();

    /// <summary>
    /// An identity provider that answers the three requests a token costs. Every test needs one, because
    /// the cleanup fetches a token before it touches any sibling API.
    /// </summary>
    private static TestHttpHandler Idp() =>
        new TestHttpHandler()
            .AnswersJson(DiscoveryPath, """
                {
                  "issuer": "http://localhost:8080/realms/crucible",
                  "authorization_endpoint": "http://localhost:8080/realms/crucible/protocol/openid-connect/auth",
                  "token_endpoint": "http://localhost:8080/realms/crucible/protocol/openid-connect/token",
                  "jwks_uri": "http://localhost:8080/realms/crucible/protocol/openid-connect/certs",
                  "response_types_supported": ["code"],
                  "subject_types_supported": ["public"],
                  "id_token_signing_alg_values_supported": ["RS256"]
                }
                """)
            .AnswersJson($"{Realm}/protocol/openid-connect/certs", """{"keys":[]}""")
            .AnswersJson(TokenPath, """{"access_token":"abc123","token_type":"Bearer","expires_in":300}""");

    /// <summary>
    /// The service under test, over the test's own database and the given transport.
    /// </summary>
    /// <remarks>
    /// A null logger rather than a recording one: nothing here asserts on a log line, and the swallowed
    /// failures this class characterizes are all observable in the database or on the hub. The queue is the
    /// real <c>IntegrationQueue</c> and is never used - <c>CancelPush</c> does not go through it - but the
    /// constructor requires one.
    /// </remarks>
    private IntegrationService Service(TestHttpHandler handler) => new(
        NullLogger<IntegrationService>.Instance,
        new SessionScopeFactory(Session, handler),
        new IntegrationQueue(),
        handler.AsFactory(),
        Options(),
        Hub);

    private static IOptionsMonitor<ClientOptions> Options()
    {
        var monitor = Substitute.For<IOptionsMonitor<ClientOptions>>();
        monitor.CurrentValue.Returns(new ClientOptions
        {
            PlayerApiUrl = "http://player.example/",
            GalleryApiUrl = "http://gallery.example/",
            CiteApiUrl = "http://cite.example/",
            SteamfitterApiUrl = "http://steamfitter.example/"
        });

        return monitor;
    }

    /// <summary>
    /// A MSEL deployed to all four applications, which is the state a cancel acts on.
    /// </summary>
    private async Task<MselEntity> SeedDeployedMsel(
        bool withPlayer = true, bool withGallery = true, bool withCite = true, bool withSteamfitter = true)
    {
        var msel = BlueprintAppFactory.Msel(status: MselItemStatus.Deployed);

        if (withPlayer)
        {
            msel.PlayerViewId = PlayerViewId;
        }

        if (withGallery)
        {
            msel.GalleryCollectionId = GalleryCollectionId;
            msel.GalleryExhibitId = GalleryExhibitId;
        }

        if (withCite)
        {
            msel.CiteEvaluationId = CiteEvaluationId;
        }

        if (withSteamfitter)
        {
            msel.SteamfitterScenarioId = SteamfitterScenarioId;
        }

        await Seed(msel);

        return msel;
    }

    /// <summary>
    /// Waits for the cleanup to finish, which is the only signal it gives: <c>CancelPush</c> returns
    /// immediately and the work runs on an un-awaited task.
    /// </summary>
    private Task<MselEntity> WaitForApproval(Guid mselId) =>
        WaitFor(mselId, x => x.Status == MselItemStatus.Approved);

    private async Task<MselEntity> WaitFor(Guid mselId, Func<MselEntity, bool> done)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);

        while (DateTime.UtcNow < deadline)
        {
            await using var context = NewContext();
            var msel = await context.Msels.AsNoTracking().SingleOrDefaultAsync(x => x.Id == mselId, Ct);

            if (msel is not null && done(msel))
            {
                return msel;
            }

            await Task.Delay(50, Ct);
        }

        throw new TimeoutException(
            $"The cancel cleanup for {mselId} did not reach the expected state within twenty seconds.");
    }

    /// <summary>For the one case with no database change to wait on.</summary>
    private async Task WaitForHub(Func<HubSend, bool> done)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);

        while (DateTime.UtcNow < deadline)
        {
            if (Hub.Sends.Any(done))
            {
                return;
            }

            await Task.Delay(50, Ct);
        }

        throw new TimeoutException("The cancel cleanup broadcast nothing within twenty seconds.");
    }

    /// <summary>
    /// The scope factory <c>IntegrationService</c> resolves everything from. Each scope gets a fresh
    /// <c>BlueprintContext</c> over the test's own database, which is what makes the worker's
    /// <c>_scopeFactory.CreateScope()</c> calls reach it.
    /// </summary>
    private sealed class SessionScopeFactory : IServiceScopeFactory
    {
        private readonly IServiceProvider _provider;

        public SessionScopeFactory(TestDatabaseSession session, TestHttpHandler handler)
        {
            var services = new ServiceCollection();

            services.AddScoped(_ => session.CreateContext());
            services.AddSingleton(handler.AsFactory());
            services.AddSingleton(new ResourceOwnerAuthorizationOptions
            {
                Authority = "http://localhost:8080/realms/crucible",
                ClientId = "blueprint-admin",
                UserName = "blueprint-admin",
                Password = string.Empty,
                Scope = "player player-vm cite gallery steamfitter"
            });
            services.AddScoped(_ => Substitute.For<IScenarioEventService>());

            _provider = services.BuildServiceProvider();
        }

        public IServiceScope CreateScope() => _provider.CreateScope();
    }
}

/// <summary>Stub rules for the four pulls, so each test says only what it is about.</summary>
internal static class CancelPullStubs
{
    public static TestHttpHandler AllFourPullsSucceed(this TestHttpHandler handler) =>
        handler
            .Answers("api/scenarios/*", HttpStatusCode.NoContent)
            .Answers("api/evaluations/*", HttpStatusCode.NoContent)
            .Answers("api/collections/*", HttpStatusCode.NoContent)
            .Answers("api/views/*", HttpStatusCode.NoContent);
}
