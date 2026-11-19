// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Blueprint.Api.Data.Enumerations;
using Blueprint.Api.Data.Models;
using Blueprint.Api.Hubs;
using Blueprint.Api.Services;
using Blueprint.Api.Tests.Support;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Xunit;

namespace Blueprint.Api.Tests.Services;

/// <summary><c>IntegrationService</c>'s push - the orchestration that turns a MSEL into a Player view, a
/// Gallery collection and a CITE evaluation, and starts its deployment.</summary>
public class IntegrationServicePushTests(DatabaseFixture fixture) : DatabaseTestBase(fixture)
{
    /// <summary>Starts describing an actor to seed over this test's database.</summary>
    private TestActorBuilder Actor() => new(Db, Ct);

    // ---------------------------------------------------------------------------------------------
    // The order, and the progress a user sees
    // ---------------------------------------------------------------------------------------------

    /// <remarks>
    /// The whole progress sequence in one assertion, which is the clearest statement of what a push does.
    /// Player is split around Gallery and CITE because an application's url can name their ids.
    /// </remarks>
    [Fact]
    public async Task Push_PushesTheThreeApplicationsInOrder()
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

    /// <summary>The push generates and saves an id for each application it will use before contacting
    /// any.</summary>
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
    }

    /// <summary>
    /// A push that deploys the MSEL starts a deployment, which its automation runs are kept under, at its first move:
    /// where the push leaves CITE and Gallery.
    /// </summary>
    [Fact]
    public async Task Push_StartsADeploymentAtTheFirstMove()
    {
        var msel = await SeedPushableMsel();
        var harness = Harness();

        await harness.PushAsync(msel.Id);

        var deployed = await harness.WaitForDeployment(msel.Id, Ct);

        Assert.NotNull(deployed.DeploymentId);
        Assert.Equal(1, deployed.CurrentMoveNumber);
        Assert.Equal(0, deployed.CurrentGroupNumber);
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

    /// <summary>A view id on the queue item is used for the Player view instead of the MSEL's own.</summary>
    /// <remarks>
    /// The queue item carries a <c>PlayerViewId</c> so that re-pushing a MSEL can reuse the view somebody is
    /// already looking at. The view is created under it, so the MSEL takes it too, and the teams go in that
    /// view rather than in one under an ID the MSEL made up.
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
        Assert.Equal(fromTheQueue, deployed.PlayerViewId);
        Assert.Contains($"player/api/views/{fromTheQueue}/teams", harness.Handler.Paths);
        Assert.DoesNotContain(harness.Handler.Paths, p => p.StartsWith("player/api/views/") && p.EndsWith("/teams") && !p.Contains(fromTheQueue.ToString()));
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
    }

    /// <summary>A push of a MSEL using no integration contacts nothing and marks the MSEL deployed.</summary>
    [Fact]
    public async Task Push_ForAMselUsingNothing_DeploysWithoutContactingAnything()
    {
        var msel = await SeedPushableMsel(x =>
        {
            x.UsePlayer = false;
            x.UseGallery = false;
            x.UseCite = false;
        });

        var harness = Harness();

        await harness.PushAsync(msel.Id);

        var deployed = await harness.WaitForDeployment(msel.Id, Ct);

        Assert.Equal(MselItemStatus.Deployed, deployed.Status);
        Assert.Null(deployed.PlayerViewId);
        Assert.DoesNotContain(harness.Handler.Paths, x => x.StartsWith("player/") || x.StartsWith("cite/"));
    }

    /// <remarks>
    /// Three lists, fetched concurrently before any application is pushed, so that
    /// <c>CreateTeamsAsync</c> knows which users each already has.
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
            paths.IndexOf(application.Path) > paths.IndexOf("gallery/api/articles"),
            "the applications should be created after the Gallery articles");
    }

    /// <summary>The exhibit is created at move 0, inject 0, and is not updated again by the push.</summary>
    [Fact]
    public async Task Push_LeavesTheExhibitAsCreated()
    {
        var msel = await SeedPushableMsel();
        var harness = Harness();

        await harness.PushAsync(msel.Id);
        await harness.WaitForDeployment(msel.Id, Ct);

        Assert.Contains(harness.Handler.Sent, x => x.Method == HttpMethod.Post && x.Path == "gallery/api/exhibits");
        Assert.DoesNotContain(harness.Handler.Sent, x => x.Method == HttpMethod.Put && x.Path.StartsWith("gallery/api/exhibits/"));
    }

    /// <summary>The push sends the MSEL's own applications. A template names no MSEL, so it is never one of
    /// them and never reaches Player.</summary>
    [Fact]
    public async Task Push_SendsTheMselsApplicationsAndNoTemplate()
    {
        var msel = await SeedPushableMsel();
        await Seed(TestData.PlayerApplicationTemplate("A template"));
        var harness = Harness();

        await harness.PushAsync(msel.Id);
        await harness.WaitForDeployment(msel.Id, Ct);

        var application = Assert.Single(harness.Handler.Sent, x => x.Path.EndsWith("/applications"));
        Assert.Equal("Console", Body(application.Body)["name"].GetString());
    }

    /// <summary>A MSEL team named Admin takes the place of Player's own: the view is created without one, and
    /// that team alone gets the role Player gives its Admin team.</summary>
    [Fact]
    public async Task Push_OfAMselWithAnAdminTeam_GivesItTheViewAdminRoleInsteadOfPlayersOwnTeam()
    {
        var viewAdmin = Guid.NewGuid();
        var msel = await SeedPushableMsel();
        var admin = TestData.Team(msel.Id);
        admin.Name = "Admin";
        await Seed(admin);
        var harness = Harness(x => x.AnswersJson(
            "player/api/team-roles", $$"""[{"id":"{{viewAdmin}}","name":"View Admin"}]"""));

        await harness.PushAsync(msel.Id);
        await harness.WaitForDeployment(msel.Id, Ct);

        var view = Body(harness.Handler.Sent.Single(x => x.Path == "player/api/views").Body);
        var adminTeam = harness.Handler.Sent
            .Where(x => x.Path.StartsWith("player/api/views/") && x.Path.EndsWith("/teams"))
            .Select(x => Body(x.Body))
            .Single(x => x["id"].GetString() == admin.Id.ToString());

        Assert.False(view["createAdminTeam"].GetBoolean());
        Assert.Equal(viewAdmin.ToString(), adminTeam["roleId"].GetString());
    }

    /// <summary>A Player without the View Admin role stops the push before a view is created.</summary>
    [Fact]
    public async Task Push_OfAMselWithAnAdminTeam_WhenPlayerHasNoViewAdminRole_CreatesNoView()
    {
        var msel = await SeedPushableMsel();
        var admin = TestData.Team(msel.Id);
        admin.Name = "Admin";
        await Seed(admin);
        var harness = Harness(x => x.AnswersJson(
            "player/api/team-roles", $$"""[{"id":"{{Guid.NewGuid()}}","name":"View Member"}]"""));

        await harness.PushAsync(msel.Id);

        var failed = await harness.WaitForError(msel.Id, Ct);

        Assert.Contains("Player has no \"View Admin\" team role to give the MSEL's Admin team.", failed.IntegrationStatus);
        Assert.DoesNotContain("player/api/views", harness.Handler.Paths);
    }

    // ---------------------------------------------------------------------------------------------
    // The move number every application is told
    // ---------------------------------------------------------------------------------------------

    /// <summary>The Gallery article carries the move and inject the real move service computes from the timeline.</summary>
    [Fact]
    public async Task Push_SendsGalleryTheArticlesMoveAndInjectFromTheTimeline()
    {
        var msel = await SeedPushableMsel();
        var harness = Harness();

        await harness.PushAsync(msel.Id);
        await harness.WaitForDeployment(msel.Id, Ct);

        var article = Body(harness.Handler.Sent.Single(x => x.Path == "gallery/api/articles").Body);
        Assert.Equal(1, Number(article, "move"));
        Assert.Equal(0, Number(article, "inject"));
    }

    /// <summary>The article's status goes to Gallery under its color name, a status cell holding one of
    /// Gallery's earlier names included.</summary>
    [Theory]
    [InlineData("Red", "Red")]
    [InlineData("critical", "Orange")]
    [InlineData("3", "Gray")]
    public async Task Push_SendsGalleryTheArticlesStatusUnderItsColorName(string cell, string expected)
    {
        var msel = await SeedPushableMsel();
        var scenarioEvent = await ReadBack(rb => rb.ScenarioEvents.SingleAsync(x => x.MselId == msel.Id, Ct));
        var status = TestData.DataField(mselId: msel.Id, displayOrder: 2);
        status.GalleryArticleParameter = GalleryArticleParameter.Status.ToString();
        await Seed(status);
        await Seed(TestData.DataValue(status.Id, scenarioEvent.Id, cell));
        var harness = Harness();

        await harness.PushAsync(msel.Id);
        await harness.WaitForDeployment(msel.Id, Ct);

        var article = Body(harness.Handler.Sent.Single(x => x.Path == "gallery/api/articles").Body);
        Assert.Equal(expected, article["status"].GetString());
    }

    // ---------------------------------------------------------------------------------------------
    // What a failure leaves behind
    // ---------------------------------------------------------------------------------------------

    /// <summary>Push when an application fails names the call site.</summary>
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
        Assert.Contains("player/api/views", harness.Handler.Paths);
    }

    /// <remarks>
    /// <para>
    /// The failure stops the push where it stood, so nothing is <em>created</em> in CITE -
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

        var errors = harness.Hub.Recipients(MainHubMethods.IntegrationStatusUpdated, msel.Id)
            .Where(group => harness.Hub.ToGroup(group)
                .Any(x => x.Arguments[1] is string status && status.StartsWith("ERROR")))
            .ToList();

        Assert.Contains(msel.Id.ToString(), errors);
        Assert.Contains(MainHub.ADMIN_DATA_GROUP, errors);
    }

    // ---------------------------------------------------------------------------------------------
    // A MSEL's own scoring model
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// A MSEL with its own scoring model creates it in CITE, makes the evaluation from it, then deletes it: CITE
    /// copied it into the evaluation, and the pull deletes that copy with the evaluation.
    /// </summary>
    [Fact]
    public async Task Push_OfAMselWithItsOwnScoringModel_CreatesItForTheEvaluationThenDeletesIt()
    {
        var msel = await SeedPushableMsel(m => m.CiteScoringModelId = null);
        await Seed(TestData.MselScoringModel(msel.Id, "Harbor Questions"));
        var harness = Harness(ScoringModels);

        await harness.PushAsync(msel.Id);
        await harness.WaitForDeployment(msel.Id, Ct);

        var cite = harness.Handler.Sent.Where(x => x.Path.StartsWith("cite/api/scoringmodels") || x.Path == "cite/api/evaluations").ToList();
        Assert.Equal(
            [
                (System.Net.Http.HttpMethod.Post, "cite/api/scoringmodels"),
                (System.Net.Http.HttpMethod.Post, "cite/api/evaluations"),
                (System.Net.Http.HttpMethod.Delete, $"cite/api/scoringmodels/{UploadedScoringModelId}")
            ],
            cite.Select(x => (x.Method, x.Path)));
        Assert.Equal("Harbor Questions", Body(cite[0].Body)["description"].GetString());
        Assert.Equal(UploadedScoringModelId.ToString(), Body(cite[1].Body)["scoringModelId"].GetString());
        Assert.Contains("Pushing Scoring Model to CITE", Statuses(harness, msel.Id));
    }

    /// <summary>A linked model is pushed as before: nothing is created in CITE or deleted from it.</summary>
    [Fact]
    public async Task Push_OfAMselLinkingACiteScoringModel_CreatesNoScoringModel()
    {
        var msel = await SeedPushableMsel();
        var harness = Harness(ScoringModels);

        await harness.PushAsync(msel.Id);
        await harness.WaitForDeployment(msel.Id, Ct);

        Assert.DoesNotContain(harness.Handler.Paths, x => x.StartsWith("cite/api/scoringmodels"));
        var evaluation = harness.Handler.Sent.Single(x => x.Path == "cite/api/evaluations");
        Assert.Equal(msel.CiteScoringModelId.ToString(), Body(evaluation.Body)["scoringModelId"].GetString());
        Assert.DoesNotContain("Pushing Scoring Model to CITE", Statuses(harness, msel.Id));
    }

    /// <summary>A model CITE will not delete is left there unused, which does not fail the push.</summary>
    [Fact]
    public async Task Push_WhenCiteRefusesToDeleteTheModel_StillDeploys()
    {
        var msel = await SeedPushableMsel(m => m.CiteScoringModelId = null);
        await Seed(TestData.MselScoringModel(msel.Id));
        var harness = Harness(x => ScoringModels(x.Answers($"DELETE cite/api/scoringmodels/{UploadedScoringModelId}", HttpStatusCode.Forbidden)));

        await harness.PushAsync(msel.Id);

        var deployed = await harness.WaitForDeployment(msel.Id, Ct);
        Assert.Equal(MselItemStatus.Deployed, deployed.Status);
    }

    /// <summary>An evaluation CITE refuses fails the push, and the model made for it is deleted all the same.</summary>
    [Fact]
    public async Task Push_WhenTheEvaluationFails_DeletesTheModelMadeForIt()
    {
        var msel = await SeedPushableMsel(m => m.CiteScoringModelId = null);
        await Seed(TestData.MselScoringModel(msel.Id));
        var harness = Harness(x => ScoringModels(x.AnswersJson("POST cite/api/evaluations", "no", HttpStatusCode.InternalServerError)));

        await harness.PushAsync(msel.Id);

        var failed = await harness.WaitForError(msel.Id, Ct);
        Assert.StartsWith("ERROR: CITE - begin processing failed", failed.IntegrationStatus);
        Assert.Contains(harness.Handler.Sent, x => x.Method == System.Net.Http.HttpMethod.Delete && x.Path == $"cite/api/scoringmodels/{UploadedScoringModelId}");
    }

    /// <summary>A model CITE refuses to create fails the push before an evaluation is asked for.</summary>
    [Fact]
    public async Task Push_WhenCiteRefusesTheModel_CreatesNoEvaluation()
    {
        var msel = await SeedPushableMsel(m => m.CiteScoringModelId = null);
        await Seed(TestData.MselScoringModel(msel.Id));
        var harness = Harness(x => x.AnswersJson("POST cite/api/scoringmodels", "no", HttpStatusCode.Forbidden));

        await harness.PushAsync(msel.Id);

        await harness.WaitForError(msel.Id, Ct);
        Assert.DoesNotContain(harness.Handler.Sent, x => x.Method != System.Net.Http.HttpMethod.Get && x.Path == "cite/api/evaluations");
    }

    private static readonly Guid UploadedScoringModelId = Guid.Parse("12121212-1212-1212-1212-121212121212");

    /// <summary>CITE creating a scoring model, and deleting it.</summary>
    private static SiblingApiHandler ScoringModels(SiblingApiHandler handler) =>
        handler
            .AnswersJson("POST cite/api/scoringmodels", $$"""{"id":"{{UploadedScoringModelId}}","description":"m"}""", HttpStatusCode.Created)
            .Answers($"DELETE cite/api/scoringmodels/{UploadedScoringModelId}", HttpStatusCode.NoContent);
    // ---------------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------------

    private static readonly Guid StubId = Guid.Parse("99999999-9999-9999-9999-999999999999");

    /// <summary>
    /// A MSEL ready to push: all three integrations on, two moves numbered 1 and 2, one team with one user,
    /// and one scenario event that is a Gallery article.
    /// </summary>
    /// <remarks>
    /// The moves are numbered 1 and 2 rather than 0 and 1 on purpose - it is the only way to tell a move's
    /// number from its index, which is what two of these tests are about.
    /// </remarks>
    private async Task<MselEntity> SeedPushableMsel(Action<MselEntity> adjust = null)
    {
        var msel = TestData.Msel(status: MselItemStatus.Approved);

        msel.UsePlayer = true;
        msel.UseGallery = true;
        msel.UseCite = true;
        msel.CiteScoringModelId = Guid.NewGuid();
        msel.StartTime = DateTime.UtcNow.AddDays(1);
        msel.DurationSeconds = 3600;

        adjust?.Invoke(msel);

        await Seed(msel);
        await Seed(
            TestData.Move(msel.Id, moveNumber: 1, deltaSeconds: 0),
            TestData.Move(msel.Id, moveNumber: 2, deltaSeconds: 100));

        var team = TestData.Team(msel.Id);
        team.CiteTeamTypeId = Guid.NewGuid();
        await Seed(team);
        await Actor().WithName("Ada").OnTeam(team).SeedAsync();

        await Seed(TestData.PlayerApplication(
            msel.Id,
            name: "Console",
            url: "http://application.example/?cite={citeEvaluationId}&exhibit={galleryExhibitId}"));

        var scenarioEvent = TestData.ScenarioEvent(msel.Id, deltaSeconds: 0);
        scenarioEvent.IntegrationTarget = "Gallery";
        await Seed(scenarioEvent);

        return msel;
    }


    /// <remarks>
    /// <paramref name="first"/> is applied before the general rules, because rules are matched in the order
    /// they were added: a test that wants one route to fail has to register that route before the rule that
    /// would otherwise answer it.
    /// </remarks>
    private IntegrationServiceHarness Harness(Func<SiblingApiHandler, SiblingApiHandler> first = null)
    {
        var handler = Everything(first);

        return new IntegrationServiceHarness(Session, handler);
    }

    private static IEnumerable<string> Statuses(IntegrationServiceHarness harness, Guid mselId) =>
        harness.Hub.ToGroup(mselId)
            .Where(x => x.Method == MainHubMethods.IntegrationStatusUpdated)
            .Select(x => (string)x.Arguments[1]);

    private static int Number(Dictionary<string, JsonElement> body, string name) =>
        body.Single(x => string.Equals(x.Key, name, StringComparison.OrdinalIgnoreCase)).Value.GetInt32();

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
            .AnswersJson("gallery/api/exhibits/*", $$"""{"id":"{{StubId}}","collectionId":"{{StubId}}","name":"x"}""")
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
            .AnswersJson("POST cite/api/users", $$"""{"id":"{{StubId}}","name":"u"}""", HttpStatusCode.Created);
}
