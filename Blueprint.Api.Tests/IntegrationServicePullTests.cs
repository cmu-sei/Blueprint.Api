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
using Blueprint.Api.Tests.Infrastructure;
using Xunit;

namespace Blueprint.Api.Tests;

/// <summary>
/// <c>IntegrationService.PullIntegrations</c> - taking a MSEL's deployment back out of the four sibling
/// applications.
/// </summary>
/// <remarks>
/// <para>
/// Reached the way production reaches it: an item with <c>IsPush</c> false on the real queue, taken by the
/// worker's loop and run on a thread whose delegate is <c>private async void</c>. The four pulls themselves
/// are pinned by the <c>Integration*Extensions</c> files, so these tests are about the orchestration
/// around them.
/// </para>
/// <para>
/// <strong>It is a near-copy of <c>PerformCancelCleanupAsync</c>, and differs in three ways.</strong> The
/// final status comes from the queue item rather than being hard-coded to <c>Approved</c>, so the caller
/// decides what a pulled MSEL becomes. It does <em>not</em> set the status to <c>Pulling</c> while it
/// works, where the cancel path does - so a MSEL being pulled still says <c>Deployed</c> until the moment
/// it does not. And its Player double-delete carries the comment <c>// TODO: Player requires two
/// deletes?</c>, which the cancel copy lacks; the two blocks are otherwise identical, down to the order and
/// the per-pull <c>try</c>.
/// </para>
/// <para>
/// <strong>A pull that cannot get a token leaves everything as it was.</strong> The token is fetched in
/// <c>ProcessTheMsel</c> before the push-or-pull branch, so a pull never starts, the MSEL keeps all five
/// integration ids and its <c>Deployed</c> status, and the only trace is an <c>ERROR:</c> integration
/// status. As with the cancel path, nothing retries. See
/// <see cref="Pull_WhenNoTokenCanBeFetched_LeavesEverythingAsItWas"/>.
/// </para>
/// <para>
/// <strong>A MSEL that is not there produces a cascade of three failures and one error message.</strong>
/// <c>ProcessTheMsel</c> loads it with <c>SingleOrDefaultAsync</c> and never checks, so the pull
/// dereferences null; the inner handler then dereferences the same null building its error status; and only
/// the outer handler, which uses the queue item's id instead, manages to say anything. Queueing work for a
/// deleted MSEL is not far-fetched - the queue is a <c>BlockingCollection</c> with no bound and no
/// coordination with deletion. And the message that reaches the user names a step that <em>succeeded</em>
/// and has a dependency-injection scope object stringified into it. See
/// <see cref="Pull_ForAMselThatIsNotThere_ReportsAStepThatSucceededAndLeaksTheDiScope"/>.
/// </para>
/// <para>
/// Per this branch's rule, every test above characterizes rather than fixes, and says what fixing it will do
/// to the test.
/// </para>
/// </remarks>
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

    /// <remarks>
    /// Two identical DELETEs, and here the code says why it might be: <c>// TODO: Player requires two
    /// deletes?</c>. A question rather than an answer, and the copy of this block in
    /// <c>PerformCancelCleanupAsync</c> carries no comment at all. Either way the second is a 404 that
    /// <c>PullFromPlayerAsync</c>'s empty <c>catch</c> absorbs.
    /// </remarks>
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

        var announced = harness.Hub.Of(MainHubMethods.IntegrationStatusUpdated)
            .Where(x => x.Group == msel.Id.ToString())
            .Select(x => (string)x.Args[1])
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

        var recipients = harness.Hub.Of(MainHubMethods.IntegrationStatusUpdated)
            .Select(x => x.Group)
            .Distinct()
            .ToList();

        Assert.Equal(2, recipients.Count);
        Assert.Contains(msel.Id.ToString(), recipients);
        Assert.Contains(MainHub.ADMIN_DATA_GROUP, recipients);
    }

    /// <remarks>
    /// Each pull has its own <c>try</c>, so an unreachable Steamfitter does not stop the other three, and the
    /// MSEL still reaches its final status with every id cleared. Right in shape, and it means a pull reports
    /// success having deleted nothing - the four <c>PullFrom*Async</c> methods each swallow their own
    /// failures as well.
    /// </remarks>
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

    /// <remarks>
    /// <para>
    /// Three failures to produce one message. <c>ProcessTheMsel</c> loads the MSEL with
    /// <c>SingleOrDefaultAsync</c> and never checks it, so the pull dereferences null; the inner handler then
    /// dereferences the same null while building its error status; and only the outer handler, which uses the
    /// queue item's id rather than the MSEL's, gets as far as writing anything. The row is gone, so its
    /// <c>ExecuteUpdateAsync</c> matches nothing either and the broadcast is all that survives.
    /// </para>
    /// <para>
    /// What that broadcast says is the second finding. <c>currentProcessStep</c> was last set at line 293 to
    /// <c>"Getting Auth Token with scope " + scope</c> - a <c>DI</c> scope concatenated into a string - so
    /// the message a user is shown names a step that succeeded and stringifies an
    /// <c>IServiceScope</c> implementation into it. The whole of it reads
    /// <c>ERROR: Getting Auth Token with scope
    /// Microsoft.Extensions.DependencyInjection.ServiceLookup.ServiceProviderEngineScope failed - Object
    /// reference not set to an instance of an object.</c>
    /// </para>
    /// <para>
    /// A null check after the load turns this test red, and so does removing the <c>+ scope</c>. Both are
    /// fixes; the second is a one-word deletion.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Pull_ForAMselThatIsNotThere_ReportsAStepThatSucceededAndLeaksTheDiScope()
    {
        var unknown = Guid.NewGuid();
        var harness = Harness();

        await harness.PullAsync(unknown);

        var error = await WaitForBroadcast(
            harness, x => x.Args[1] is string status && status.StartsWith("ERROR"));
        var message = (string)error.Args[1];

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
    // Helpers
    // ---------------------------------------------------------------------------------------------

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
    private IntegrationServiceHarness Harness(Func<TestHttpHandler, TestHttpHandler> first = null) =>
        new(Session, Everything(first));

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

    /// <summary>The identity provider and the four deletes a pull makes, and nothing else.</summary>
    private static TestHttpHandler Everything(Func<TestHttpHandler, TestHttpHandler> first = null) =>
        (first is null ? new TestHttpHandler() : first(new TestHttpHandler()))
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
    private async Task<HubSend> WaitForBroadcast(
        IntegrationServiceHarness harness, Func<HubSend, bool> done)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);

        while (DateTime.UtcNow < deadline)
        {
            var send = harness.Hub.Sends.FirstOrDefault(done);

            if (send is not null)
            {
                return send;
            }

            await Task.Delay(50, Ct);
        }

        throw new TimeoutException("The worker broadcast nothing matching within ten seconds.");
    }
}
