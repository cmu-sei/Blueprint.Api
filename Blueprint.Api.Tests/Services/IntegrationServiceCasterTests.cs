// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Blueprint.Api.Data.Enumerations;
using Blueprint.Api.Data.Models;
using Blueprint.Api.Hubs;
using Blueprint.Api.Services;
using Blueprint.Api.Tests.Support;
using Xunit;

namespace Blueprint.Api.Tests.Services;

/// <summary><c>IntegrationService</c>'s Caster steps: the push that deploys a MSEL's topology as a Caster
/// workspace, and the pull and cancel cleanup that destroy it before the Player view goes.</summary>
/// <remarks>
/// Driven through <see cref="IntegrationServiceHarness"/>, as the other push and pull tests are: the worker
/// builds its own Caster client from <c>ClientSettings:CasterApiUrl</c> and the service account's token, so
/// <see cref="SiblingApiHandler"/> stands in for caster.api under the <c>caster/</c> prefix. The MSELs use
/// Player and Caster only, which keeps each arrangement to the routes those two answer.
/// </remarks>
public class IntegrationServiceCasterTests(DatabaseFixture fixture) : DatabaseTestBase(fixture)
{
    /// <summary>Starts describing an actor to seed over this test's database.</summary>
    private TestActorBuilder Actor() => new(Db, Ct);

    // ---------------------------------------------------------------------------------------------
    // Push
    // ---------------------------------------------------------------------------------------------

    /// <summary>The Caster steps run after every Player step, and each one is announced.</summary>
    [Fact]
    public async Task Push_OfACasterMsel_DeploysTheTopologyAfterPlayer()
    {
        var msel = await SeedCasterMsel();
        var harness = PushHarness();

        await harness.PushAsync(msel.Id);
        await harness.WaitForDeployment(msel.Id, Ct);

        Assert.Equal(
            [
                "Pushing View to Player",
                "Pushing Teams to Player",
                "Pushing Player Applications",
                "Caster - creating workspace",
                "Caster - writing variables",
                "Caster - creating run",
                "Caster - planning",
                "Caster - applying"
            ],
            Statuses(harness, msel.Id));
    }

    [Fact]
    public async Task Push_OfACasterMsel_MarksItDeployedAndRecordsTheWorkspaceAndTheRun()
    {
        var msel = await SeedCasterMsel();
        var harness = PushHarness();

        await harness.PushAsync(msel.Id);

        var deployed = await harness.WaitForDeployment(msel.Id, Ct);

        Assert.Null(deployed.IntegrationStatus);
        Assert.Equal(WorkspaceId, deployed.CasterWorkspaceId);
        Assert.Equal(RunId, deployed.CasterRunId);
    }

    [Fact]
    public async Task Push_OfACasterMsel_CreatesTheWorkspaceInTheMselsDirectoryUnderTheMselsId()
    {
        var msel = await SeedCasterMsel();
        var harness = PushHarness();

        await harness.PushAsync(msel.Id);
        await harness.WaitForDeployment(msel.Id, Ct);

        var workspace = Body(Assert.Single(harness.Handler.Sent, x => x.Path == "caster/api/workspaces").Body);

        Assert.Equal($"blueprint-{msel.Id}", workspace["name"].GetString());
        Assert.Equal(DirectoryId.ToString(), workspace["directoryId"].GetString());
    }

    /// <summary>
    /// The variables file names the view, the pusher and each of the view's teams, a mapped MSEL team under
    /// the variable it is mapped to and Player's own Admin team under its name.
    /// </summary>
    [Fact]
    public async Task Push_OfACasterMsel_WritesTheViewThePusherAndTheTeamsIntoTheVariablesFile()
    {
        var team = Guid.NewGuid();
        var adminTeam = Guid.NewGuid();
        var msel = await SeedCasterMsel(teamId: team);
        var pusher = await Actor().WithName("Pat Planner").SeedAsync();
        var harness = PushHarness(x => x.AnswersJson(
            "GET player/api/views/*/teams",
            $$"""[{"id":"{{team}}","name":"Blue Cell"},{"id":"{{adminTeam}}","name":"Admin"}]"""));

        await harness.PushAsync(msel.Id, userId: pusher.Id);

        var deployed = await harness.WaitForDeployment(msel.Id, Ct);
        var file = Body(Assert.Single(harness.Handler.Sent, x => x.Path == "caster/api/files").Body);

        Assert.Equal("ws.auto.tfvars", file["name"].GetString());
        Assert.Equal(WorkspaceId.ToString(), file["workspaceId"].GetString());
        Assert.Equal(DirectoryId.ToString(), file["directoryId"].GetString());
        Assert.Equal(
            $"exercise_id = \"{deployed.PlayerViewId}\"\r\n" +
            $"view_id = \"{deployed.PlayerViewId}\"\r\n" +
            $"user_id = \"{pusher.Id}\"\r\n" +
            "username = \"Pat Planner\"\r\n" +
            $"blue = \"{team}\"\r\n" +
            $"admin = \"{adminTeam}\"\r\n",
            file["content"].GetString());
    }

    /// <summary>A push queued with no pusher names the MSEL's creator, with an empty name when the creator
    /// has no user row.</summary>
    [Fact]
    public async Task Push_OfACasterMselQueuedWithNoPusher_WritesTheCreatorAsTheUser()
    {
        var msel = await SeedCasterMsel();
        var harness = PushHarness();

        await harness.PushAsync(msel.Id);
        await harness.WaitForDeployment(msel.Id, Ct);

        var content = Body(Assert.Single(harness.Handler.Sent, x => x.Path == "caster/api/files").Body)["content"]
            .GetString();

        Assert.Contains($"user_id = \"{msel.CreatedBy}\"\r\nusername = \"\"\r\n", content);
    }

    [Fact]
    public async Task Push_OfACasterMsel_PlansAndAppliesTheRunItCreated()
    {
        var msel = await SeedCasterMsel();
        var harness = PushHarness();

        await harness.PushAsync(msel.Id);
        await harness.WaitForDeployment(msel.Id, Ct);

        var run = Body(Assert.Single(harness.Handler.Sent, x => x.Path == "caster/api/runs").Body);

        Assert.Equal(WorkspaceId.ToString(), run["workspaceId"].GetString());
        Assert.False(run["isDestroy"].GetBoolean());
        Assert.Single(harness.Handler.Sent, x => x.Path == $"caster/api/runs/{RunId}/actions/apply");
    }

    /// <summary>Caster is called with the service account's token, not a user's.</summary>
    [Fact]
    public async Task Push_OfACasterMsel_CallsCasterWithTheServiceAccountsToken()
    {
        var msel = await SeedCasterMsel();
        var harness = PushHarness();

        await harness.PushAsync(msel.Id);
        await harness.WaitForDeployment(msel.Id, Ct);

        var toCaster = harness.Handler.Sent.Where(x => x.Path.StartsWith("caster/")).ToList();

        Assert.NotEmpty(toCaster);
        Assert.All(toCaster, x => Assert.Equal("Bearer abc123", x.Authorization));
    }

    [Fact]
    public async Task Push_OfAMselNotUsingCaster_ContactsCasterNotAtAll()
    {
        var msel = await SeedCasterMsel(useCaster: false);
        var harness = PushHarness();

        await harness.PushAsync(msel.Id);

        var deployed = await harness.WaitForDeployment(msel.Id, Ct);

        Assert.Null(deployed.CasterWorkspaceId);
        Assert.DoesNotContain(harness.Handler.Paths, x => x.StartsWith("caster/"));
    }

    /// <summary>A plan that fails is never applied; the error names the step, and the workspace and run ids
    /// stay for the cleanup.</summary>
    [Fact]
    public async Task Push_WhenTheCasterPlanFails_ReportsItAndKeepsTheWorkspaceAndRunIds()
    {
        var msel = await SeedCasterMsel();
        var harness = PushHarness(x => x.AnswersJson($"GET caster/api/runs/{RunId}", Run("Failed")));

        await harness.PushAsync(msel.Id);

        var failed = await harness.WaitForError(msel.Id, Ct);

        Assert.Equal(
            $"ERROR: Caster - planning failed - Caster run {RunId} plan ended Failed. " +
            "See the run in Caster for the terraform output.",
            failed.IntegrationStatus);
        Assert.NotEqual(MselItemStatus.Deployed, failed.Status);
        Assert.Equal(WorkspaceId, failed.CasterWorkspaceId);
        Assert.Equal(RunId, failed.CasterRunId);
        Assert.DoesNotContain(harness.Handler.Paths, x => x.EndsWith("/actions/apply"));
    }

    /// <summary>A workspace Caster refuses to create leaves no workspace id, and the Player view the push
    /// made first stays recorded.</summary>
    [Fact]
    public async Task Push_WhenCasterRefusesTheWorkspace_ReportsItAndRecordsNoWorkspace()
    {
        var msel = await SeedCasterMsel();
        var harness = PushHarness(x => x.AnswersJson("POST caster/api/workspaces", "{}", HttpStatusCode.Forbidden));

        await harness.PushAsync(msel.Id);

        var failed = await harness.WaitForError(msel.Id, Ct);

        Assert.StartsWith("ERROR: Caster - creating workspace failed - ", failed.IntegrationStatus);
        Assert.Null(failed.CasterWorkspaceId);
        Assert.Null(failed.CasterRunId);
        Assert.NotNull(failed.PlayerViewId);
        Assert.DoesNotContain(harness.Handler.Paths, x => x == "caster/api/runs");
    }

    // ---------------------------------------------------------------------------------------------
    // Retry Caster
    // ---------------------------------------------------------------------------------------------

    /// <summary>A retry redoes the Caster step alone, in the workspace the failed push made, and finishes
    /// the push.</summary>
    [Fact]
    public async Task RetryCaster_OfAFailedCasterPush_RunsOnlyTheCasterStepsInTheWorkspaceItHas()
    {
        var msel = await SeedFailedCasterPush();
        var harness = RetryHarness();

        await RetryAsync(harness, msel.Id);

        var deployed = await harness.WaitForDeployment(msel.Id, Ct);

        Assert.Equal(
            [
                "Caster - stopping active runs",
                "Caster - writing variables",
                "Caster - creating run",
                "Caster - planning",
                "Caster - applying"
            ],
            Statuses(harness, msel.Id));
        Assert.Null(deployed.IntegrationStatus);
        Assert.Equal(PlayerViewId, deployed.PlayerViewId);
        Assert.Equal(WorkspaceId, deployed.CasterWorkspaceId);
        Assert.Equal(RunId, deployed.CasterRunId);
        Assert.DoesNotContain(harness.Handler.Sent, x => x.Method == HttpMethod.Post && x.Path.StartsWith("player/"));
        Assert.DoesNotContain(harness.Handler.Sent, x => x.Method == HttpMethod.Post && x.Path == "caster/api/workspaces");
    }

    /// <summary>The variables file is rewritten, so a fix to the team variables reaches terraform; the
    /// workspace's other files, and other workspaces' variables files, are left alone.</summary>
    [Fact]
    public async Task RetryCaster_OfAFailedCasterPush_ReplacesOnlyTheWorkspacesVariablesFile()
    {
        var team = Guid.NewGuid();
        var msel = await SeedFailedCasterPush(team);
        var name = $"blueprint-{msel.Id}";
        var harness = RetryHarness(name, x => x.AnswersJson(
            "GET player/api/views/*/teams", $$"""[{"id":"{{team}}","name":"Blue Cell"}]"""));

        await RetryAsync(harness, msel.Id);
        await harness.WaitForDeployment(msel.Id, Ct);

        var deleted = harness.Handler.Sent.Where(x => x.Method == HttpMethod.Delete).Select(x => x.Path).ToList();
        Assert.Equal([$"caster/api/files/{OldVarsFileId}"], deleted);
        var sent = harness.Handler.Sent.ToList();
        Assert.True(
            sent.FindIndex(x => x.Method == HttpMethod.Delete) < sent.FindIndex(x => x.Method == HttpMethod.Post && x.Path == "caster/api/files"),
            "The old variables file has to go before the new one is written.");
        var file = Body(Assert.Single(sent, x => x.Method == HttpMethod.Post && x.Path == "caster/api/files").Body);
        Assert.Equal($"{name}.auto.tfvars", file["name"].GetString());
        Assert.Contains($"view_id = \"{PlayerViewId}\"", file["content"].GetString());
        Assert.Contains($"blue = \"{team}\"", file["content"].GetString());
    }

    /// <summary>A push that failed before Caster made the workspace, or one deleted in Caster since, gets
    /// the whole Caster step.</summary>
    [Fact]
    public async Task RetryCaster_WhenTheWorkspaceIsGone_CreatesOne()
    {
        var msel = await SeedFailedCasterPush();
        var harness = PushHarness(x => x.Answers($"GET caster/api/workspaces/{WorkspaceId}", HttpStatusCode.NotFound));

        await RetryAsync(harness, msel.Id);

        var deployed = await harness.WaitForDeployment(msel.Id, Ct);

        Assert.Equal("Caster - creating workspace", Statuses(harness, msel.Id)[0]);
        Assert.Single(harness.Handler.Sent, x => x.Method == HttpMethod.Post && x.Path == "caster/api/workspaces");
        Assert.DoesNotContain(harness.Handler.Sent, x => x.Method == HttpMethod.Delete);
        Assert.Equal(RunId, deployed.CasterRunId);
    }

    /// <summary>A retry that fails again reports it the same way, so it can be retried again.</summary>
    [Fact]
    public async Task RetryCaster_WhenThePlanFailsAgain_ReportsItAsACasterError()
    {
        var msel = await SeedFailedCasterPush();
        var harness = RetryHarness(first: x => x.AnswersJson($"GET caster/api/runs/{RunId}", Run("Failed")));

        await RetryAsync(harness, msel.Id);

        var failed = await harness.WaitForError(msel.Id, Ct);

        Assert.StartsWith("ERROR: Caster - planning failed - ", failed.IntegrationStatus);
        Assert.Equal(MselItemStatus.Pushing, failed.Status);
    }

    // ---------------------------------------------------------------------------------------------
    // Pull
    // ---------------------------------------------------------------------------------------------

    /// <summary>The workspace is destroyed and deleted first, while the Player view its VMs belong to still
    /// exists.</summary>
    [Fact]
    public async Task Pull_OfACasterMsel_DeletesTheWorkspaceBeforeThePlayerView()
    {
        var msel = await SeedDeployedCasterMsel();
        var harness = PullHarness();

        await harness.PullAsync(msel.Id);
        await harness.WaitForFinalStatus(msel.Id, MselItemStatus.Approved, Ct);

        Assert.Equal(
            [
                $"GET caster/api/workspaces/{WorkspaceId}",
                $"GET caster/api/workspaces/{WorkspaceId}/runs",
                $"GET caster/api/workspaces/{WorkspaceId}/resources",
                $"DELETE caster/api/workspaces/{WorkspaceId}",
                $"DELETE player/api/views/{PlayerViewId}",
                $"DELETE player/api/views/{PlayerViewId}"
            ],
            harness.Handler.Sent
                .Where(x => !x.Path.StartsWith("realms/"))
                .Select(x => $"{x.Method} {x.Path}")
                .ToList());
    }

    [Fact]
    public async Task Pull_OfACasterMsel_AnnouncesTheCasterStepsFirst()
    {
        var msel = await SeedDeployedCasterMsel();
        var harness = PullHarness();

        await harness.PullAsync(msel.Id);
        await harness.WaitForFinalStatus(msel.Id, MselItemStatus.Approved, Ct);

        Assert.Equal(
            [
                "Pulling Integrations",
                "Pulling Caster Workspace",
                "Pulling Caster Workspace - stopping active runs",
                "Pulling Caster Workspace - deleting workspace",
                "Pulling Player View"
            ],
            Statuses(harness, msel.Id));
    }

    [Fact]
    public async Task Pull_OfACasterMsel_ClearsTheWorkspaceAndRunIds()
    {
        var msel = await SeedDeployedCasterMsel();
        var harness = PullHarness();

        await harness.PullAsync(msel.Id);

        var pulled = await harness.WaitForFinalStatus(msel.Id, MselItemStatus.Approved, Ct);

        Assert.Null(pulled.CasterWorkspaceId);
        Assert.Null(pulled.CasterRunId);
        Assert.Null(pulled.PlayerViewId);
    }

    /// <summary>The other pulls get a token fetched after the destroy, which can outlast the first.</summary>
    [Fact]
    public async Task Pull_OfACasterMsel_FetchesANewTokenForThePullsAfterTheDestroy()
    {
        var msel = await SeedDeployedCasterMsel();
        var harness = PullHarness();

        await harness.PullAsync(msel.Id);
        await harness.WaitForFinalStatus(msel.Id, MselItemStatus.Approved, Ct);

        var paths = harness.Handler.Paths.ToList();
        var lastToken = paths.LastIndexOf(IntegrationServiceHarness.TokenPath);

        Assert.True(lastToken > paths.IndexOf($"caster/api/workspaces/{WorkspaceId}/resources"));
        Assert.True(lastToken < paths.IndexOf($"player/api/views/{PlayerViewId}"));
    }

    /// <summary>A workspace Caster no longer has counts as pulled, and the rest of the pull goes on.</summary>
    [Fact]
    public async Task Pull_OfAMselWhoseWorkspaceIsAlreadyGone_PullsTheRest()
    {
        var msel = await SeedDeployedCasterMsel();
        var harness = PullHarness(x => x.Answers($"GET caster/api/workspaces/{WorkspaceId}", HttpStatusCode.NotFound));

        await harness.PullAsync(msel.Id);

        var pulled = await harness.WaitForFinalStatus(msel.Id, MselItemStatus.Approved, Ct);

        Assert.Null(pulled.CasterWorkspaceId);
        Assert.DoesNotContain(harness.Handler.Sent, x => x.Method == HttpMethod.Delete && x.Path.StartsWith("caster/"));
        Assert.Contains($"player/api/views/{PlayerViewId}", harness.Handler.Paths);
    }

    /// <summary>Unlike the other pulls, a failed Caster destroy stops the pull: nothing else is removed and
    /// every id stays, so the pull can be run again.</summary>
    [Fact]
    public async Task Pull_WhenTheCasterDestroyFails_StopsAndKeepsEveryId()
    {
        var msel = await SeedDeployedCasterMsel();
        var harness = PullHarness(x => x.AnswersJson($"GET caster/api/workspaces/{WorkspaceId}", "{}", HttpStatusCode.InternalServerError));

        await harness.PullAsync(msel.Id);

        var failed = await harness.WaitForError(msel.Id, Ct);

        Assert.StartsWith($"ERROR: Caster - pull workspace {WorkspaceId} failed - ", failed.IntegrationStatus);
        Assert.EndsWith(
            "Nothing else was removed. Remove Integrations again to retry, or clean up the workspace in Caster.",
            failed.IntegrationStatus);
        Assert.Equal(MselItemStatus.Deployed, failed.Status);
        Assert.Equal(WorkspaceId, failed.CasterWorkspaceId);
        Assert.Equal(RunId, failed.CasterRunId);
        Assert.Equal(PlayerViewId, failed.PlayerViewId);
        Assert.DoesNotContain(harness.Handler.Paths, x => x.StartsWith("player/"));
    }

    // ---------------------------------------------------------------------------------------------
    // Cancel cleanup
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task CancelPush_OfACasterMsel_DeletesTheWorkspaceBeforeThePlayerViewAndApproves()
    {
        var msel = await SeedDeployedCasterMsel();
        var harness = PullHarness();

        harness.Service.CancelPush(msel.Id);

        var cleaned = await harness.WaitForFinalStatus(msel.Id, MselItemStatus.Approved, Ct);
        var paths = harness.Handler.Sent.Select(x => $"{x.Method} {x.Path}").ToList();

        Assert.Null(cleaned.CasterWorkspaceId);
        Assert.Null(cleaned.CasterRunId);
        Assert.Null(cleaned.PlayerViewId);
        Assert.True(
            paths.IndexOf($"DELETE caster/api/workspaces/{WorkspaceId}") <
            paths.IndexOf($"DELETE player/api/views/{PlayerViewId}"));
    }

    /// <summary>The cancel's Caster steps read as cancelling, so the UI does not offer Cancel Push again.</summary>
    [Fact]
    public async Task CancelPush_OfACasterMsel_AnnouncesTheCasterStepsAsCancelling()
    {
        var msel = await SeedDeployedCasterMsel();
        var harness = PullHarness();

        harness.Service.CancelPush(msel.Id);
        await harness.WaitForFinalStatus(msel.Id, MselItemStatus.Approved, Ct);

        Assert.Equal(
            [
                "Cancelling - pulling Caster Workspace",
                "Cancelling - pulling Caster Workspace - stopping active runs",
                "Cancelling - pulling Caster Workspace - deleting workspace"
            ],
            Statuses(harness, msel.Id).Where(x => x.Contains("Caster")));
    }

    [Fact]
    public async Task CancelPush_WhenTheCasterDestroyFails_StopsAndKeepsEveryId()
    {
        var msel = await SeedDeployedCasterMsel();
        var harness = PullHarness(x => x.AnswersJson($"GET caster/api/workspaces/{WorkspaceId}", "{}", HttpStatusCode.InternalServerError));

        harness.Service.CancelPush(msel.Id);

        var failed = await harness.WaitForError(msel.Id, Ct);

        Assert.StartsWith($"ERROR: Caster - pull workspace {WorkspaceId} failed - ", failed.IntegrationStatus);
        Assert.Equal(MselItemStatus.Pulling, failed.Status);
        Assert.Equal(WorkspaceId, failed.CasterWorkspaceId);
        Assert.Equal(PlayerViewId, failed.PlayerViewId);
        Assert.DoesNotContain(harness.Handler.Paths, x => x.StartsWith("player/"));
    }

    // ---------------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------------

    private static readonly Guid StubId = Guid.Parse("99999999-9999-9999-9999-999999999999");
    private static readonly Guid DirectoryId = Guid.Parse("cccccccc-0000-0000-0000-000000000001");
    private static readonly Guid WorkspaceId = Guid.Parse("cccccccc-0000-0000-0000-000000000002");
    private static readonly Guid RunId = Guid.Parse("cccccccc-0000-0000-0000-000000000003");
    private static readonly Guid PlayerViewId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    /// <summary>
    /// A MSEL ready to push to Player and Caster: a directory, and one team mapped to the template's
    /// <c>blue</c> variable.
    /// </summary>
    private async Task<MselEntity> SeedCasterMsel(bool useCaster = true, Guid? teamId = null)
    {
        var msel = TestData.Msel(status: MselItemStatus.Approved);
        msel.UsePlayer = true;
        msel.UseCaster = useCaster;
        msel.CasterDirectoryId = DirectoryId;
        await Seed(msel);

        var team = TestData.Team(msel.Id);
        team.Id = teamId ?? team.Id;
        team.CasterTeamVariable = "blue";
        await Seed(team);

        return msel;
    }

    /// <summary>A MSEL deployed to Player and Caster, as a pull or a cancel finds it.</summary>
    private async Task<MselEntity> SeedDeployedCasterMsel()
    {
        var msel = TestData.Msel(status: MselItemStatus.Deployed);
        msel.UsePlayer = true;
        msel.UseCaster = true;
        msel.CasterDirectoryId = DirectoryId;
        msel.PlayerViewId = PlayerViewId;
        msel.CasterWorkspaceId = WorkspaceId;
        msel.CasterRunId = RunId;
        await Seed(msel);

        return msel;
    }

    private static readonly Guid OldVarsFileId = Guid.Parse("cccccccc-0000-0000-0000-000000000004");

    /// <summary>A MSEL whose push made the Player view and the Caster workspace, and failed at the plan.</summary>
    private async Task<MselEntity> SeedFailedCasterPush(Guid? teamId = null)
    {
        var msel = TestData.Msel(status: MselItemStatus.Pushing);
        msel.UsePlayer = true;
        msel.UseCaster = true;
        msel.CasterDirectoryId = DirectoryId;
        msel.PlayerViewId = PlayerViewId;
        msel.CasterWorkspaceId = WorkspaceId;
        msel.CasterRunId = Guid.NewGuid();
        msel.IntegrationStatus = "ERROR: Caster - planning failed - the plan did not finish";
        await Seed(msel);

        var team = TestData.Team(msel.Id);
        team.Id = teamId ?? team.Id;
        team.CasterTeamVariable = "blue";
        await Seed(team);

        return msel;
    }

    /// <summary>
    /// The push's rules, plus the workspace the failed push left: no active runs, and a directory holding
    /// its variables file, another of its files, and another workspace's variables file of the same name.
    /// </summary>
    private IntegrationServiceHarness RetryHarness(string workspaceName = "ws", Func<SiblingApiHandler, SiblingApiHandler> first = null) =>
        PushHarness(x => (first is null ? x : first(x))
            .AnswersJson($"GET caster/api/workspaces/{WorkspaceId}/runs", "[]")
            .AnswersJson($"GET caster/api/workspaces/{WorkspaceId}",
                $$"""{"id":"{{WorkspaceId}}","name":"{{workspaceName}}","directoryId":"{{DirectoryId}}"}""")
            .AnswersJson($"GET caster/api/directories/{DirectoryId}/files", $$"""
                [
                  {"id":"{{OldVarsFileId}}","name":"{{workspaceName}}.auto.tfvars","directoryId":"{{DirectoryId}}","workspaceId":"{{WorkspaceId}}"},
                  {"id":"{{Guid.NewGuid()}}","name":"main.tf","directoryId":"{{DirectoryId}}","workspaceId":"{{WorkspaceId}}"},
                  {"id":"{{Guid.NewGuid()}}","name":"{{workspaceName}}.auto.tfvars","directoryId":"{{DirectoryId}}","workspaceId":"{{Guid.NewGuid()}}"}
                ]
                """)
            .Answers($"DELETE caster/api/files/{OldVarsFileId}", HttpStatusCode.NoContent));

    /// <summary>Queues the push Retry Caster queues, and starts the worker.</summary>
    private static async Task RetryAsync(IntegrationServiceHarness harness, Guid mselId)
    {
        await harness.StartAsync();
        harness.Queue.Add(new IntegrationInformation
        {
            MselId = mselId,
            IsPush = true,
            CasterOnly = true,
            FinalStatus = MselItemStatus.Deployed
        });
    }

    /// <remarks>
    /// <paramref name="first"/> is applied before the general rules, because rules are matched in the order
    /// they were added.
    /// </remarks>
    private IntegrationServiceHarness PushHarness(Func<SiblingApiHandler, SiblingApiHandler> first = null) =>
        new(Session, Idp(first)
            .AnswersJson("GET player/api/users", "[]")
            .AnswersJson("GET player/api/views/*/teams", """[{"id":"00000000-0000-0000-0000-000000000001","name":"Admin"}]""")
            .AnswersJson("player/api/views/*/teams", $$"""{"id":"{{StubId}}","name":"t"}""", HttpStatusCode.Created)
            .AnswersJson("player/api/views", $$"""{"id":"{{StubId}}","name":"v","status":"Active"}""", HttpStatusCode.Created)
            .AnswersJson("player/api/teams/*", $$"""{"id":"{{StubId}}","name":"u"}""")
            .AnswersJson("POST player/api/users", $$"""{"id":"{{StubId}}","name":"u"}""", HttpStatusCode.Created)
            .AnswersJson("POST caster/api/workspaces",
                $$"""{"id":"{{WorkspaceId}}","name":"ws","directoryId":"{{DirectoryId}}"}""", HttpStatusCode.Created)
            .AnswersJson("POST caster/api/files",
                $$"""{"id":"{{StubId}}","name":"ws.auto.tfvars","directoryId":"{{DirectoryId}}","workspaceId":"{{WorkspaceId}}"}""",
                HttpStatusCode.Created)
            .AnswersJson("POST caster/api/runs", Run("Queued"), HttpStatusCode.Created)
            .AnswersJson($"GET caster/api/runs/{RunId}", Run("Planned"), once: true)
            .AnswersJson($"GET caster/api/runs/{RunId}", Run("Applied"))
            .AnswersJson($"POST caster/api/runs/{RunId}/actions/apply", "{}"));

    /// <summary>The identity provider, an empty workspace that deletes cleanly, and the Player deletes.</summary>
    private IntegrationServiceHarness PullHarness(Func<SiblingApiHandler, SiblingApiHandler> first = null) =>
        new(Session, Idp(first)
            .AnswersJson($"GET caster/api/workspaces/{WorkspaceId}/runs", "[]")
            .AnswersJson($"GET caster/api/workspaces/{WorkspaceId}/resources", "[]")
            .AnswersJson($"GET caster/api/workspaces/{WorkspaceId}",
                $$"""{"id":"{{WorkspaceId}}","name":"ws","directoryId":"{{DirectoryId}}"}""")
            .Answers($"DELETE caster/api/workspaces/{WorkspaceId}", HttpStatusCode.NoContent)
            .Answers("player/api/views/*", HttpStatusCode.NoContent));

    private static SiblingApiHandler Idp(Func<SiblingApiHandler, SiblingApiHandler> first) =>
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
                """{"access_token":"abc123","token_type":"Bearer","expires_in":300}""");

    private static string Run(string status) =>
        $$"""{"id":"{{RunId}}","workspaceId":"{{WorkspaceId}}","isDestroy":false,"status":"{{status}}","createdAt":"2026-10-07T00:00:00Z"}""";

    private static List<string> Statuses(IntegrationServiceHarness harness, Guid mselId) =>
        [.. harness.Hub.ToGroup(mselId)
            .Where(x => x.Method == MainHubMethods.IntegrationStatusUpdated)
            .Select(x => (string)x.Arguments[1])];

    private static Dictionary<string, JsonElement> Body(string body) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(body);
}
