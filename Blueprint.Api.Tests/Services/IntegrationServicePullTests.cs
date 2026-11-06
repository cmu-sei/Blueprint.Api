// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Blueprint.Api.Data.Enumerations;
using Blueprint.Api.Data.Models;
using Blueprint.Api.Hubs;
using Blueprint.Api.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Blueprint.Api.Tests.Services;

/// <summary><c>IntegrationService.PullIntegrations</c> - taking a MSEL's deployment back out of the four
/// sibling applications.</summary>
public class IntegrationServicePullTests(DatabaseFixture fixture) : DatabaseTestBase(fixture)
{
    [Fact]
    public async Task Pull_PullsEveryIntegrationTheMselHas()
    {
        var msel = await SeedDeployedMsel();
        var harness = Harness();

        await harness.PullAsync(msel.Id);
        await harness.WaitForFinalStatus(msel.Id, MselItemStatus.Approved, Ct);

        Assert.Equal(
            [
                $"steamfitter/api/scenarios/{SteamfitterScenarioId}",
                $"cite/api/evaluations/{CiteEvaluationId}",
                $"gallery/api/collections/{GalleryCollectionId}",
                $"player/api/views/{PlayerViewId}",
                $"player/api/views/{PlayerViewId}"
            ],
            harness.Handler.Paths.Where(x => !x.StartsWith("realms/")).ToList());
    }

    /// <remarks>
    /// The reverse of the order a push creates them in, which is what you want when the later ones name the
    /// earlier: a Gallery exhibit names a Steamfitter scenario, and a CITE evaluation names a Gallery
    /// exhibit.
    /// </remarks>
    [Fact]
    public async Task Pull_SkipsAnIntegrationTheMselDoesNotHave()
    {
        var msel = await SeedDeployedMsel(withCite: false, withSteamfitter: false);
        var harness = Harness();

        await harness.PullAsync(msel.Id);
        await harness.WaitForFinalStatus(msel.Id, MselItemStatus.Approved, Ct);

        Assert.Equal(
            [
                $"gallery/api/collections/{GalleryCollectionId}",
                $"player/api/views/{PlayerViewId}",
                $"player/api/views/{PlayerViewId}"
            ],
            harness.Handler.Paths.Where(x => !x.StartsWith("realms/")).ToList());
    }

    /// <summary>Pull sends two deletes to player.</summary>
    [Fact]
    public async Task Pull_SendsTwoDeletesToPlayer()
    {
        var msel = await SeedDeployedMsel(withCite: false, withGallery: false, withSteamfitter: false);
        var harness = Harness();

        await harness.PullAsync(msel.Id);
        await harness.WaitForFinalStatus(msel.Id, MselItemStatus.Approved, Ct);

        var deletes = harness.Handler.Sent.Where(x => x.Path == $"player/api/views/{PlayerViewId}").ToList();

        Assert.Equal(2, deletes.Count);
        Assert.All(deletes, x => Assert.Equal(HttpMethod.Delete, x.Method));
    }

    /// <remarks>
    /// The queue item's <c>FinalStatus</c>, not a constant - which is how the same worker serves both
    /// <c>POST msels/{id}/pull</c>, which wants the MSEL back at <c>Approved</c>, and the archive route,
    /// which wants something else. The cancel path hard-codes <c>Approved</c> instead.
    /// </remarks>
    [Theory]
    [InlineData(MselItemStatus.Approved)]
    [InlineData(MselItemStatus.Pending)]
    [InlineData(MselItemStatus.Archived)]
    public async Task Pull_TakesItsFinalStatusFromTheQueueItem(MselItemStatus finalStatus)
    {
        var msel = await SeedDeployedMsel();
        var harness = Harness();

        await harness.PullAsync(msel.Id, finalStatus);

        var pulled = await harness.WaitForFinalStatus(msel.Id, finalStatus, Ct);

        Assert.Equal(finalStatus, pulled.Status);
    }

    [Fact]
    public async Task Pull_ClearsEveryIntegrationIdAndTheIntegrationStatus()
    {
        var msel = await SeedDeployedMsel();
        var harness = Harness();

        await harness.PullAsync(msel.Id);

        var pulled = await harness.WaitForFinalStatus(msel.Id, MselItemStatus.Approved, Ct);

        Assert.Null(pulled.PlayerViewId);
        Assert.Null(pulled.GalleryCollectionId);
        Assert.Null(pulled.GalleryExhibitId);
        Assert.Null(pulled.CiteEvaluationId);
        Assert.Null(pulled.SteamfitterScenarioId);
        Assert.Null(pulled.IntegrationStatus);
    }

    [Fact]
    public async Task Pull_AnnouncesEachIntegrationAsItPullsIt()
    {
        var msel = await SeedDeployedMsel();
        var harness = Harness();

        await harness.PullAsync(msel.Id);
        await harness.WaitForFinalStatus(msel.Id, MselItemStatus.Approved, Ct);

        var announced = harness.Hub.ToGroup(msel.Id)
            .Where(x => x.Method == MainHubMethods.IntegrationStatusUpdated)
            .Select(x => (string)x.Arguments[1])
            .ToList();

        Assert.Equal(
            [
                "Pulling Integrations",
                "Pulling Steamfitter Scenario",
                "Pulling CITE Evaluation",
                "Pulling Gallery Collection",
                "Pulling Player View"
            ],
            announced);
    }

    [Fact]
    public async Task Pull_BroadcastsEveryStatusToTheMselGroupAndTheAdminGroup()
    {
        var msel = await SeedDeployedMsel();
        var harness = Harness();

        await harness.PullAsync(msel.Id);
        await harness.WaitForFinalStatus(msel.Id, MselItemStatus.Approved, Ct);

        var recipients = harness.Hub.Recipients(MainHubMethods.IntegrationStatusUpdated, msel.Id);

        Assert.Equal(2, recipients.Count);
        Assert.Contains(msel.Id.ToString(), recipients);
        Assert.Contains(MainHub.ADMIN_DATA_GROUP, recipients);
    }

    /// <summary>Each pull has its own <c>try</c>: an unreachable Steamfitter does not stop the other three, and
    /// the MSEL reaches its final status with every id cleared.</summary>
    [Fact]
    public async Task Pull_WhenOnePullFails_StillPullsTheRestAndFinishes()
    {
        var msel = await SeedDeployedMsel();
        var harness = Harness(x => x.Throws($"steamfitter/api/scenarios/{SteamfitterScenarioId}"));

        await harness.PullAsync(msel.Id);

        var pulled = await harness.WaitForFinalStatus(msel.Id, MselItemStatus.Approved, Ct);

        Assert.Contains($"cite/api/evaluations/{CiteEvaluationId}", harness.Handler.Paths);
        Assert.Contains($"gallery/api/collections/{GalleryCollectionId}", harness.Handler.Paths);
        Assert.Contains($"player/api/views/{PlayerViewId}", harness.Handler.Paths);
        Assert.Null(pulled.SteamfitterScenarioId);
    }

    /// <remarks>
    /// <para>
    /// The token is fetched in <c>ProcessTheMsel</c> before it decides between a push and a pull, so a pull
    /// never begins: nothing is contacted, no id is cleared, and the status stays where it was. The only
    /// trace is the <c>ERROR:</c> integration status.
    /// </para>
    /// <para>
    /// The <c>Deployed</c> assertion is the other half of this class's remarks: unlike the cancel path, a
    /// pull does not mark the MSEL <c>Pulling</c> on its way through, so a pull that dies leaves a MSEL that
    /// still claims to be deployed - which it is, since nothing was removed.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Pull_WhenNoTokenCanBeFetched_LeavesEverythingAsItWas()
    {
        var msel = await SeedDeployedMsel();
        var harness = Harness(x => x.AnswersJson(
            IntegrationServiceHarness.DiscoveryPath, "not found", HttpStatusCode.NotFound));

        await harness.PullAsync(msel.Id);

        var failed = await harness.WaitForError(msel.Id, Ct);

        Assert.StartsWith("ERROR:", failed.IntegrationStatus);
        Assert.Equal(MselItemStatus.Deployed, failed.Status);
        Assert.Equal(PlayerViewId, failed.PlayerViewId);
        Assert.Equal(CiteEvaluationId, failed.CiteEvaluationId);
        Assert.Equal(GalleryCollectionId, failed.GalleryCollectionId);
        Assert.Equal(SteamfitterScenarioId, failed.SteamfitterScenarioId);
        Assert.DoesNotContain(harness.Handler.Paths, x => !x.StartsWith("realms/"));
    }

    /// <summary>Pull for a MSEL that is not there reports a step that succeeded and leaks the di scope.</summary>
    [Fact]
    public async Task Pull_ForAMselThatIsNotThere_ReportsAStepThatSucceededAndLeaksTheDiScope()
    {
        var unknown = Guid.NewGuid();
        var harness = Harness();

        await harness.PullAsync(unknown);

        var error = await WaitForBroadcast(
            harness, unknown, x => x.Arguments[1] is string status && status.StartsWith("ERROR"));
        var message = (string)error.Arguments[1];

        Assert.Equal(unknown, error.Payload);
        Assert.StartsWith("ERROR: Getting Auth Token with scope Microsoft.Extensions.DependencyInjection", message);
        Assert.EndsWith("Object reference not set to an instance of an object.", message);
        Assert.DoesNotContain(unknown.ToString(), message);
        Assert.DoesNotContain(harness.Handler.Paths, x => !x.StartsWith("realms/"));
    }

    /// <remarks>
    /// One token for the whole pull, shared by all four clients - the same economy the cancel path has, and
    /// the opposite of every other caller in the integration layer, which fetches one per operation.
    /// </remarks>
    [Fact]
    public async Task Pull_FetchesOneTokenForAllFourPulls()
    {
        var msel = await SeedDeployedMsel();
        var harness = Harness();

        await harness.PullAsync(msel.Id);
        await harness.WaitForFinalStatus(msel.Id, MselItemStatus.Approved, Ct);

        Assert.Equal(1, harness.Handler.Paths.Count(x => x == IntegrationServiceHarness.TokenPath));
    }

    // ---------------------------------------------------------------------------------------------
    // The run row
    // ---------------------------------------------------------------------------------------------

    /// <summary>The pull clears the MSEL's ids as before, but the run keeps its own, and is marked Removed
    /// and ended.</summary>
    [Fact]
    public async Task Pull_KeepsTheRunsIdsAndMarksItRemoved()
    {
        var msel = await SeedDeployedMsel();
        var run = await SeedRunOf(msel);
        var harness = Harness();
        var before = DateTime.UtcNow;

        await harness.PullAsync(msel.Id);

        var pulled = await harness.WaitForFinalStatus(msel.Id, MselItemStatus.Approved, Ct);
        var stored = await ReadBack(db => db.MselRuns.AsNoTracking().SingleAsync(x => x.Id == run.Id, Ct));

        Assert.Null(pulled.PlayerViewId);
        Assert.Equal(MselRunStatus.Removed, stored.Status);
        Assert.NotNull(stored.ActualEndTime);
        Assert.InRange(stored.ActualEndTime.Value, before, DateTime.UtcNow);
        Assert.Equal(PlayerViewId, stored.PlayerViewId);
        Assert.Equal(GalleryCollectionId, stored.GalleryCollectionId);
        Assert.Equal(GalleryExhibitId, stored.GalleryExhibitId);
        Assert.Equal(CiteEvaluationId, stored.CiteEvaluationId);
        Assert.Equal(SteamfitterScenarioId, stored.SteamfitterScenarioId);
    }

    /// <summary>The ids the pull deletes come from the run, so an id only the run still holds is pulled
    /// too.</summary>
    [Fact]
    public async Task Pull_DeletesWhatTheRunNames()
    {
        var msel = await SeedDeployedMsel(withCite: false, withSteamfitter: false);
        var run = TestData.MselRun(msel.Id);
        run.PlayerViewId = PlayerViewId;
        run.GalleryCollectionId = GalleryCollectionId;
        run.GalleryExhibitId = GalleryExhibitId;
        run.CiteEvaluationId = CiteEvaluationId;
        run.SteamfitterScenarioId = SteamfitterScenarioId;
        await Seed(run);
        var harness = Harness();

        await harness.PullAsync(msel.Id);
        await harness.WaitForFinalStatus(msel.Id, MselItemStatus.Approved, Ct);

        Assert.Equal(
            [
                $"steamfitter/api/scenarios/{SteamfitterScenarioId}",
                $"cite/api/evaluations/{CiteEvaluationId}",
                $"gallery/api/collections/{GalleryCollectionId}",
                $"player/api/views/{PlayerViewId}",
                $"player/api/views/{PlayerViewId}"
            ],
            harness.Handler.Paths.Where(x => !x.StartsWith("realms/")).ToList());
    }

    /// <summary>A MSEL with no run (none was recorded for it) is pulled from its own ids, as before, and gets
    /// no run.</summary>
    [Fact]
    public async Task Pull_ForAMselWithNoRun_PullsTheMselsIdsAndRecordsNoRun()
    {
        var msel = await SeedDeployedMsel();
        var harness = Harness();

        await harness.PullAsync(msel.Id);
        await harness.WaitForFinalStatus(msel.Id, MselItemStatus.Approved, Ct);

        Assert.Contains($"cite/api/evaluations/{CiteEvaluationId}", harness.Handler.Paths);
        Assert.False(await ReadBack(db => db.MselRuns.AnyAsync(x => x.MselId == msel.Id, Ct)));
    }

    /// <summary>A run removed earlier is a closed record; the pull neither reads nor changes it.</summary>
    [Fact]
    public async Task Pull_LeavesAnEarlierRemovedRunAsItWas()
    {
        var msel = await SeedDeployedMsel();
        var earlier = TestData.MselRun(msel.Id, MselRunStatus.Removed);
        await Seed(earlier);
        var current = await SeedRunOf(msel);
        // as PostgreSQL stores it, to the microsecond
        var endedAt = (await ReadBack(db => db.MselRuns.AsNoTracking().SingleAsync(x => x.Id == earlier.Id, Ct))).ActualEndTime;
        var harness = Harness();

        await harness.PullAsync(msel.Id);
        await harness.WaitForFinalStatus(msel.Id, MselItemStatus.Approved, Ct);

        var stored = await ReadBack(db => db.MselRuns.AsNoTracking().SingleAsync(x => x.Id == earlier.Id, Ct));
        Assert.Equal(MselRunStatus.Removed, stored.Status);
        Assert.Equal(endedAt, stored.ActualEndTime);
        Assert.DoesNotContain($"cite/api/evaluations/{earlier.CiteEvaluationId}", harness.Handler.Paths);
        Assert.Equal(MselRunStatus.Removed,
            (await ReadBack(db => db.MselRuns.AsNoTracking().SingleAsync(x => x.Id == current.Id, Ct))).Status);
    }

    /// <summary>A pull that never begins (no token) leaves the run open, like the MSEL.</summary>
    [Fact]
    public async Task Pull_WhenNoTokenCanBeFetched_LeavesTheRunOpen()
    {
        var msel = await SeedDeployedMsel();
        var run = await SeedRunOf(msel);
        var harness = Harness(x => x.AnswersJson(
            IntegrationServiceHarness.DiscoveryPath, "not found", HttpStatusCode.NotFound));

        await harness.PullAsync(msel.Id);
        await harness.WaitForError(msel.Id, Ct);

        var stored = await ReadBack(db => db.MselRuns.AsNoTracking().SingleAsync(x => x.Id == run.Id, Ct));
        Assert.Equal(MselRunStatus.Active, stored.Status);
        Assert.Null(stored.ActualEndTime);
    }

    // ---------------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------------

    /// <summary>An Active run holding the same ids as <see cref="SeedDeployedMsel"/>'s MSEL.</summary>
    private async Task<MselRunEntity> SeedRunOf(MselEntity msel)
    {
        var run = TestData.MselRun(msel.Id);
        run.PlayerViewId = msel.PlayerViewId;
        run.GalleryCollectionId = msel.GalleryCollectionId;
        run.GalleryExhibitId = msel.GalleryExhibitId;
        run.CiteEvaluationId = msel.CiteEvaluationId;
        run.SteamfitterScenarioId = msel.SteamfitterScenarioId;
        await Seed(run);

        return run;
    }

    private static readonly Guid PlayerViewId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid GalleryCollectionId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid GalleryExhibitId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid CiteEvaluationId = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly Guid SteamfitterScenarioId = Guid.Parse("55555555-5555-5555-5555-555555555555");

    /// <remarks>
    /// <paramref name="first"/> is applied before the general rules, because rules are matched in the order
    /// they were added: a test that wants one route to fail has to register it before the rule that would
    /// otherwise answer it.
    /// </remarks>
    private IntegrationServiceHarness Harness(Func<SiblingApiHandler, SiblingApiHandler> first = null) =>
        new(Session, Everything(first));

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

    /// <summary>The identity provider and the four deletes a pull makes, and nothing else.</summary>
    private static SiblingApiHandler Everything(Func<SiblingApiHandler, SiblingApiHandler> first = null) =>
        (first is null ? new SiblingApiHandler() : first(new SiblingApiHandler()))
            .AnswersJson(IntegrationServiceHarness.DiscoveryPath, """
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
            .AnswersJson(IntegrationServiceHarness.JwksPath, """{"keys":[]}""")
            .AnswersJson(IntegrationServiceHarness.TokenPath,
                """{"access_token":"abc123","token_type":"Bearer","expires_in":300}""")
            .Answers("steamfitter/api/scenarios/*", HttpStatusCode.NoContent)
            .Answers("cite/api/evaluations/*", HttpStatusCode.NoContent)
            .Answers("gallery/api/collections/*", HttpStatusCode.NoContent)
            .Answers("player/api/views/*", HttpStatusCode.NoContent);

    /// <summary>For the one case with no row left to poll.</summary>
    private async Task<HubBroadcast> WaitForBroadcast(
        IntegrationServiceHarness harness, Guid group, Func<HubBroadcast, bool> done)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);

        while (DateTime.UtcNow < deadline)
        {
            var send = harness.Hub.ToGroup(group).FirstOrDefault(done);

            if (send is not null)
            {
                return send;
            }

            await Task.Delay(50, Ct);
        }

        throw new TimeoutException("The worker broadcast nothing matching within ten seconds.");
    }
}
