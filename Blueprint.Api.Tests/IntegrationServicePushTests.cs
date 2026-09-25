// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Blueprint.Api.Data.Enumerations;
using Blueprint.Api.Data.Models;
using Blueprint.Api.Hubs;
using Blueprint.Api.Services;
using Blueprint.Api.Tests.Infrastructure;
using NSubstitute;
using Xunit;

namespace Blueprint.Api.Tests;

/// <summary>
/// <c>IntegrationService</c>'s push - the orchestration that turns a MSEL into a Player view, a Gallery
/// collection, a CITE evaluation and a Steamfitter scenario.
/// </summary>
/// <remarks>
/// <para>
/// Everything the push calls is already pinned by the four <c>Integration*Extensions</c> test files, so
/// these tests are about orchestration only: which applications are contacted for which <c>Use*</c> flags,
/// in what order, what the progress says, and what is left behind when a step fails.
/// <see cref="IntegrationServiceHarness"/> builds the worker, and the push runs where production runs
/// it - on a <c>new Thread</c> whose delegate is <c>private async void</c>, taken off the real queue.
/// </para>
/// <para>
/// <strong>The Steamfitter task names and the CITE and Gallery move-change urls are built from a move's
/// index, not its number.</strong> <c>GetMovesAndGroups</c> computes a correctly-guarded <c>moveNumber</c>
/// local on line 784 and then stores <c>moveIndex</c> on line 785 - the dead local is the fix. So a MSEL
/// whose moves are numbered 1 and 2 gets Steamfitter tasks named <c>00-…</c> and <c>01-…</c>, and is told
/// to advance CITE and Gallery to move 0 and move 1. Its public sibling
/// <c>ScenarioEventService.GetMovesAndInjects</c> uses <c>MoveNumber</c>, so the same MSEL is described two
/// different ways to two applications in one push. See
/// <see cref="Push_NamesSteamfitterTasksFromTheMoveIndexRatherThanTheMoveNumber"/> and
/// <see cref="Push_TellsCiteAndGalleryToAdvanceToTheMoveIndexRatherThanTheMoveNumber"/>, and
/// <c>ScenarioEventServiceMovesAndInjectsTests</c> for the half that is right.
/// </para>
/// <para>
/// <strong>A push that fails leaves the MSEL undeployed with its integration ids already written.</strong>
/// The ids are generated and saved before any sibling API is contacted - deliberately, so that a failure
/// can be cleaned up - and the only thing the error path does is write an <c>ERROR:</c> status. So the MSEL
/// keeps pointing at whatever was created before the failure, its status stays wherever it was, and the
/// only ways forward are a fresh push or a cancel. That the ids survive is what makes a cancel able to
/// clean up; that nothing triggers one is the gap.
/// </para>
/// <para>
/// <strong>A MSEL that uses nothing deploys.</strong> With all four flags false the push generates no ids,
/// builds no clients, fetches no user lists, contacts nothing, and marks the MSEL <c>Deployed</c> - so
/// "deployed" does not imply that anything exists anywhere.
/// </para>
/// <para>
/// The order is Player teams, Gallery, CITE, Steamfitter, then Player applications. Player is split in two
/// because an application's url can name the CITE evaluation and the Gallery exhibit, which do not exist
/// until those two have run. The three user lists are fetched concurrently before any of it.
/// </para>
/// <para>
/// Per this branch's rule, every test above characterizes rather than fixes, and says what fixing it will do
/// to the test.
/// </para>
/// </remarks>
public class IntegrationServicePushTests(DatabaseFixture fixture) : DatabaseTestBase(fixture)
{
    // ---------------------------------------------------------------------------------------------
    // The order, and the progress a user sees
    // ---------------------------------------------------------------------------------------------

    /// <remarks>
    /// The whole progress sequence in one assertion, which is the clearest statement of what a push does.
    /// Player is split around Gallery, CITE and Steamfitter because an application's url can name their
    /// ids.
    /// </remarks>
    [Fact]
    public async Task Push_PushesTheFourApplicationsInOrder()
    {
        var msel = await SeedPushableMsel();
        var harness = Harness();

        await harness.PushAsync(msel.Id);
        await harness.WaitForDeployment(msel.Id, Ct);

        Assert.Equal(
            [
                "Pushing View to Player",
                "Pushing Teams to Player",
                "Pushing Collection to Gallery",
                "Pushing Exhibit to Gallery",
                "Pushing Teams to Gallery",
                "Pushing Exhibit Memberships to Gallery",
                "Pushing Cards to Gallery",
                "Pushing Articles to Gallery",
                "Pushing Evaluation to CITE",
                "Pushing Moves to CITE",
                "Pushing Teams to CITE",
                "Pushing Evaluation Memberships to CITE",
                "Pushing Duties to CITE",
                "Pushing Actions to CITE",
                "Finishing Evaluation to CITE",
                "Pushing Scenario to Steamfitter",
                "Pushing Scenario Memberships to Steamfitter",
                "Pushing Player Applications"
            ],
            Statuses(harness, msel.Id));
    }

    [Fact]
    public async Task Push_MarksTheMselDeployedAndClearsTheIntegrationStatus()
    {
        var msel = await SeedPushableMsel();
        var harness = Harness();

        await harness.PushAsync(msel.Id);

        var deployed = await harness.WaitForDeployment(msel.Id, Ct);

        Assert.Equal(MselItemStatus.Deployed, deployed.Status);
        Assert.Null(deployed.IntegrationStatus);
    }

    /// <remarks>
    /// Before anything else, and saved - the comment says why: so that a failure part-way through leaves ids
    /// a cancel can clean up. <c>Pushing Integrations</c> is written by <c>SaveChangesAsync</c> rather than
    /// the <c>ExecuteUpdateAsync</c> the per-step statuses use, so it is the one status that also raises a
    /// <c>MselUpdated</c>.
    /// </remarks>
    [Fact]
    public async Task Push_PreGeneratesAnIdForEachApplicationItWillUse()
    {
        var msel = await SeedPushableMsel();
        var harness = Harness();

        await harness.PushAsync(msel.Id);

        var deployed = await harness.WaitForDeployment(msel.Id, Ct);

        Assert.NotNull(deployed.PlayerViewId);
        Assert.NotNull(deployed.GalleryCollectionId);
        Assert.NotNull(deployed.GalleryExhibitId);
        Assert.NotNull(deployed.CiteEvaluationId);
        Assert.NotNull(deployed.SteamfitterScenarioId);
    }

    [Fact]
    public async Task Push_KeepsAnIntegrationIdTheMselAlreadyHas()
    {
        var existing = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");
        var msel = await SeedPushableMsel(x => x.CiteEvaluationId = existing);
        var harness = Harness();

        await harness.PushAsync(msel.Id);

        var deployed = await harness.WaitForDeployment(msel.Id, Ct);

        Assert.Equal(existing, deployed.CiteEvaluationId);
    }

    /// <remarks>
    /// The queue item carries a <c>PlayerViewId</c> so that re-pushing a MSEL can reuse the view somebody is
    /// already looking at. It reaches <c>CreateViewAsync</c>, which prefers it over the MSEL's own - and
    /// note the MSEL's own is <em>not</em> updated to match, so the two disagree for the rest of the push.
    /// </remarks>
    [Fact]
    public async Task Push_UsesThePlayerViewIdFromTheQueueItemWhenGivenOne()
    {
        var fromTheQueue = Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee");
        var msel = await SeedPushableMsel();
        var harness = Harness();

        await harness.PushAsync(msel.Id, playerViewId: fromTheQueue);

        var deployed = await harness.WaitForDeployment(msel.Id, Ct);
        var view = Body(harness.Handler.Sent.First(x => x.Path == "player/api/views").Body);

        Assert.Equal(fromTheQueue.ToString(), view["id"].GetString());
        Assert.NotEqual(fromTheQueue, deployed.PlayerViewId);
    }

    // ---------------------------------------------------------------------------------------------
    // The Use* flags
    // ---------------------------------------------------------------------------------------------

    /// <remarks>
    /// Each flag gates the id, the client and the whole per-application block, so an application a MSEL does
    /// not use is not contacted at all - not even for its user list.
    /// </remarks>
    [Fact]
    public async Task Push_ForAnApplicationItDoesNotUse_GeneratesNoIdAndContactsItNotAtAll()
    {
        var msel = await SeedPushableMsel(x =>
        {
            x.UseGallery = false;
            x.UseCite = false;
        });

        var harness = Harness();

        await harness.PushAsync(msel.Id);

        var deployed = await harness.WaitForDeployment(msel.Id, Ct);

        Assert.Null(deployed.GalleryCollectionId);
        Assert.Null(deployed.GalleryExhibitId);
        Assert.Null(deployed.CiteEvaluationId);
        Assert.DoesNotContain(harness.Handler.Paths, x => x.StartsWith("gallery/"));
        Assert.DoesNotContain(harness.Handler.Paths, x => x.StartsWith("cite/"));
        Assert.Contains(harness.Handler.Paths, x => x.StartsWith("player/"));
        Assert.Contains(harness.Handler.Paths, x => x.StartsWith("steamfitter/"));
    }

    /// <remarks>
    /// <para>
    /// No ids, no clients, no user lists, nothing contacted - and the MSEL is <c>Deployed</c>. So the status
    /// says a deployment exists when nothing anywhere does, and the only clue is that every integration id
    /// is null.
    /// </para>
    /// <para>
    /// Refusing a push with no integrations selected - which is what <c>CanMselBePushed</c> looks like it was
    /// meant for, being a method that returns <c>true</c> behind a <c>// TODO: build this out!!!</c> and is
    /// called from nowhere - turns this test red.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Push_ForAMselUsingNothing_DeploysWithoutContactingAnything()
    {
        var msel = await SeedPushableMsel(x =>
        {
            x.UsePlayer = false;
            x.UseGallery = false;
            x.UseCite = false;
            x.UseSteamfitter = false;
        });

        var harness = Harness();

        await harness.PushAsync(msel.Id);

        var deployed = await harness.WaitForDeployment(msel.Id, Ct);

        Assert.Equal(MselItemStatus.Deployed, deployed.Status);
        Assert.Null(deployed.PlayerViewId);
        Assert.Null(deployed.SteamfitterScenarioId);
        Assert.DoesNotContain(harness.Handler.Paths, x => x.StartsWith("player/") || x.StartsWith("cite/"));
    }

    /// <remarks>
    /// Three lists, fetched concurrently before any application is pushed, so that
    /// <c>CreateTeamsAsync</c> knows which users each already has. Steamfitter is not asked, because its
    /// memberships are created against user ids without creating the users first.
    /// </remarks>
    [Fact]
    public async Task Push_FetchesTheUserListFromEveryApplicationThatNeedsOne()
    {
        var msel = await SeedPushableMsel();
        var harness = Harness();

        await harness.PushAsync(msel.Id);
        await harness.WaitForDeployment(msel.Id, Ct);

        var lists = harness.Handler.Sent
            .Where(x => x.Method == System.Net.Http.HttpMethod.Get && x.Path.EndsWith("api/users"))
            .Select(x => x.Path)
            .OrderBy(x => x)
            .ToList();

        Assert.Equal(["cite/api/users", "gallery/api/users", "player/api/users"], lists);
    }

    /// <remarks>
    /// <para>
    /// This is why Player is split in two. An application's url can name the CITE evaluation and the Gallery
    /// exhibit, and <c>CreateApplicationsAsync</c> substitutes them - so it has to run after those two have
    /// been pushed, which is why the view and its teams go first and the applications go last.
    /// </para>
    /// <para>
    /// The ids substituted are the ones this push generated, which is the whole point: the application in
    /// Player links to the evaluation and exhibit created alongside it.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Push_CreatesPlayerApplicationsLastSoTheirUrlsCanNameTheOtherIntegrations()
    {
        var msel = await SeedPushableMsel();
        var harness = Harness();

        await harness.PushAsync(msel.Id);

        var deployed = await harness.WaitForDeployment(msel.Id, Ct);
        var paths = harness.Handler.Paths.ToList();
        var application = harness.Handler.Sent.First(x => x.Path.EndsWith("/applications"));

        Assert.Equal(
            $"http://application.example/?cite={deployed.CiteEvaluationId}&exhibit={deployed.GalleryExhibitId}",
            Body(application.Body)["url"].GetString());

        Assert.True(
            paths.IndexOf(application.Path) > paths.IndexOf("cite/api/evaluations"),
            "the applications should be created after the CITE evaluation their urls name");
        Assert.True(
            paths.IndexOf(application.Path) > paths.IndexOf("steamfitter/api/scenarios"),
            "the applications should be created last of all");
    }

    // ---------------------------------------------------------------------------------------------
    // The move number every application is told
    // ---------------------------------------------------------------------------------------------

    /// <remarks>
    /// <para>
    /// The defect in this class's remarks. The MSEL's two moves are numbered 1 and 2; the scenario event sits
    /// in the first of them. <c>GetMovesAndGroups</c> hands <c>IntegrationService</c> the move's
    /// <em>index</em>, so the task is named from 0.
    /// </para>
    /// <para>
    /// Using the <c>moveNumber</c> local that line 784 already computes turns this test red and names the
    /// task <c>01-00</c>. Note the padding is the other defect recorded in
    /// <c>IntegrationSteamfitterExtensionsTests</c>: whatever number arrives is formatted by string
    /// arithmetic that wraps at a hundred.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Push_NamesSteamfitterTasksFromTheMoveIndexRatherThanTheMoveNumber()
    {
        var msel = await SeedPushableMsel();
        var harness = Harness();

        await harness.PushAsync(msel.Id);
        await harness.WaitForDeployment(msel.Id, Ct);

        var names = harness.Handler.Sent
            .Where(x => x.Path == "steamfitter/api/tasks")
            .Select(x => Body(x.Body)["name"].GetString())
            .ToList();

        Assert.Contains("00-00 notify", names);
        Assert.DoesNotContain("01-00 notify", names);
    }

    /// <remarks>
    /// The same index reaches the urls the move-change tasks will call, so the tasks blueprint creates tell
    /// CITE to advance to move 0 and Gallery to inject 0 of move 0 - for a MSEL whose first move is
    /// numbered 1. A CITE evaluation that has no move 0 - and blueprint deletes the one CITE creates - has
    /// nothing to advance to.
    /// </remarks>
    [Fact]
    public async Task Push_TellsCiteAndGalleryToAdvanceToTheMoveIndexRatherThanTheMoveNumber()
    {
        var msel = await SeedPushableMsel();
        var harness = Harness();

        await harness.PushAsync(msel.Id);
        var deployed = await harness.WaitForDeployment(msel.Id, Ct);

        var urls = harness.Handler.Sent
            .Where(x => x.Path == "steamfitter/api/tasks")
            .Select(x => Body(x.Body)["actionParameters"])
            .Where(x => x.ValueKind == JsonValueKind.Object && x.TryGetProperty("Url", out _))
            .Select(x => x.GetProperty("Url").GetString())
            .ToList();

        Assert.Contains(
            $"{IntegrationServiceHarness.CiteApiUrl}api/evaluations/{deployed.CiteEvaluationId}/move/0",
            urls);
        Assert.Contains(
            $"{IntegrationServiceHarness.GalleryApiUrl}api/exhibits/{deployed.GalleryExhibitId}/move/0/inject/0",
            urls);
    }

    // ---------------------------------------------------------------------------------------------
    // What a failure leaves behind
    // ---------------------------------------------------------------------------------------------

    /// <remarks>
    /// <para>
    /// The status names the <em>call site</em>, not the step that failed. Each of the four process methods
    /// tracks its own <c>currentProcessStep</c> in fine detail - <c>"Gallery - Pushing Collection"</c> -
    /// logs it, and rethrows; but that variable is a local, so the outer handler writes the much coarser
    /// value it last set before making the call. The detail reaches the log and never reaches the user.
    /// </para>
    /// <para>
    /// Passing the inner step out, or catching where it is known, turns this test red and would tell an
    /// operator which of Gallery's seven steps failed rather than that Gallery was started.
    /// </para>
    /// <para>
    /// Gallery is chosen here because it runs second: Player has already been pushed when it fails, so the
    /// next test can show what is left pointing at what.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Push_WhenAnApplicationFails_NamesTheCallSiteRatherThanTheStepThatFailed()
    {
        var msel = await SeedPushableMsel();
        var harness = Harness(x => x.AnswersJson("gallery/api/collections", "no", HttpStatusCode.InternalServerError));

        await harness.PushAsync(msel.Id);

        var failed = await harness.WaitForError(msel.Id, Ct);

        Assert.StartsWith("ERROR: Gallery - start GalleryProcess failed", failed.IntegrationStatus);
        Assert.DoesNotContain("Pushing Collection", failed.IntegrationStatus);
        Assert.NotEqual(MselItemStatus.Deployed, failed.Status);
    }

    /// <remarks>
    /// <para>
    /// The integration ids were written before the first request, so they survive - which is what lets a
    /// cancel clean up afterwards, and is deliberate. What is missing is anything that triggers one: the MSEL
    /// is left saying <c>Pushing Integrations</c>, holding four ids, with a Player view that exists and a
    /// Gallery collection that does not.
    /// </para>
    /// <para>
    /// Note the status is the <c>Pushing</c> the pre-generation step wrote, not something the failure chose.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Push_WhenAnApplicationFails_LeavesEveryIntegrationIdInPlace()
    {
        var msel = await SeedPushableMsel();
        var harness = Harness(x => x.AnswersJson("gallery/api/collections", "no", HttpStatusCode.InternalServerError));

        await harness.PushAsync(msel.Id);

        var failed = await harness.WaitForError(msel.Id, Ct);

        Assert.NotNull(failed.PlayerViewId);
        Assert.NotNull(failed.GalleryCollectionId);
        Assert.NotNull(failed.CiteEvaluationId);
        Assert.NotNull(failed.SteamfitterScenarioId);
        Assert.Contains("player/api/views", harness.Handler.Paths);
    }

    /// <remarks>
    /// <para>
    /// The failure stops the push where it stood, so nothing is <em>created</em> in CITE or Steamfitter -
    /// there is no partial-success path, and one application's refusal abandons the rest.
    /// </para>
    /// <para>
    /// But CITE has already been read: the three user lists are fetched up front, before any application is
    /// pushed. So a push that dies at Gallery has still asked Player and CITE who their users are, which is
    /// three round trips spent and two applications told that something was attempted.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Push_WhenAnApplicationFails_CreatesNothingInALaterApplication()
    {
        var msel = await SeedPushableMsel();
        var harness = Harness(x => x.AnswersJson("gallery/api/collections", "no", HttpStatusCode.InternalServerError));

        await harness.PushAsync(msel.Id);
        await harness.WaitForError(msel.Id, Ct);

        var writes = harness.Handler.Sent
            .Where(x => x.Method != System.Net.Http.HttpMethod.Get)
            .Select(x => x.Path)
            .ToList();

        Assert.DoesNotContain(writes, x => x.StartsWith("cite/"));
        Assert.DoesNotContain(writes, x => x.StartsWith("steamfitter/"));
        Assert.Contains("cite/api/users", harness.Handler.Paths);
    }

    /// <remarks>
    /// The error status goes to the MSEL's group and to the administrators' group, like every other status.
    /// </remarks>
    [Fact]
    public async Task Push_WhenAnApplicationFails_BroadcastsTheError()
    {
        var msel = await SeedPushableMsel();
        var harness = Harness(x => x.AnswersJson("gallery/api/collections", "no", HttpStatusCode.InternalServerError));

        await harness.PushAsync(msel.Id);
        await harness.WaitForError(msel.Id, Ct);

        var errors = harness.Hub.Of(MainHubMethods.IntegrationStatusUpdated)
            .Where(x => x.Args[1] is string status && status.StartsWith("ERROR"))
            .Select(x => x.Group)
            .ToList();

        Assert.Contains(msel.Id.ToString(), errors);
        Assert.Contains(MainHub.ADMIN_DATA_GROUP, errors);
    }

    // ---------------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------------

    private static readonly Guid StubId = Guid.Parse("99999999-9999-9999-9999-999999999999");

    /// <summary>
    /// A MSEL ready to push: all four integrations on, two moves numbered 1 and 2, one team with one user,
    /// and one scenario event carrying a Steamfitter notification.
    /// </summary>
    /// <remarks>
    /// The moves are numbered 1 and 2 rather than 0 and 1 on purpose - it is the only way to tell a move's
    /// number from its index, which is what two of these tests are about.
    /// </remarks>
    private async Task<MselEntity> SeedPushableMsel(Action<MselEntity> adjust = null)
    {
        var msel = BlueprintAppFactory.Msel(status: MselItemStatus.Approved);

        msel.UsePlayer = true;
        msel.UseGallery = true;
        msel.UseCite = true;
        msel.UseSteamfitter = true;
        msel.CiteScoringModelId = Guid.NewGuid();
        msel.StartTime = DateTime.UtcNow.AddDays(1);
        msel.DurationSeconds = 3600;

        adjust?.Invoke(msel);

        await Seed(msel);
        await Seed(
            BlueprintAppFactory.Move(msel.Id, moveNumber: 1, deltaSeconds: 0),
            BlueprintAppFactory.Move(msel.Id, moveNumber: 2, deltaSeconds: 100));

        var team = BlueprintAppFactory.Team(msel.Id);
        team.CiteTeamTypeId = Guid.NewGuid();
        await Seed(team);
        await Actor().WithName("Ada").OnTeam(team).SeedAsync();

        await Seed(BlueprintAppFactory.PlayerApplication(
            msel.Id,
            name: "Console",
            url: "http://application.example/?cite={citeEvaluationId}&exhibit={galleryExhibitId}"));

        ScenarioEventId = Guid.NewGuid();

        var scenarioEvent = BlueprintAppFactory.ScenarioEvent(msel.Id, deltaSeconds: 0);
        scenarioEvent.Id = ScenarioEventId;
        scenarioEvent.IntegrationTarget = "Gallery,Steamfitter";
        await Seed(scenarioEvent);
        await Seed(BlueprintAppFactory.SteamfitterTask(
            scenarioEvent.Id, "notify", actionParameters: new() { ["notificationText"] = "stand by" }));

        return msel;
    }

    /// <summary>The scenario event the current test seeded, so the move service can answer for it.</summary>
    private Guid ScenarioEventId { get; set; }

    /// <remarks>
    /// <paramref name="first"/> is applied before the general rules, because rules are matched in the order
    /// they were added: a test that wants one route to fail has to register that route before the rule that
    /// would otherwise answer it.
    /// </remarks>
    private IntegrationServiceHarness Harness(Func<TestHttpHandler, TestHttpHandler> first = null)
    {
        var handler = Everything(first);

        var moves = Substitute.For<IScenarioEventService>();
        moves.GetMovesAndInjects(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, int[]> { [ScenarioEventId] = [1, 0] });

        return new IntegrationServiceHarness(Session, handler, moves);
    }

    private static IEnumerable<string> Statuses(IntegrationServiceHarness harness, Guid mselId) =>
        harness.Hub.Of(MainHubMethods.IntegrationStatusUpdated)
            .Where(x => x.Group == mselId.ToString())
            .Select(x => (string)x.Args[1]);

    private static Dictionary<string, JsonElement> Body(string body) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(body);

    /// <summary>
    /// Every request a full push makes, answered. Written once because a push touches thirty-odd routes
    /// across four applications, and a test that had to stub them itself would say nothing about what it was
    /// for.
    /// </summary>
    /// <remarks>
    /// Ordered most specific first: rules are matched in the order they were added, so
    /// <c>cite/api/evaluations/*/memberships</c> has to precede <c>POST cite/api/evaluations</c>. The
    /// distinct path prefix per application is what makes <c>api/users</c> unambiguous - see
    /// <see cref="IntegrationServiceHarness"/>.
    /// </remarks>
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

            .AnswersJson("GET player/api/users", "[]")
            .AnswersJson("GET gallery/api/users", "[]")
            .AnswersJson("GET cite/api/users", "[]")

            .AnswersJson("player/api/views/*/teams", $$"""{"id":"{{StubId}}","name":"t"}""", HttpStatusCode.Created)
            .AnswersJson("player/api/views/*/applications", $$"""{"id":"{{StubId}}","name":"a","viewId":"{{StubId}}"}""", HttpStatusCode.Created)
            .AnswersJson("player/api/views", $$"""{"id":"{{StubId}}","name":"v","status":"Active"}""", HttpStatusCode.Created)
            .AnswersJson("player/api/teams/*/application-instances", $$"""{"id":"{{StubId}}"}""", HttpStatusCode.Created)
            .AnswersJson("player/api/teams/*", $$"""{"id":"{{StubId}}","name":"u"}""")
            .AnswersJson("POST player/api/users", $$"""{"id":"{{StubId}}","name":"u"}""", HttpStatusCode.Created)

            .AnswersJson("gallery/api/exhibit-roles", $$"""[{"id":"{{StubId}}","name":"Observer"}]""")
            .AnswersJson("gallery/api/exhibits/*/memberships", $$"""{"id":"{{StubId}}"}""", HttpStatusCode.Created)
            .AnswersJson("gallery/api/collections", $$"""{"id":"{{StubId}}","name":"c"}""", HttpStatusCode.Created)
            .AnswersJson("gallery/api/exhibits", $$"""{"id":"{{StubId}}","collectionId":"{{StubId}}"}""", HttpStatusCode.Created)
            .AnswersJson("gallery/api/teamusers", $$"""{"id":"{{StubId}}"}""", HttpStatusCode.Created)
            .AnswersJson("gallery/api/teamcards", $$"""{"id":"{{StubId}}"}""", HttpStatusCode.Created)
            .AnswersJson("gallery/api/teamarticles", $$"""{"id":"{{StubId}}"}""", HttpStatusCode.Created)
            .AnswersJson("gallery/api/teams", $$"""{"id":"{{StubId}}","name":"t"}""", HttpStatusCode.Created)
            .AnswersJson("gallery/api/cards", $$"""{"id":"{{StubId}}","name":"card"}""", HttpStatusCode.Created)
            .AnswersJson("gallery/api/articles", $$"""{"id":"{{StubId}}","name":"a"}""", HttpStatusCode.Created)
            .AnswersJson("POST gallery/api/users", $$"""{"id":"{{StubId}}","name":"u"}""", HttpStatusCode.Created)

            .AnswersJson("cite/api/team-roles", $$"""[{"id":"{{StubId}}","name":"Inviter"}]""")
            .AnswersJson("cite/api/evaluation-roles", $$"""[{"id":"{{StubId}}","name":"Observer"}]""")
            .AnswersJson("cite/api/evaluations/*/memberships", $$"""{"id":"{{StubId}}"}""", HttpStatusCode.Created)
            .AnswersJson("POST cite/api/evaluations",
                $$"""{"id":"{{StubId}}","description":"e","moves":[{"id":"{{StubId}}","moveNumber":0}]}""",
                HttpStatusCode.Created)
            .AnswersJson("PUT cite/api/evaluations/*", $$"""{"id":"{{StubId}}","description":"e"}""")
            .Answers("DELETE cite/api/moves/*", HttpStatusCode.NoContent)
            .AnswersJson("cite/api/moves", $$"""{"id":"{{StubId}}","moveNumber":1}""", HttpStatusCode.Created)
            .AnswersJson("cite/api/teams/*/memberships", $$"""{"id":"{{StubId}}"}""", HttpStatusCode.Created)
            .AnswersJson("cite/api/teams", $$"""{"id":"{{StubId}}","name":"t"}""", HttpStatusCode.Created)
            .AnswersJson("cite/api/duties", $$"""{"id":"{{StubId}}","name":"d"}""", HttpStatusCode.Created)
            .AnswersJson("cite/api/actions", $$"""{"id":"{{StubId}}"}""", HttpStatusCode.Created)
            .AnswersJson("POST cite/api/users", $$"""{"id":"{{StubId}}","name":"u"}""", HttpStatusCode.Created)

            .AnswersJson("steamfitter/api/scenario-roles", $$"""[{"id":"{{StubId}}","name":"Manager"}]""")
            .AnswersJson("steamfitter/api/scenarios/*/memberships", $$"""{"id":"{{StubId}}"}""", HttpStatusCode.Created)
            .AnswersJson("steamfitter/api/scenarios", $$"""{"id":"{{StubId}}","name":"s","status":"active"}""", HttpStatusCode.Created)
            .AnswersJson("steamfitter/api/tasks", $$"""{"id":"{{StubId}}","name":"t"}""", HttpStatusCode.Created);
}
