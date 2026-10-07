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
using Blueprint.Api.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

using ClientOptions = Blueprint.Api.Infrastructure.Options.ClientOptions;

namespace Blueprint.Api.Tests.Services;

/// <summary><c>IntegrationService.CancelPush</c> and the cleanup behind it - what happens to a MSEL when
/// somebody cancels a deployment.</summary>
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

    /// <summary>Cancelling sends two identical DELETEs for the Player view.</summary>
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

        var statuses = Hub.Of(MainHubMethods.IntegrationStatusUpdated, msel.Id);

        Assert.Equal(
            "Cancelling - removing partial integrations",
            Assert.IsType<string>(statuses[0].Arguments[1]));
        Assert.Equal(msel.Id, statuses[0].Payload);
    }

    [Fact]
    public async Task CancelPush_BroadcastsEveryStatusToTheMselGroupAndTheAdminGroup()
    {
        var msel = await SeedDeployedMsel();
        var handler = Idp().AllFourPullsSucceed();

        Service(handler).CancelPush(msel.Id);

        await WaitForApproval(msel.Id);

        var recipients = Hub.Recipients(MainHubMethods.IntegrationStatusUpdated, msel.Id);

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

        var announced = Hub.ToGroup(msel.Id)
            .Where(x => x.Method == MainHubMethods.IntegrationStatusUpdated)
            .Select(x => (string)x.Arguments[1])
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

    /// <summary>Each pull has its own <c>try</c>: an unreachable Steamfitter does not stop the other three, and
    /// the MSEL is still approved and cleared.</summary>
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

    /// <summary>Cancel push when no token can be fetched leaves the MSEL pulling with its ids intact.</summary>
    [Fact]
    public async Task CancelPush_WhenNoTokenCanBeFetched_LeavesTheMselPullingWithItsIdsIntact()
    {
        var msel = await SeedDeployedMsel();
        var handler = new SiblingApiHandler()
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
        var handler = new SiblingApiHandler()
            .AnswersJson(DiscoveryPath, "not found", HttpStatusCode.NotFound)
            .AllFourPullsSucceed();

        Service(handler).CancelPush(msel.Id);

        await WaitFor(msel.Id, x => x.IntegrationStatus is not null && x.IntegrationStatus.StartsWith("ERROR"));

        var errors = Hub.Of(MainHubMethods.IntegrationStatusUpdated, msel.Id)
            .Where(x => x.Arguments[1] is string status && status.StartsWith("ERROR"))
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

        await WaitForHub(unknown);

        Assert.DoesNotContain(handler.Paths, x => x.StartsWith("api/"));
        Assert.Equal(
            "Cancelling - removing partial integrations",
            (string)Hub.Of(MainHubMethods.IntegrationStatusUpdated, unknown)[0].Arguments[1]);
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
    private HubRecorder<MainHub> Hub { get; } = new();

    /// <summary>
    /// An identity provider that answers the three requests a token costs. Every test needs one, because
    /// the cleanup fetches a token before it touches any sibling API.
    /// </summary>
    private static SiblingApiHandler Idp() =>
        new SiblingApiHandler()
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
    /// failures these tests read are all observable in the database or on the hub. The queue is the
    /// real <c>IntegrationQueue</c> and is never used - <c>CancelPush</c> does not go through it - but the
    /// constructor requires one.
    /// </remarks>
    private IntegrationService Service(SiblingApiHandler handler) => new(
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
        var msel = TestData.Msel(status: MselItemStatus.Deployed);

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
    private async Task WaitForHub(Guid group)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);

        while (DateTime.UtcNow < deadline)
        {
            if (Hub.ToGroup(group).Count > 0)
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

        public SessionScopeFactory(ITestDatabaseSession<BlueprintContext> session, SiblingApiHandler handler)
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
    public static SiblingApiHandler AllFourPullsSucceed(this SiblingApiHandler handler) =>
        handler
            .Answers("api/scenarios/*", HttpStatusCode.NoContent)
            .Answers("api/evaluations/*", HttpStatusCode.NoContent)
            .Answers("api/collections/*", HttpStatusCode.NoContent)
            .Answers("api/views/*", HttpStatusCode.NoContent);
}
