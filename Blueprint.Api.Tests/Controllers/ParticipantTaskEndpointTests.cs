// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Blueprint.Api.Automation;
using Blueprint.Api.Data.Enumerations;
using Blueprint.Api.Data.Models;
using Blueprint.Api.Tests.Support;
using Blueprint.Api.ViewModels;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Blueprint.Api.Tests.Controllers;

/// <summary>
/// The MSEL's Tasks page: the tasks a participant, a member of one of the MSEL's teams, can run, and the exercise's
/// score, as Steamfitter's manual tasks page offered them.
/// </summary>
public class ParticipantTaskEndpointTests(DatabaseFixture fixture, BlueprintAppFactory factory)
    : ApiTestBase(fixture, factory), IClassFixture<BlueprintAppFactory>
{
    /// <summary>
    /// A participant sees the tasks marked for them, in event and list order, with each one's points and how its newest
    /// run went; a task not marked for them, or disabled, is not listed, though the score counts every task's points.
    /// </summary>
    [Fact]
    public async Task GetParticipantTasks_ListsTheParticipantsTasks_WithTheExercisesScore()
    {
        var (msel, scenarioEvent, team) = await SeedDeployed();
        var first = await SeedTask(msel, scenarioEvent, "Collect evidence", score: 10, displayOrder: 1);
        await SeedTask(msel, scenarioEvent, "Report", score: 5, displayOrder: 2);
        await SeedTask(msel, scenarioEvent, "Facilitator only", score: 20, userExecutable: false);
        await SeedTask(msel, scenarioEvent, "Disabled", score: 7, enabled: false);
        await SeedRun(first, msel, AutomationStatus.Succeeded);
        var participant = await Actor().OnTeam(team).SeedAsync();

        var page = await ReadAsync<ParticipantTasks>(await Client(participant).GetAsync($"/api/msels/{msel.Id}/participantTasks", Ct));

        Assert.True(page.IsDeployed);
        Assert.Equal(["Collect evidence", "Report"], page.Tasks.Select(t => t.Name));
        Assert.Equal(35, page.Score);
        Assert.Equal(10, page.ScoreEarned);
        var collected = page.Tasks[0];
        Assert.Equal(AutomationStatus.Succeeded, collected.Status);
        Assert.Equal((10, 10), (collected.TotalScore, collected.TotalScoreEarned));
        // not repeatable, and it succeeded
        Assert.False(collected.CanRun);
        Assert.Equal("The task has succeeded, and runs only once.", collected.CannotRunReason);
        Assert.True(page.Tasks[1].CanRun);
        Assert.Null(page.Tasks[1].Status);
    }

    /// <summary>What a participant is sent names no action, parameter, VM mask or output.</summary>
    [Fact]
    public async Task GetParticipantTasks_SendsNothingButTheTasksNameAndState()
    {
        var (msel, scenarioEvent, team) = await SeedDeployed();
        var task = await SeedTask(msel, scenarioEvent, "Collect evidence");
        task.ActionParameters["Text"] = "the-action-parameter";
        task.VmMask = "the-vm-mask";
        await ReadBack(async db =>
        {
            db.Update(task);
            return await db.SaveChangesAsync(Ct);
        });
        var participant = await Actor().OnTeam(team).SeedAsync();

        var body = await (await Client(participant).GetAsync($"/api/msels/{msel.Id}/participantTasks", Ct)).Content.ReadAsStringAsync(Ct);

        using var document = JsonDocument.Parse(body);
        var sent = document.RootElement.GetProperty("tasks")[0];
        Assert.Equal(
            ["canRun", "cannotRunReason", "description", "id", "name", "scenarioEventId", "status", "statusAt", "totalScore", "totalScoreEarned"],
            sent.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.DoesNotContain("the-action-parameter", body);
        Assert.DoesNotContain("the-vm-mask", body);
    }

    /// <summary>
    /// Read gate: anyone who may read the MSEL, its participants included, and ViewMsels. A member of another MSEL's team
    /// is refused (near miss).
    /// </summary>
    [Theory]
    [InlineData("participant", HttpStatusCode.OK)]
    [InlineData("contributor", HttpStatusCode.OK)]
    [InlineData("viewMsels", HttpStatusCode.OK)]
    [InlineData("otherTeam", HttpStatusCode.Forbidden)]
    [InlineData("nobody", HttpStatusCode.Forbidden)]
    public async Task GetParticipantTasks_IsReadByWhoeverMayReadTheMsel(string who, HttpStatusCode expected)
    {
        var (msel, _, team) = await SeedDeployed();
        var actor = await ActorFor(who, msel, team);

        await AssertStatus(expected, await Client(actor).GetAsync($"/api/msels/{msel.Id}/participantTasks", Ct));
    }

    /// <summary>
    /// A participant runs a task marked for them: the run is the exercise's, started by them, runs the tasks it triggers,
    /// and the answer is the task as the Tasks page shows it, not the run.
    /// </summary>
    [Fact]
    public async Task RunParticipantTask_StartsAnExerciseRun_AndAnswersTheTask()
    {
        var (msel, scenarioEvent, team) = await SeedDeployed();
        var task = await SeedTask(msel, scenarioEvent, "Collect evidence", score: 10);
        var participant = await Actor().OnTeam(team).SeedAsync();

        var response = await Run(Client(participant), msel, task);

        var answered = await ReadAsync<ParticipantTask>(response);
        Assert.Equal(task.Id, answered.Id);
        Assert.Equal(AutomationStatus.Pending, answered.Status);
        Assert.False(answered.CanRun);
        var run = await ReadBack(db => db.AutomationRuns.SingleAsync(r => r.AutomationTaskId == task.Id, Ct));
        Assert.Equal(
            (AutomationRunKind.Exercise, AutomationRunSource.Participant, participant.Id, msel.DeploymentId, true),
            (run.Kind, run.Source, run.RequestedBy, run.DeploymentId, run.TriggerChain));
        Assert.DoesNotContain("playerViewId", await response.Content.ReadAsStringAsync(Ct), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Run gate: a participant, a contributor and EditMsels run; ViewMsels only reads (near miss), and a member of another
    /// MSEL's team is refused.
    /// </summary>
    [Theory]
    [InlineData("participant", HttpStatusCode.OK)]
    [InlineData("contributor", HttpStatusCode.OK)]
    [InlineData("editMsels", HttpStatusCode.OK)]
    [InlineData("viewMsels", HttpStatusCode.Forbidden)]
    [InlineData("otherTeam", HttpStatusCode.Forbidden)]
    public async Task RunParticipantTask_IsForWhoeverIsInTheMsel(string who, HttpStatusCode expected)
    {
        var (msel, scenarioEvent, team) = await SeedDeployed();
        var task = await SeedTask(msel, scenarioEvent, "Collect evidence");
        var actor = await ActorFor(who, msel, team);

        await AssertStatus(expected, await Run(Client(actor), msel, task));
        Assert.Equal(expected == HttpStatusCode.OK ? 1 : 0, await ReadBack(db => db.AutomationRuns.CountAsync(r => r.AutomationTaskId == task.Id, Ct)));
    }

    /// <summary>
    /// A participant cannot run a task not marked for them, a disabled one, or one of another MSEL: to them it is not
    /// there.
    /// </summary>
    [Theory]
    [InlineData("notMarked")]
    [InlineData("disabled")]
    [InlineData("otherMsel")]
    public async Task RunParticipantTask_OfATaskNotOffered_Is404(string which)
    {
        var (msel, scenarioEvent, team) = await SeedDeployed();
        var (otherMsel, otherEvent, _) = await SeedDeployed();
        var task = which switch
        {
            "notMarked" => await SeedTask(msel, scenarioEvent, "Facilitator only", userExecutable: false),
            "disabled" => await SeedTask(msel, scenarioEvent, "Off", enabled: false),
            _ => await SeedTask(otherMsel, otherEvent, "Elsewhere"),
        };
        var participant = await Actor().OnTeam(team).SeedAsync();

        await AssertStatus(HttpStatusCode.NotFound, await Run(Client(participant), msel, task));
    }

    /// <summary>
    /// A participant runs a task only while the exercise runs: not before it is pushed, not while its automation is
    /// paused, not while the task runs, and not again once a task that runs only once succeeded; a repeatable one runs
    /// again.
    /// </summary>
    [Theory]
    [InlineData("notDeployed", HttpStatusCode.Conflict)]
    [InlineData("paused", HttpStatusCode.Conflict)]
    [InlineData("running", HttpStatusCode.Conflict)]
    [InlineData("succeeded", HttpStatusCode.Conflict)]
    [InlineData("succeededRepeatable", HttpStatusCode.OK)]
    public async Task RunParticipantTask_FollowsTheExercise(string state, HttpStatusCode expected)
    {
        var (msel, scenarioEvent, team) = await SeedDeployed(
            status: state == "notDeployed" ? MselItemStatus.Approved : MselItemStatus.Deployed,
            paused: state == "paused");
        var task = await SeedTask(msel, scenarioEvent, "Collect evidence", repeatable: state == "succeededRepeatable");
        if (state == "running")
            await SeedRun(task, msel, AutomationStatus.Running);
        if (state.StartsWith("succeeded"))
            await SeedRun(task, msel, AutomationStatus.Succeeded);
        var participant = await Actor().OnTeam(team).SeedAsync();

        await AssertStatus(expected, await Run(Client(participant), msel, task));
    }

    /// <summary>
    /// A facilitator's run by hand also waits for the task's run that has not ended, so two runs of one task do not
    /// overlap.
    /// </summary>
    [Fact]
    public async Task RunAutomationTask_WhileTheTaskRuns_Is409()
    {
        var (msel, scenarioEvent, _) = await SeedDeployed();
        var task = await SeedTask(msel, scenarioEvent, "Collect evidence");
        await SeedRun(task, msel, AutomationStatus.Queued);
        var facilitator = await Actor().OnMsel(msel, MselRole.Facilitator).SeedAsync();

        await AssertStatus(HttpStatusCode.Conflict,
            await Client(facilitator).PostAsync($"/api/automationTasks/{task.Id}/run", null, Ct));
    }

    // ---------------------------------------------------------------------------------------------

    private async Task<(MselEntity, ScenarioEventEntity, TeamEntity)> SeedDeployed(
        MselItemStatus status = MselItemStatus.Deployed, bool paused = false)
    {
        var msel = TestData.Msel(status: status);
        msel.DeploymentId = status == MselItemStatus.Deployed ? Guid.NewGuid() : null;
        msel.PlayerViewId = Guid.NewGuid();
        msel.AutomationPaused = paused;
        var team = TestData.Team(msel.Id);
        var scenarioEvent = TestData.ScenarioEvent(msel.Id);
        await Seed(msel, team, scenarioEvent);
        return (msel, scenarioEvent, team);
    }

    private async Task<AutomationTaskEntity> SeedTask(MselEntity msel, ScenarioEventEntity scenarioEvent, string name,
        int score = 0, bool userExecutable = true, bool enabled = true, bool repeatable = false, int displayOrder = 0)
    {
        var task = TestData.AutomationTask(msel.Id, scenarioEvent.Id, displayOrder: displayOrder);
        task.Name = name;
        task.Score = score;
        task.UserExecutable = userExecutable;
        task.Enabled = enabled;
        task.Repeatable = repeatable;
        await Seed(task);
        return task;
    }

    private async Task SeedRun(AutomationTaskEntity task, MselEntity msel, AutomationStatus status)
    {
        var run = AutomationRuns.New(task, msel.Id, AutomationRunKind.Exercise, AutomationRunSource.Manual, DateTime.UtcNow,
            msel.DeploymentId, msel.PlayerViewId, null);
        run.Status = status;
        await Seed(run);
    }

    private async Task<TestActor> ActorFor(string who, MselEntity msel, TeamEntity team)
    {
        switch (who)
        {
            case "participant":
                return await Actor().OnTeam(team).SeedAsync();
            case "contributor":
                return await Actor().OnMsel(msel, MselRole.Viewer).SeedAsync();
            case "viewMsels":
                return await Actor().WithSystemPermissions(SystemPermission.ViewMsels).SeedAsync();
            case "editMsels":
                return await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();
            case "otherTeam":
                var (_, _, otherTeam) = await SeedDeployed();
                return await Actor().OnTeam(otherTeam).SeedAsync();
            default:
                return await Actor().SeedAsync();
        }
    }

    private Task<HttpResponseMessage> Run(HttpClient client, MselEntity msel, AutomationTaskEntity task) =>
        client.PostAsync($"/api/msels/{msel.Id}/participantTasks/{task.Id}/run", null, Ct);
}
