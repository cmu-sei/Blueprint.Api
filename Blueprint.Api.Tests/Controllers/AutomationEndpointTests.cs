// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Blueprint.Api.Automation;
using Blueprint.Api.Data.Enumerations;
using Blueprint.Api.Data.Models;
using Blueprint.Api.Tests.Support;
using Blueprint.Api.ViewModels;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace Blueprint.Api.Tests.Controllers;

/// <summary>
/// <c>AutomationController</c> and <c>MselExecutionController</c>, and the automation tasks a scenario event's edit
/// saves: authoring and testing tasks on the Scenario Events list, and running them from the Facilitator View.
/// </summary>
public class AutomationEndpointTests(DatabaseFixture fixture, BlueprintAppFactory factory)
    : ApiTestBase(fixture, factory), IClassFixture<BlueprintAppFactory>
{
    // ---------------------------------------------------------------------------------------------
    // saving tasks with their scenario event
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task UpdatingAnEvent_WithItsTasks_SavesThemInOrder()
    {
        var (msel, scenarioEvent) = await SeedEvent();
        var actor = await Actor().OnMsel(msel, MselRole.Editor).SeedAsync();

        var response = await PutEvent(Client(actor), scenarioEvent, Notification("first"), Http("second"));

        await AssertStatus(HttpStatusCode.OK, response);
        var tasks = await GetTasks(Client(actor), msel.Id);
        Assert.Equal(["first", "second"], tasks.OrderBy(t => t.DisplayOrder).Select(t => t.Name));
        Assert.All(tasks, t => Assert.Equal(scenarioEvent.Id, t.ScenarioEventId));
    }

    [Fact]
    public async Task CreatingAnEvent_WithTasks_SavesThem()
    {
        var msel = TestData.Msel();
        await Seed(msel);
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();
        var id = Guid.NewGuid();

        var response = await Client(actor).PostAsJsonAsync("/api/scenarioEvents", new
        {
            id,
            mselId = msel.Id,
            groupOrder = 1,
            deltaSeconds = 0,
            scenarioEventType = "Inject",
            dataValues = Array.Empty<object>(),
            automationTasks = new[] { Notification("new") },
        }, Ct);

        await AssertStatus(HttpStatusCode.OK, response);
        Assert.Equal("new", (await ReadBack(db => db.AutomationTasks.SingleAsync(t => t.ScenarioEventId == id, Ct))).Name);
    }

    /// <summary>A task keeps its points and whether participants can run it; a negative score is refused.</summary>
    [Theory]
    [InlineData(25, HttpStatusCode.OK)]
    [InlineData(-1, HttpStatusCode.BadRequest)]
    public async Task SavingATask_KeepsItsScoreAndWhetherParticipantsRunIt(int score, HttpStatusCode expected)
    {
        var (msel, scenarioEvent) = await SeedEvent();
        var actor = await Actor().OnMsel(msel, MselRole.Editor).SeedAsync();

        var response = await PutEvent(Client(actor), scenarioEvent, new
        {
            name = "Collect evidence",
            action = "player_notification",
            actionParameters = new Dictionary<string, string> { ["Text"] = "Go" },
            iterations = 1,
            triggerCondition = "Manual",
            enabled = true,
            score,
            userExecutable = true,
        });

        await AssertStatus(expected, response);
        if (expected == HttpStatusCode.OK)
        {
            var stored = await ReadBack(db => db.AutomationTasks.SingleAsync(t => t.ScenarioEventId == scenarioEvent.Id, Ct));
            Assert.Equal((25, true), (stored.Score, stored.UserExecutable));
            var listed = Assert.Single(await GetTasks(Client(actor), msel.Id));
            Assert.Equal((25, true), (listed.Score, listed.UserExecutable));
        }
        else
        {
            Assert.Contains("the score must be from 0 to 100000", await response.Content.ReadAsStringAsync(Ct));
        }
    }

    /// <summary>An edit that does not list the tasks, as moving the event on the timeline does, keeps them.</summary>
    [Fact]
    public async Task UpdatingAnEvent_WithoutItsTasks_KeepsThem()
    {
        var (msel, scenarioEvent) = await SeedEvent();
        var task = await SeedTask(msel, scenarioEvent);
        var actor = await Actor().OnMsel(msel, MselRole.Editor).SeedAsync();

        await AssertStatus(HttpStatusCode.OK, await PutEvent(Client(actor), scenarioEvent, null));

        Assert.True(await ReadBack(db => db.AutomationTasks.AnyAsync(t => t.Id == task.Id, Ct)));
    }

    /// <summary>A task the list leaves out is deleted, and a task it triggered elsewhere is now run by hand.</summary>
    [Fact]
    public async Task UpdatingAnEvent_WithoutATask_DeletesIt_AndFreesTheTasksItTriggered()
    {
        var (msel, scenarioEvent) = await SeedEvent();
        var task = await SeedTask(msel, scenarioEvent);
        var other = await SeedEventOf(msel);
        var triggered = TestData.AutomationTask(msel.Id, other.Id, trigger: AutomationTrigger.OnSuccess, triggerTaskId: task.Id);
        await Seed(triggered);
        var actor = await Actor().OnMsel(msel, MselRole.Editor).SeedAsync();

        await AssertStatus(HttpStatusCode.OK, await PutEvent(Client(actor), scenarioEvent));

        Assert.False(await ReadBack(db => db.AutomationTasks.AnyAsync(t => t.Id == task.Id, Ct)));
        var freed = await ReadBack(db => db.AutomationTasks.SingleAsync(t => t.Id == triggered.Id, Ct));
        Assert.Equal(AutomationTrigger.Manual, freed.TriggerCondition);
        Assert.Null(freed.TriggerTaskId);
    }

    /// <summary>A client names its new tasks, so one of them can be triggered by another in the same save.</summary>
    [Fact]
    public async Task UpdatingAnEvent_CanChainOneNewTaskToAnother()
    {
        var (msel, scenarioEvent) = await SeedEvent();
        var actor = await Actor().OnMsel(msel, MselRole.Editor).SeedAsync();
        var first = Guid.NewGuid();

        await AssertStatus(HttpStatusCode.OK, await PutEvent(Client(actor), scenarioEvent,
            Notification("first", id: first),
            Notification("second", trigger: AutomationTrigger.OnSuccess, triggerTaskId: first)));

        var second = await ReadBack(db => db.AutomationTasks.SingleAsync(t => t.Name == "second" && t.MselId == msel.Id, Ct));
        Assert.Equal(first, second.TriggerTaskId);
    }

    /// <summary>A secret is stored encrypted, named in reads and never sent; an edit that leaves it out keeps it.</summary>
    [Fact]
    public async Task ATasksSecret_IsStoredEncrypted_NamedButNeverReturned_AndKeptUntilCleared()
    {
        var (msel, scenarioEvent) = await SeedEvent();
        var actor = await Actor().OnMsel(msel, MselRole.Editor).SeedAsync();
        var id = Guid.NewGuid();

        await AssertStatus(HttpStatusCode.OK, await PutEvent(Client(actor), scenarioEvent, Http("h", id: id, secrets: new() { ["Headers"] = "{\"X-Key\":\"s3cret\"}" })));

        var stored = await ReadBack(db => db.AutomationTasks.SingleAsync(t => t.Id == id, Ct));
        Assert.DoesNotContain("s3cret", stored.SecretParameters);
        var read = await GetTasks(Client(actor), msel.Id);
        Assert.Equal(["Headers"], Assert.Single(read).SecretParameterKeys);
        Assert.DoesNotContain("s3cret", await (await Client(actor).GetAsync($"/api/msels/{msel.Id}/automationTasks", Ct)).Content.ReadAsStringAsync(Ct));

        await AssertStatus(HttpStatusCode.OK, await PutEvent(Client(actor), scenarioEvent, Http("h", id: id)));
        Assert.Equal(stored.SecretParameters, (await ReadBack(db => db.AutomationTasks.SingleAsync(t => t.Id == id, Ct))).SecretParameters);

        await AssertStatus(HttpStatusCode.OK, await PutEvent(Client(actor), scenarioEvent, Http("h", id: id, secrets: new() { ["Headers"] = "" })));
        Assert.Null((await ReadBack(db => db.AutomationTasks.SingleAsync(t => t.Id == id, Ct))).SecretParameters);
    }

    public static TheoryData<string, string> RefusedTasks => new()
    {
        { "missing parameter", "Notification text is required." },
        { "vm without mask", "a VM mask is required" },
        { "trigger elsewhere", "the task that triggers it is not one of this MSEL" },
        { "trigger itself", "it cannot trigger itself" },
        { "no trigger task", "the task that triggers it is required" },
        { "no name", "a name is required" },
        { "too many iterations", "iterations must be from 1 to 1000" },
    };

    [Theory]
    [MemberData(nameof(RefusedTasks))]
    public async Task UpdatingAnEvent_WithATaskThatCannotRun_Is400_AndSavesNothing(string what, string message)
    {
        var (msel, scenarioEvent) = await SeedEvent();
        var elsewhere = await SeedTask(await SeedMselWithEvent());
        var actor = await Actor().OnMsel(msel, MselRole.Editor).SeedAsync();
        var id = Guid.NewGuid();
        var task = what switch
        {
            "missing parameter" => Notification("n", text: null),
            "vm without mask" => Task("vm", "vm_hw_power_on", new()),
            "trigger elsewhere" => Notification("n", trigger: AutomationTrigger.OnSuccess, triggerTaskId: elsewhere.Id),
            "trigger itself" => Notification("n", id: id, trigger: AutomationTrigger.OnFailure, triggerTaskId: id),
            "no trigger task" => Notification("n", trigger: AutomationTrigger.OnCompletion),
            "no name" => Notification(""),
            _ => Notification("n", iterations: 1001),
        };

        var response = await PutEvent(Client(actor), scenarioEvent, task);

        await AssertStatus(HttpStatusCode.BadRequest, response);
        Assert.Contains(message, await response.Content.ReadAsStringAsync(Ct));
        Assert.False(await ReadBack(db => db.AutomationTasks.AnyAsync(t => t.ScenarioEventId == scenarioEvent.Id, Ct)));
    }

    [Fact]
    public async Task UpdatingAnEvent_WithTriggersThatLoop_Is400()
    {
        var (msel, scenarioEvent) = await SeedEvent();
        var actor = await Actor().OnMsel(msel, MselRole.Editor).SeedAsync();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();

        var response = await PutEvent(Client(actor), scenarioEvent,
            Notification("a", id: a, trigger: AutomationTrigger.OnSuccess, triggerTaskId: b),
            Notification("b", id: b, trigger: AutomationTrigger.OnSuccess, triggerTaskId: a));

        await AssertStatus(HttpStatusCode.BadRequest, response);
        Assert.Contains("its triggers lead back to it", await response.Content.ReadAsStringAsync(Ct));
    }

    /// <summary>
    /// A task that only leads into a loop of other tasks, kept from older data, is not in that loop, so it saves; the loop
    /// is the other tasks' to answer for.
    /// </summary>
    [Fact]
    public async Task UpdatingAnEvent_WithATaskThatLeadsIntoAnotherLoop_SavesIt()
    {
        var (msel, scenarioEvent) = await SeedEvent();
        var other = await SeedEventOf(msel);
        var b = TestData.AutomationTask(msel.Id, other.Id, trigger: AutomationTrigger.OnSuccess);
        var c = TestData.AutomationTask(msel.Id, other.Id, displayOrder: 1, trigger: AutomationTrigger.OnSuccess, triggerTaskId: b.Id);
        await Seed(b, c);
        // the loop is closed in a second save: EF cannot insert two rows that name each other
        await ReadBack(db => db.AutomationTasks.Where(t => t.Id == b.Id)
            .ExecuteUpdateAsync(u => u.SetProperty(t => t.TriggerTaskId, c.Id), Ct));
        var actor = await Actor().OnMsel(msel, MselRole.Editor).SeedAsync();

        await AssertStatus(HttpStatusCode.OK, await PutEvent(Client(actor), scenarioEvent,
            Notification("a", trigger: AutomationTrigger.OnSuccess, triggerTaskId: b.Id)));
    }

    /// <summary>
    /// A Manual task's trigger id is dropped, not checked: an author who makes a task Manual and deletes its old trigger
    /// in the same save is not refused.
    /// </summary>
    [Fact]
    public async Task UpdatingAnEvent_WithAManualTaskStillNamingAGoneTrigger_SavesIt()
    {
        var (msel, scenarioEvent) = await SeedEvent();
        var actor = await Actor().OnMsel(msel, MselRole.Editor).SeedAsync();

        await AssertStatus(HttpStatusCode.OK, await PutEvent(Client(actor), scenarioEvent,
            Notification("n", trigger: AutomationTrigger.Manual, triggerTaskId: Guid.NewGuid())));

        var saved = await ReadBack(db => db.AutomationTasks.SingleAsync(t => t.ScenarioEventId == scenarioEvent.Id, Ct));
        Assert.Null(saved.TriggerTaskId);
    }

    /// <summary>A secret given twice, in different cases, is refused as such rather than failing the save.</summary>
    [Fact]
    public async Task UpdatingAnEvent_WithASecretGivenTwice_Is400()
    {
        var (msel, scenarioEvent) = await SeedEvent();
        var actor = await Actor().OnMsel(msel, MselRole.Editor).SeedAsync();

        var response = await PutEvent(Client(actor), scenarioEvent,
            Http("h", secrets: new() { ["Headers"] = "{\"X\":\"a\"}", ["headers"] = "{\"X\":\"b\"}" }));

        await AssertStatus(HttpStatusCode.BadRequest, response);
        Assert.Contains("the secret Headers is given more than once", await response.Content.ReadAsStringAsync(Ct));
    }

    /// <summary>
    /// Deleting an event takes its tasks, and a task of another event that one of them triggered is now run by hand, as
    /// when an edit removes the task, rather than left waiting for a trigger that is gone.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeletingAnEvent_MakesTheTasksItsTasksTriggered_RunByHand(bool batch)
    {
        var (msel, scenarioEvent) = await SeedEvent();
        var other = await SeedEventOf(msel);
        var trigger = TestData.AutomationTask(msel.Id, scenarioEvent.Id);
        var triggered = TestData.AutomationTask(msel.Id, other.Id, trigger: AutomationTrigger.OnSuccess, triggerTaskId: trigger.Id);
        await Seed(trigger, triggered);
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        var response = batch
            ? await Client(actor).PostAsJsonAsync("/api/scenarioEvents/batchDelete", new[] { scenarioEvent.Id }, Ct)
            : await Client(actor).DeleteAsync($"/api/scenarioEvents/{scenarioEvent.Id}", Ct);

        Assert.True(response.IsSuccessStatusCode, $"{response.StatusCode}");
        var kept = await ReadBack(db => db.AutomationTasks.SingleAsync(t => t.Id == triggered.Id, Ct));
        Assert.Equal((AutomationTrigger.Manual, null), (kept.TriggerCondition, kept.TriggerTaskId));
        Assert.False(await ReadBack(db => db.AutomationTasks.AnyAsync(t => t.Id == trigger.Id, Ct)));
    }

    /// <summary>An edit cannot take another event's task over by naming it.</summary>
    [Fact]
    public async Task UpdatingAnEvent_WithAnotherEventsTaskId_Is400()
    {
        var (msel, scenarioEvent) = await SeedEvent();
        var others = await SeedTask(msel, await SeedEventOf(msel));
        var actor = await Actor().OnMsel(msel, MselRole.Editor).SeedAsync();

        await AssertStatus(HttpStatusCode.BadRequest, await PutEvent(Client(actor), scenarioEvent, Notification("n", id: others.Id)));
    }

    // ---------------------------------------------------------------------------------------------
    // GET msels/{mselId}/automationTasks, scenarioEvents/{id}/automationTasks, automationActions
    // ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(MselRole.Owner)]
    [InlineData(MselRole.Editor)]
    [InlineData(MselRole.Approver)]
    [InlineData(MselRole.MoveEditor)]
    [InlineData(MselRole.Viewer)]
    [InlineData(MselRole.Evaluator)]
    [InlineData(MselRole.Facilitator)]
    public async Task GetTasks_ForEveryMselRole_ReturnsTheMselsTasks(MselRole role)
    {
        var (msel, scenarioEvent) = await SeedEvent();
        var task = await SeedTask(msel, scenarioEvent);
        await SeedTask(await SeedMselWithEvent());
        var actor = await Actor().OnMsel(msel, role).SeedAsync();

        Assert.Equal(task.Id, Assert.Single(await GetTasks(Client(actor), msel.Id)).Id);
    }

    [Fact]
    public async Task GetTasks_ForTheMselsCreator_Is200()
    {
        var actor = await Actor().SeedAsync();
        var msel = TestData.Msel(createdBy: actor.Id);
        await Seed(msel);

        await AssertStatus(HttpStatusCode.OK, await Client(actor).GetAsync($"/api/msels/{msel.Id}/automationTasks", Ct));
    }

    [Fact]
    public async Task GetTasks_WithViewMsels_Is200()
    {
        var (msel, scenarioEvent) = await SeedEvent();
        await SeedTask(msel, scenarioEvent);
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewMsels).SeedAsync();

        Assert.Single(await GetTasks(Client(actor), msel.Id));
    }

    /// <summary>A participant reads the MSEL through its Exercise View, which shows no automation.</summary>
    [Fact]
    public async Task GetTasks_ForAParticipant_IsEmpty()
    {
        var (msel, scenarioEvent) = await SeedEvent();
        await SeedTask(msel, scenarioEvent);
        var team = TestData.Team(msel.Id);
        await Seed(team);
        var actor = await Actor().OnTeam(team).SeedAsync();

        Assert.Empty(await GetTasks(Client(actor), msel.Id));
    }

    [Fact]
    public async Task GetTasks_is_forbidden_for_an_owner_of_another_msel()
    {
        var (msel, _) = await SeedEvent();
        var actor = await Actor().OnNewMsel(MselRole.Owner).SeedAsync();

        await AssertStatus(HttpStatusCode.Forbidden, await Client(actor).GetAsync($"/api/msels/{msel.Id}/automationTasks", Ct));
    }

    [Fact]
    public async Task GetTasksOfAnEvent_ReturnsThemInOrder()
    {
        var (msel, scenarioEvent) = await SeedEvent();
        var second = TestData.AutomationTask(msel.Id, scenarioEvent.Id, displayOrder: 1);
        var first = TestData.AutomationTask(msel.Id, scenarioEvent.Id, displayOrder: 0);
        await Seed(second, first);
        var actor = await Actor().OnMsel(msel, MselRole.Viewer).SeedAsync();

        var tasks = await ReadAsync<List<AutomationTask>>(await Client(actor).GetAsync($"/api/scenarioEvents/{scenarioEvent.Id}/automationTasks", Ct));

        Assert.Equal([first.Id, second.Id], tasks.Select(t => t.Id));
    }

    [Fact]
    public async Task GetTasksOfAnEvent_is_forbidden_for_a_viewer_of_another_msel()
    {
        var (_, scenarioEvent) = await SeedEvent();
        var actor = await Actor().OnNewMsel(MselRole.Viewer).SeedAsync();

        await AssertStatus(HttpStatusCode.Forbidden, await Client(actor).GetAsync($"/api/scenarioEvents/{scenarioEvent.Id}/automationTasks", Ct));
    }

    [Fact]
    public async Task GetTasksOfAnEventThatIsNotThere_Is404()
    {
        await AssertStatus(HttpStatusCode.NotFound, await RootClient.GetAsync($"/api/scenarioEvents/{Guid.NewGuid()}/automationTasks", Ct));
    }

    [Fact]
    public async Task GetActions_ListsEveryAction()
    {
        var actor = await Actor().SeedAsync();

        var actions = await ReadAsync<List<AutomationActionDefinition>>(await Client(actor).GetAsync("/api/automationActions", Ct));

        Assert.Equal(Enum.GetValues<AutomationAction>().Length, actions.Count);
    }

    // ---------------------------------------------------------------------------------------------
    // POST automationTasks/test
    // ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(MselRole.Owner)]
    [InlineData(MselRole.Editor)]
    [InlineData(MselRole.Approver)]
    public async Task Test_ForARoleThatEditsEvents_MakesATestRun(MselRole role)
    {
        var (msel, scenarioEvent) = await SeedEvent();
        var actor = await Actor().OnMsel(msel, role).SeedAsync();

        var run = await Created(await Test(Client(actor), scenarioEvent, Http("draft")));

        Assert.Equal(AutomationRunKind.Test, run.Kind);
        Assert.Equal(AutomationStatus.Pending, run.Status);
        Assert.Null(run.DeploymentId);
        Assert.Null(run.AutomationTaskId);
        Assert.Equal(actor.Id, run.RequestedBy);
    }

    [Fact]
    public async Task Test_WithEditMsels_MakesATestRun()
    {
        var (_, scenarioEvent) = await SeedEvent();
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();

        await AssertStatus(HttpStatusCode.Created, await Test(Client(actor), scenarioEvent, Http("draft")));
    }

    [Theory]
    [InlineData(MselRole.Facilitator)]
    [InlineData(MselRole.Evaluator)]
    [InlineData(MselRole.Viewer)]
    [InlineData(MselRole.MoveEditor)]
    public async Task Test_is_forbidden_for_a_role_that_does_not_edit_events(MselRole role)
    {
        var (msel, scenarioEvent) = await SeedEvent();
        var actor = await Actor().OnMsel(msel, role).SeedAsync();

        await AssertStatus(HttpStatusCode.Forbidden, await Test(Client(actor), scenarioEvent, Http("draft")));
    }

    [Fact]
    public async Task Test_is_forbidden_for_an_editor_of_another_msel()
    {
        var (_, scenarioEvent) = await SeedEvent();
        var actor = await Actor().OnNewMsel(MselRole.Editor).SeedAsync();

        await AssertStatus(HttpStatusCode.Forbidden, await Test(Client(actor), scenarioEvent, Http("draft")));
    }

    /// <summary>A test acts as Blueprint's service account, so it may use only a Player view the caller can see.</summary>
    [Fact]
    public async Task Test_AgainstAViewTheCallerCannotSee_Is403()
    {
        var (msel, scenarioEvent) = await SeedEvent();
        var actor = await Actor().OnMsel(msel, MselRole.Editor).SeedAsync();
        Factory.PlayerApi.GetUserViewsAsync(actor.Id, Arg.Any<CancellationToken>())
            .Returns(new List<Player.Api.Client.View> { new() { Id = Guid.NewGuid() } });

        // Data-row gate: the views Player answers the caller can see.
        await AssertStatus(HttpStatusCode.Forbidden, await Test(Client(actor), scenarioEvent, Vm("power"), playerViewId: Guid.NewGuid()));
    }

    [Fact]
    public async Task Test_AgainstAViewTheCallerCanSee_UsesIt_AndRemembersIt()
    {
        var (msel, scenarioEvent) = await SeedEvent();
        var actor = await Actor().OnMsel(msel, MselRole.Editor).SeedAsync();
        var viewId = Guid.NewGuid();
        Factory.PlayerApi.GetUserViewsAsync(actor.Id, Arg.Any<CancellationToken>())
            .Returns(new List<Player.Api.Client.View> { new() { Id = viewId } });

        var run = await Created(await Test(Client(actor), scenarioEvent, Vm("power"), playerViewId: viewId));

        Assert.Equal(viewId, run.PlayerViewId);
        Assert.Equal(viewId, (await ReadBack(db => db.Msels.SingleAsync(m => m.Id == msel.Id, Ct))).AutomationTestPlayerViewId);
    }

    /// <summary>The MSEL's own view is always allowed: its contributors test against the exercise they built.</summary>
    [Fact]
    public async Task Test_AgainstTheMselsOwnView_IsAllowed_WithoutAskingPlayer()
    {
        var (msel, scenarioEvent) = await SeedEvent(playerViewId: Guid.NewGuid());
        var actor = await Actor().OnMsel(msel, MselRole.Editor).SeedAsync();

        await AssertStatus(HttpStatusCode.Created, await Test(Client(actor), scenarioEvent, Vm("power"), playerViewId: msel.PlayerViewId));
        await Factory.PlayerApi.DidNotReceive().GetUserViewsAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Test_OfAVmTaskWithNoView_Is400()
    {
        var (msel, scenarioEvent) = await SeedEvent();
        var actor = await Actor().OnMsel(msel, MselRole.Editor).SeedAsync();

        await AssertStatus(HttpStatusCode.BadRequest, await Test(Client(actor), scenarioEvent, Vm("power")));
    }

    /// <summary>A test of a saved task runs with its stored secrets where the form sets none, and can run what it triggers.</summary>
    [Fact]
    public async Task Test_OfASavedTask_UsesItsStoredSecrets_AndCanChain()
    {
        var (msel, scenarioEvent) = await SeedEvent();
        var actor = await Actor().OnMsel(msel, MselRole.Editor).SeedAsync();
        var id = Guid.NewGuid();
        await AssertStatus(HttpStatusCode.OK, await PutEvent(Client(actor), scenarioEvent, Http("saved", id: id, secrets: new() { ["Headers"] = "{}" })));
        var stored = await ReadBack(db => db.AutomationTasks.SingleAsync(t => t.Id == id, Ct));

        var run = await Created(await Test(Client(actor), scenarioEvent, Http("saved", id: id), taskId: id, includeChain: true));

        Assert.Equal(id, run.AutomationTaskId);
        Assert.True(run.TriggerChain);
        var snapshot = AutomationRuns.Snapshot(await ReadBack(db => db.AutomationRuns.SingleAsync(r => r.Id == run.Id, Ct)));
        Assert.Equal(stored.SecretParameters, snapshot.SecretParameters);
    }

    // ---------------------------------------------------------------------------------------------
    // POST msels/{mselId}/automationTasks/vmPreview
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task PreviewVms_ListsTheViewsVmsTheMaskMatches()
    {
        var (msel, _) = await SeedEvent(playerViewId: Guid.NewGuid());
        var actor = await Actor().OnMsel(msel, MselRole.Editor).SeedAsync();
        Factory.PlayerVm.GetAllViewVmsAsync(msel.PlayerViewId.Value, Arg.Any<CancellationToken>()).Returns(new List<Player.Vm.Api.Vm>
        {
            new() { Id = Guid.NewGuid(), Name = "web-01" },
            new() { Id = Guid.NewGuid(), Name = "db-01" },
        });

        var preview = await ReadAsync<AutomationVmPreview>(await PreviewVms(Client(actor), msel.Id, msel.PlayerViewId.Value, "WEB"));

        Assert.Equal("name", preview.MatchMode);
        Assert.Equal(["web-01"], preview.Vms.Select(v => v.Name));
        Assert.Null(preview.Error);
    }

    [Fact]
    public async Task PreviewVms_WhenThePlayerVmApiRefuses_SaysSo()
    {
        var (msel, _) = await SeedEvent(playerViewId: Guid.NewGuid());
        var actor = await Actor().OnMsel(msel, MselRole.Editor).SeedAsync();
        Factory.PlayerVm.GetAllViewVmsAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Throws(new HttpRequestException("403 Forbidden"));

        var preview = await ReadAsync<AutomationVmPreview>(await PreviewVms(Client(actor), msel.Id, msel.PlayerViewId.Value, "web"));

        Assert.Empty(preview.Vms);
        Assert.Contains("403 Forbidden", preview.Error);
    }

    [Fact]
    public async Task PreviewVms_OfAViewTheCallerCannotSee_Is403()
    {
        var (msel, _) = await SeedEvent();
        var actor = await Actor().OnMsel(msel, MselRole.Editor).SeedAsync();
        Factory.PlayerApi.GetUserViewsAsync(actor.Id, Arg.Any<CancellationToken>()).Returns(new List<Player.Api.Client.View>());

        // Data-row gate: the views Player answers the caller can see.
        await AssertStatus(HttpStatusCode.Forbidden, await PreviewVms(Client(actor), msel.Id, Guid.NewGuid(), "web"));
    }

    [Fact]
    public async Task PreviewVms_WithEditMsels_OfTheMselsOwnView_Is200()
    {
        var (msel, _) = await SeedEvent(playerViewId: Guid.NewGuid());
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();
        Factory.PlayerVm.GetAllViewVmsAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(new List<Player.Vm.Api.Vm>());

        await AssertStatus(HttpStatusCode.OK, await PreviewVms(Client(actor), msel.Id, msel.PlayerViewId.Value, "web"));
    }

    [Theory]
    [InlineData(MselRole.Viewer)]
    [InlineData(MselRole.Facilitator)]
    public async Task PreviewVms_is_forbidden_for_a_role_that_does_not_edit_events(MselRole role)
    {
        var (msel, _) = await SeedEvent(playerViewId: Guid.NewGuid());
        var actor = await Actor().OnMsel(msel, role).SeedAsync();

        await AssertStatus(HttpStatusCode.Forbidden, await PreviewVms(Client(actor), msel.Id, msel.PlayerViewId.Value, "web"));
    }

    // ---------------------------------------------------------------------------------------------
    // POST automationTasks/{id}/run, automationRuns/{id}/rerun, automationRuns/{id}/cancel
    // ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(MselRole.Owner)]
    [InlineData(MselRole.Facilitator)]
    public async Task Run_ForAnOwnerOrAFacilitator_MakesAnExerciseRun(MselRole role)
    {
        var (msel, scenarioEvent) = await SeedDeployed();
        var task = await SeedTask(msel, scenarioEvent);
        var actor = await Actor().OnMsel(msel, role).SeedAsync();

        var run = await Created(await Client(actor).PostAsJsonAsync($"/api/automationTasks/{task.Id}/run", new { triggerChain = false }, Ct));

        Assert.Equal(AutomationRunKind.Exercise, run.Kind);
        Assert.Equal(AutomationRunSource.Manual, run.Source);
        Assert.Equal(msel.DeploymentId, run.DeploymentId);
        Assert.Equal(msel.PlayerViewId, run.PlayerViewId);
        Assert.False(run.TriggerChain);
        Assert.Equal(task.Id, run.AutomationTaskId);
    }

    [Fact]
    public async Task Run_ForTheMselsCreator_MakesARun()
    {
        var actor = await Actor().SeedAsync();
        var (msel, scenarioEvent) = await SeedDeployed(createdBy: actor.Id);
        var task = await SeedTask(msel, scenarioEvent);

        await AssertStatus(HttpStatusCode.Created, await RunTask(Client(actor), task.Id));
    }

    [Fact]
    public async Task Run_WithEditMsels_MakesARun()
    {
        var (msel, scenarioEvent) = await SeedDeployed();
        var task = await SeedTask(msel, scenarioEvent);
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();

        await AssertStatus(HttpStatusCode.Created, await RunTask(Client(actor), task.Id));
    }

    [Theory]
    [InlineData(MselRole.Editor)]
    [InlineData(MselRole.Approver)]
    [InlineData(MselRole.Evaluator)]
    [InlineData(MselRole.Viewer)]
    public async Task Run_is_forbidden_for_a_role_that_does_not_run_the_msel(MselRole role)
    {
        var (msel, scenarioEvent) = await SeedDeployed();
        var task = await SeedTask(msel, scenarioEvent);
        var actor = await Actor().OnMsel(msel, role).SeedAsync();

        await AssertStatus(HttpStatusCode.Forbidden, await RunTask(Client(actor), task.Id));
        Assert.False(await ReadBack(db => db.AutomationRuns.AnyAsync(r => r.MselId == msel.Id, Ct)));
    }

    [Fact]
    public async Task Run_is_forbidden_for_a_facilitator_of_another_msel()
    {
        var (msel, scenarioEvent) = await SeedDeployed();
        var task = await SeedTask(msel, scenarioEvent);
        var actor = await Actor().OnNewMsel(MselRole.Facilitator).SeedAsync();

        await AssertStatus(HttpStatusCode.Forbidden, await RunTask(Client(actor), task.Id));
    }

    [Fact]
    public async Task Run_OfAMselThatIsNotDeployed_Is409()
    {
        var (msel, scenarioEvent) = await SeedEvent();
        var task = await SeedTask(msel, scenarioEvent);
        var actor = await Actor().OnMsel(msel, MselRole.Facilitator).SeedAsync();

        await AssertStatus(HttpStatusCode.Conflict, await RunTask(Client(actor), task.Id));
    }

    [Fact]
    public async Task Run_OfADisabledTask_Is409()
    {
        var (msel, scenarioEvent) = await SeedDeployed();
        var task = TestData.AutomationTask(msel.Id, scenarioEvent.Id);
        task.Enabled = false;
        await Seed(task);
        var actor = await Actor().OnMsel(msel, MselRole.Facilitator).SeedAsync();

        await AssertStatus(HttpStatusCode.Conflict, await RunTask(Client(actor), task.Id));
    }

    /// <summary>A task that is not repeatable runs once per deployment.</summary>
    [Fact]
    public async Task Run_OfATaskThatIsNotRepeatable_AfterItSucceeded_Is409_AndARepeatableOneRunsAgain()
    {
        var (msel, scenarioEvent) = await SeedDeployed();
        var once = await SeedTask(msel, scenarioEvent);
        var again = TestData.AutomationTask(msel.Id, scenarioEvent.Id);
        again.Repeatable = true;
        await Seed(again);
        await SeedRun(once, msel, AutomationStatus.Succeeded);
        await SeedRun(again, msel, AutomationStatus.Succeeded);
        var actor = await Actor().OnMsel(msel, MselRole.Facilitator).SeedAsync();

        await AssertStatus(HttpStatusCode.Conflict, await RunTask(Client(actor), once.Id));
        await AssertStatus(HttpStatusCode.Created, await RunTask(Client(actor), again.Id));
    }

    [Fact]
    public async Task Rerun_OfAnEndedRun_MakesARerun()
    {
        var (msel, scenarioEvent) = await SeedDeployed();
        var task = TestData.AutomationTask(msel.Id, scenarioEvent.Id);
        task.Repeatable = true;
        await Seed(task);
        var original = await SeedRun(task, msel, AutomationStatus.Failed);
        var actor = await Actor().OnMsel(msel, MselRole.Facilitator).SeedAsync();

        var run = await Created(await Client(actor).PostAsJsonAsync($"/api/automationRuns/{original.Id}/rerun", new { }, Ct));

        Assert.Equal(AutomationRunSource.Rerun, run.Source);
        Assert.Equal(original.Id, run.RerunOfRunId);
    }

    /// <summary>
    /// A task's delay is the wait once it is triggered, as in Steamfitter, so a run by hand and a rerun wait it too,
    /// as a scheduled, chained or group-start run does.
    /// </summary>
    [Theory]
    [InlineData("run")]
    [InlineData("rerun")]
    public async Task RunByHand_WaitsTheTasksDelay(string how)
    {
        var (msel, scenarioEvent) = await SeedDeployed();
        var task = TestData.AutomationTask(msel.Id, scenarioEvent.Id);
        task.DelaySeconds = 600;
        task.Repeatable = true;
        await Seed(task);
        var actor = await Actor().OnMsel(msel, MselRole.Facilitator).SeedAsync();
        var before = DateTime.UtcNow;

        var response = how == "run"
            ? await RunTask(Client(actor), task.Id)
            : await Client(actor).PostAsJsonAsync($"/api/automationRuns/{(await SeedRun(task, msel, AutomationStatus.Failed)).Id}/rerun", new { }, Ct);

        var run = await Created(response);
        Assert.InRange(run.DueAt, before.AddSeconds(590), DateTime.UtcNow.AddSeconds(610));
    }

    /// <summary>A test runs at once: an author testing a task does not wait out its delay.</summary>
    [Fact]
    public async Task Test_RunsAtOnce_WithoutTheTasksDelay()
    {
        var (msel, scenarioEvent) = await SeedEvent();
        var task = TestData.AutomationTask(msel.Id, scenarioEvent.Id, action: AutomationAction.http_get);
        task.ActionParameters["Url"] = "http://localhost/";
        task.DelaySeconds = 600;
        await Seed(task);
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        var run = await Created(await Client(actor).PostAsJsonAsync("/api/automationTasks/test",
            new { scenarioEventId = scenarioEvent.Id, taskId = task.Id, task = new { name = task.Name, action = "http_get", actionParameters = task.ActionParameters } }, Ct));

        Assert.True(run.DueAt <= DateTime.UtcNow.AddSeconds(5));
    }

    /// <summary>A rerun waits, as a run by hand does, for the task's run that has not ended.</summary>
    [Fact]
    public async Task Rerun_WhileTheTaskRunsAgain_Is409()
    {
        var (msel, scenarioEvent) = await SeedDeployed();
        var task = TestData.AutomationTask(msel.Id, scenarioEvent.Id);
        task.Repeatable = true;
        await Seed(task);
        var ended = await SeedRun(task, msel, AutomationStatus.Failed);
        await SeedRun(task, msel, AutomationStatus.Running);
        var actor = await Actor().OnMsel(msel, MselRole.Facilitator).SeedAsync();

        await AssertStatus(HttpStatusCode.Conflict, await Client(actor).PostAsJsonAsync($"/api/automationRuns/{ended.Id}/rerun", new { }, Ct));
    }

    /// <summary>
    /// Two requests to run one task at once make one run: the second waits for the first's, then finds the task running.
    /// </summary>
    [Fact]
    public async Task Run_TwiceAtOnce_MakesOneRun()
    {
        var (msel, scenarioEvent) = await SeedDeployed();
        var task = TestData.AutomationTask(msel.Id, scenarioEvent.Id);
        task.Repeatable = true;
        await Seed(task);
        var actor = await Actor().OnMsel(msel, MselRole.Facilitator).SeedAsync();

        var responses = await System.Threading.Tasks.Task.WhenAll(
            Enumerable.Range(0, 4).Select(_ => RunTask(Client(actor), task.Id)));

        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.Equal(3, responses.Count(r => r.StatusCode == HttpStatusCode.Conflict));
        Assert.Equal(1, await ReadBack(db => db.AutomationRuns.CountAsync(r => r.AutomationTaskId == task.Id, Ct)));
    }

    /// <summary>A task its author disabled is not rerun either, as it is not run by hand.</summary>
    [Fact]
    public async Task Rerun_OfADisabledTask_Is409()
    {
        var (msel, scenarioEvent) = await SeedDeployed();
        var task = TestData.AutomationTask(msel.Id, scenarioEvent.Id);
        task.Repeatable = true;
        task.Enabled = false;
        await Seed(task);
        var original = await SeedRun(task, msel, AutomationStatus.Failed);
        var actor = await Actor().OnMsel(msel, MselRole.Facilitator).SeedAsync();

        await AssertStatus(HttpStatusCode.Conflict, await Client(actor).PostAsJsonAsync($"/api/automationRuns/{original.Id}/rerun", new { }, Ct));
    }

    [Fact]
    public async Task Rerun_OfARunThatHasNotEnded_Is409()
    {
        var (msel, scenarioEvent) = await SeedDeployed();
        var original = await SeedRun(await SeedTask(msel, scenarioEvent), msel, AutomationStatus.Running);
        var actor = await Actor().OnMsel(msel, MselRole.Facilitator).SeedAsync();

        await AssertStatus(HttpStatusCode.Conflict, await Client(actor).PostAsJsonAsync($"/api/automationRuns/{original.Id}/rerun", new { }, Ct));
    }

    [Fact]
    public async Task Rerun_OfATest_Is400()
    {
        var (msel, scenarioEvent) = await SeedDeployed();
        var test = await SeedRun(await SeedTask(msel, scenarioEvent), msel, AutomationStatus.Failed, AutomationRunKind.Test);
        var actor = await Actor().OnMsel(msel, MselRole.Facilitator).SeedAsync();

        await AssertStatus(HttpStatusCode.BadRequest, await Client(actor).PostAsJsonAsync($"/api/automationRuns/{test.Id}/rerun", new { }, Ct));
    }

    [Fact]
    public async Task Rerun_is_forbidden_for_an_editor()
    {
        var (msel, scenarioEvent) = await SeedDeployed();
        var original = await SeedRun(await SeedTask(msel, scenarioEvent), msel, AutomationStatus.Failed);
        var actor = await Actor().OnMsel(msel, MselRole.Editor).SeedAsync();

        await AssertStatus(HttpStatusCode.Forbidden, await Client(actor).PostAsJsonAsync($"/api/automationRuns/{original.Id}/rerun", new { }, Ct));
    }

    [Fact]
    public async Task Cancel_OfAPendingExerciseRun_ByAFacilitator_CancelsIt()
    {
        var (msel, scenarioEvent) = await SeedDeployed();
        var run = await SeedRun(await SeedTask(msel, scenarioEvent), msel, AutomationStatus.Pending);
        var actor = await Actor().OnMsel(msel, MselRole.Facilitator).SeedAsync();

        var cancelled = await ReadAsync<AutomationRun>(await Client(actor).PostAsync($"/api/automationRuns/{run.Id}/cancel", null, Ct));

        Assert.Equal(AutomationStatus.Cancelled, cancelled.Status);
        Assert.True(cancelled.CancelRequested);
    }

    [Fact]
    public async Task Cancel_OfAnExerciseRun_is_forbidden_for_an_editor()
    {
        var (msel, scenarioEvent) = await SeedDeployed();
        var run = await SeedRun(await SeedTask(msel, scenarioEvent), msel, AutomationStatus.Pending);
        var actor = await Actor().OnMsel(msel, MselRole.Editor).SeedAsync();

        await AssertStatus(HttpStatusCode.Forbidden, await Client(actor).PostAsync($"/api/automationRuns/{run.Id}/cancel", null, Ct));
    }

    /// <summary>A test is cancelled by whoever may test, not by whoever runs the exercise.</summary>
    [Fact]
    public async Task Cancel_OfATest_IsForAnEditor_AndForbiddenForAFacilitator()
    {
        var (msel, scenarioEvent) = await SeedEvent();
        var task = await SeedTask(msel, scenarioEvent);
        var test = await SeedRun(task, msel, AutomationStatus.Running, AutomationRunKind.Test);
        var facilitator = await Actor().OnMsel(msel, MselRole.Facilitator).SeedAsync();
        var editor = await Actor().OnMsel(msel, MselRole.Editor).SeedAsync();

        await AssertStatus(HttpStatusCode.Forbidden, await Client(facilitator).PostAsync($"/api/automationRuns/{test.Id}/cancel", null, Ct));
        await AssertStatus(HttpStatusCode.OK, await Client(editor).PostAsync($"/api/automationRuns/{test.Id}/cancel", null, Ct));
        Assert.True((await ReadBack(db => db.AutomationRuns.SingleAsync(r => r.Id == test.Id, Ct))).CancelRequested);
    }

    [Fact]
    public async Task Cancel_OfAnEndedRun_Is409()
    {
        var (msel, scenarioEvent) = await SeedDeployed();
        var run = await SeedRun(await SeedTask(msel, scenarioEvent), msel, AutomationStatus.Succeeded);
        var actor = await Actor().OnMsel(msel, MselRole.Facilitator).SeedAsync();

        await AssertStatus(HttpStatusCode.Conflict, await Client(actor).PostAsync($"/api/automationRuns/{run.Id}/cancel", null, Ct));
    }

    // ---------------------------------------------------------------------------------------------
    // GET msels/{mselId}/automationRuns, automationRuns/{id}
    // ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(MselRole.Owner)]
    [InlineData(MselRole.Facilitator)]
    [InlineData(MselRole.Editor)]
    [InlineData(MselRole.Approver)]
    public async Task GetRuns_ForARoleThatWatchesTheFacilitatorView_ReturnsTheMselsRunsWithResults(MselRole role)
    {
        var (msel, scenarioEvent) = await SeedDeployed();
        var run = await SeedRun(await SeedTask(msel, scenarioEvent), msel, AutomationStatus.Succeeded);
        await Seed(new AutomationResultEntity { Id = Guid.NewGuid(), RunId = run.Id, Iteration = 1, Status = AutomationStatus.Succeeded, ActualOutput = "ok" });
        var actor = await Actor().OnMsel(msel, role).SeedAsync();

        var runs = await ReadAsync<List<AutomationRun>>(await Client(actor).GetAsync($"/api/msels/{msel.Id}/automationRuns", Ct));

        Assert.Equal("ok", Assert.Single(Assert.Single(runs).Results).ActualOutput);
    }

    [Theory]
    [InlineData(MselRole.Evaluator)]
    [InlineData(MselRole.Viewer)]
    [InlineData(MselRole.MoveEditor)]
    public async Task GetRuns_is_forbidden_for_a_role_that_does_not_watch_the_facilitator_view(MselRole role)
    {
        var (msel, _) = await SeedDeployed();
        var actor = await Actor().OnMsel(msel, role).SeedAsync();

        await AssertStatus(HttpStatusCode.Forbidden, await Client(actor).GetAsync($"/api/msels/{msel.Id}/automationRuns", Ct));
    }

    [Fact]
    public async Task GetRuns_is_forbidden_for_a_participant()
    {
        var (msel, _) = await SeedDeployed();
        var team = TestData.Team(msel.Id);
        await Seed(team);
        var actor = await Actor().OnTeam(team).SeedAsync();

        await AssertStatus(HttpStatusCode.Forbidden, await Client(actor).GetAsync($"/api/msels/{msel.Id}/automationRuns", Ct));
    }

    [Fact]
    public async Task GetRuns_is_forbidden_for_a_facilitator_of_another_msel()
    {
        var (msel, _) = await SeedDeployed();
        var actor = await Actor().OnNewMsel(MselRole.Facilitator).SeedAsync();

        await AssertStatus(HttpStatusCode.Forbidden, await Client(actor).GetAsync($"/api/msels/{msel.Id}/automationRuns", Ct));
    }

    [Fact]
    public async Task GetRuns_ForTheMselsCreator_AndWithViewMsels_Is200()
    {
        var creator = await Actor().SeedAsync();
        var (msel, _) = await SeedDeployed(createdBy: creator.Id);
        var viewer = await Actor().WithSystemPermissions(SystemPermission.ViewMsels).SeedAsync();

        await AssertStatus(HttpStatusCode.OK, await Client(creator).GetAsync($"/api/msels/{msel.Id}/automationRuns", Ct));
        await AssertStatus(HttpStatusCode.OK, await Client(viewer).GetAsync($"/api/msels/{msel.Id}/automationRuns", Ct));
    }

    [Fact]
    public async Task GetRuns_OfOneKind_ReturnsOnlyThose()
    {
        var (msel, scenarioEvent) = await SeedDeployed();
        var task = await SeedTask(msel, scenarioEvent);
        var exercise = await SeedRun(task, msel, AutomationStatus.Succeeded);
        await SeedRun(task, msel, AutomationStatus.Succeeded, AutomationRunKind.Test);
        var actor = await Actor().OnMsel(msel, MselRole.Facilitator).SeedAsync();

        var runs = await ReadAsync<List<AutomationRun>>(await Client(actor).GetAsync($"/api/msels/{msel.Id}/automationRuns?kind=Exercise", Ct));

        Assert.Equal(exercise.Id, Assert.Single(runs).Id);
    }

    [Fact]
    public async Task GetRun_ForAWatcher_IsTheRun_AndForAnEvaluatorIs403()
    {
        var (msel, scenarioEvent) = await SeedDeployed();
        var run = await SeedRun(await SeedTask(msel, scenarioEvent), msel, AutomationStatus.Succeeded);
        var approver = await Actor().OnMsel(msel, MselRole.Approver).SeedAsync();
        var evaluator = await Actor().OnMsel(msel, MselRole.Evaluator).SeedAsync();

        Assert.Equal(run.Id, (await ReadAsync<AutomationRun>(await Client(approver).GetAsync($"/api/automationRuns/{run.Id}", Ct))).Id);
        await AssertStatus(HttpStatusCode.Forbidden, await Client(evaluator).GetAsync($"/api/automationRuns/{run.Id}", Ct));
    }

    // ---------------------------------------------------------------------------------------------
    // the Facilitator View: execution, starting moves and groups, pausing
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task GetExecution_IsWhereTheMselIs_AndWhereEachEventFalls()
    {
        var (msel, scenarioEvent) = await SeedDeployed();
        await Seed(TestData.Move(msel.Id, moveNumber: 1), TestData.Move(msel.Id, moveNumber: 2, deltaSeconds: 600));
        var late = TestData.ScenarioEvent(msel.Id, deltaSeconds: 900);
        await Seed(late);
        var actor = await Actor().OnMsel(msel, MselRole.Editor).SeedAsync();

        var execution = await ReadAsync<MselExecution>(await Client(actor).GetAsync($"/api/msels/{msel.Id}/execution", Ct));

        Assert.Equal(msel.DeploymentId, execution.DeploymentId);
        Assert.Equal(1, execution.Positions.Single(p => p.ScenarioEventId == scenarioEvent.Id).MoveNumber);
        Assert.Equal(2, execution.Positions.Single(p => p.ScenarioEventId == late.Id).MoveNumber);
    }

    [Fact]
    public async Task GetExecution_is_forbidden_for_an_evaluator()
    {
        var (msel, _) = await SeedDeployed();
        var actor = await Actor().OnMsel(msel, MselRole.Evaluator).SeedAsync();

        await AssertStatus(HttpStatusCode.Forbidden, await Client(actor).GetAsync($"/api/msels/{msel.Id}/execution", Ct));
    }

    [Fact]
    public async Task StartMove_MovesCiteAndGallery_RecordsIt_AndRunsTheChosenTasksInOrder()
    {
        var (msel, scenarioEvent) = await SeedDeployed(useIntegrations: true);
        await Seed(TestData.Move(msel.Id, moveNumber: 1), TestData.Move(msel.Id, moveNumber: 2, deltaSeconds: 600));
        var first = await SeedTask(msel, scenarioEvent);
        var second = await SeedTask(msel, scenarioEvent);
        var actor = await Actor().OnMsel(msel, MselRole.Facilitator).SeedAsync();
        Factory.Cite.SetEvaluationCurrentMoveAsync(msel.CiteEvaluationId.Value, 2, Arg.Any<CancellationToken>())
            .Returns(new Cite.Api.Client.Evaluation { Id = msel.CiteEvaluationId.Value, CurrentMoveNumber = 2 });
        Factory.Gallery.SetExhibitMoveAndInjectAsync(msel.GalleryExhibitId.Value, 2, 0, Arg.Any<CancellationToken>())
            .Returns(new Gallery.Api.Client.Exhibit { Id = msel.GalleryExhibitId.Value, CurrentMove = 2, CurrentInject = 0 });

        var result = await ReadAsync<MselAdvanceResult>(await Client(actor).PostAsJsonAsync(
            $"/api/msels/{msel.Id}/execution/moves/2/start", new { runTaskIds = new[] { second.Id, first.Id } }, Ct));

        Assert.True(result.Succeeded);
        Assert.Equal(2, result.Execution.CurrentMoveNumber);
        Assert.Equal(0, result.Execution.CurrentGroupNumber);
        Assert.Equal(second.Id, result.FirstRun.AutomationTaskId);
        var firstRun = await ReadBack(db => db.AutomationRuns.SingleAsync(r => r.Id == result.FirstRun.Id, Ct));
        Assert.Equal(first.Id.ToString(), firstRun.FollowOnTaskIds);
        var record = await ReadBack(db => db.AutomationRuns.SingleAsync(r => r.MselId == msel.Id && r.AutomationTaskId == null, Ct));
        Assert.Equal(AutomationRunSource.GroupStart, record.Source);
        Assert.Equal(AutomationStatus.Succeeded, record.Status);
    }

    /// <summary>
    /// A start leaves out a chosen task that runs only once and has succeeded in this deployment, as a run by hand would
    /// refuse it, and says so; the rest still run, and a repeatable task runs again.
    /// </summary>
    [Fact]
    public async Task StartMove_LeavesOutATaskThatHasSucceededAndRunsOnce()
    {
        var (msel, scenarioEvent) = await SeedDeployed();
        await Seed(TestData.Move(msel.Id, moveNumber: 1));
        var once = await SeedTask(msel, scenarioEvent);
        once.Name = "Once";
        var again = await SeedTask(msel, scenarioEvent);
        await ReadBack(async db =>
        {
            db.AutomationTasks.Attach(once).Property(t => t.Name).IsModified = true;
            db.AutomationTasks.Attach(again).Entity.Repeatable = true;
            db.Entry(again).Property(t => t.Repeatable).IsModified = true;
            return await db.SaveChangesAsync(Ct);
        });
        await SeedRun(once, msel, AutomationStatus.Succeeded);
        await SeedRun(again, msel, AutomationStatus.Succeeded);
        var actor = await Actor().OnMsel(msel, MselRole.Facilitator).SeedAsync();

        var result = await ReadAsync<MselAdvanceResult>(await Client(actor).PostAsJsonAsync(
            $"/api/msels/{msel.Id}/execution/moves/1/start", new { runTaskIds = new[] { once.Id, again.Id } }, Ct));

        Assert.Equal(again.Id, result.FirstRun.AutomationTaskId);
        Assert.Contains("Once has succeeded and runs only once, so it was not run again.", result.Messages);
        var record = await ReadBack(db => db.AutomationRuns.SingleAsync(r => r.MselId == msel.Id && r.AutomationTaskId == null, Ct));
        Assert.Contains("Once has succeeded", record.StatusDetail);
    }

    /// <summary>A group start moves Gallery only: CITE follows moves.</summary>
    [Fact]
    public async Task StartGroup_MovesGalleryOnly()
    {
        var (msel, _) = await SeedDeployed(useIntegrations: true);
        await Seed(TestData.Move(msel.Id, moveNumber: 1));
        await Seed(TestData.ScenarioEvent(msel.Id, deltaSeconds: 300));
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();
        Factory.Gallery.SetExhibitMoveAndInjectAsync(msel.GalleryExhibitId.Value, 1, 1, Arg.Any<CancellationToken>())
            .Returns(new Gallery.Api.Client.Exhibit { Id = msel.GalleryExhibitId.Value, CurrentMove = 1, CurrentInject = 1 });

        var result = await ReadAsync<MselAdvanceResult>(await Client(actor).PostAsJsonAsync(
            $"/api/msels/{msel.Id}/execution/moves/1/groups/1/start", new { }, Ct));

        Assert.Equal(1, result.Execution.CurrentGroupNumber);
        await Factory.Cite.DidNotReceive().SetEvaluationCurrentMoveAsync(Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    /// <summary>A failed integration is reported, and the facilitator's position still moves on.</summary>
    [Fact]
    public async Task StartMove_WhenCiteRefuses_SaysSo_AndStillMoves()
    {
        var (msel, _) = await SeedDeployed(useIntegrations: true);
        await Seed(TestData.Move(msel.Id, moveNumber: 1));
        var actor = await Actor().OnMsel(msel, MselRole.Facilitator).SeedAsync();
        Factory.Cite.SetEvaluationCurrentMoveAsync(Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Throws(new HttpRequestException("CITE is down"));
        Factory.Gallery.SetExhibitMoveAndInjectAsync(Arg.Any<Guid>(), 1, 0, Arg.Any<CancellationToken>())
            .Returns(new Gallery.Api.Client.Exhibit { CurrentMove = 1, CurrentInject = 0 });

        var result = await ReadAsync<MselAdvanceResult>(await Client(actor).PostAsJsonAsync($"/api/msels/{msel.Id}/execution/moves/1/start", new { }, Ct));

        Assert.False(result.Succeeded);
        Assert.Contains(result.Messages, m => m.Contains("CITE is down"));
        Assert.Equal(1, result.Execution.CurrentMoveNumber);
    }

    [Theory]
    [InlineData(MselRole.Editor)]
    [InlineData(MselRole.Approver)]
    [InlineData(MselRole.Evaluator)]
    public async Task StartMove_is_forbidden_for_a_role_that_does_not_run_the_msel(MselRole role)
    {
        var (msel, _) = await SeedDeployed();
        var actor = await Actor().OnMsel(msel, role).SeedAsync();

        await AssertStatus(HttpStatusCode.Forbidden, await Client(actor).PostAsJsonAsync($"/api/msels/{msel.Id}/execution/moves/0/start", new { }, Ct));
    }

    [Fact]
    public async Task StartMove_ToAMoveTheMselDoesNotHave_Is400()
    {
        var (msel, _) = await SeedDeployed();
        await Seed(TestData.Move(msel.Id, moveNumber: 1));
        var actor = await Actor().OnMsel(msel, MselRole.Facilitator).SeedAsync();

        await AssertStatus(HttpStatusCode.BadRequest, await Client(actor).PostAsJsonAsync($"/api/msels/{msel.Id}/execution/moves/7/start", new { }, Ct));
    }

    [Fact]
    public async Task StartMove_RunningATaskThatIsNotManual_Is400()
    {
        var (msel, scenarioEvent) = await SeedDeployed();
        var scheduled = TestData.AutomationTask(msel.Id, scenarioEvent.Id, trigger: AutomationTrigger.AtEventTime);
        await Seed(scheduled);
        var actor = await Actor().OnMsel(msel, MselRole.Facilitator).SeedAsync();

        await AssertStatus(HttpStatusCode.BadRequest, await Client(actor).PostAsJsonAsync(
            $"/api/msels/{msel.Id}/execution/moves/0/start", new { runTaskIds = new[] { scheduled.Id } }, Ct));
    }

    [Fact]
    public async Task StartMove_OfAMselThatIsNotDeployed_Is409()
    {
        var (msel, _) = await SeedEvent();
        var actor = await Actor().OnMsel(msel, MselRole.Facilitator).SeedAsync();

        await AssertStatus(HttpStatusCode.Conflict, await Client(actor).PostAsJsonAsync($"/api/msels/{msel.Id}/execution/moves/0/start", new { }, Ct));
    }

    [Fact]
    public async Task Pausing_StopsTheScheduler_AndIsForTheFacilitatorOnly()
    {
        var (msel, _) = await SeedDeployed();
        var facilitator = await Actor().OnMsel(msel, MselRole.Facilitator).SeedAsync();
        var editor = await Actor().OnMsel(msel, MselRole.Editor).SeedAsync();

        await AssertStatus(HttpStatusCode.Forbidden, await Client(editor).PutAsJsonAsync($"/api/msels/{msel.Id}/execution/paused", new { paused = true }, Ct));
        var execution = await ReadAsync<MselExecution>(await Client(facilitator).PutAsJsonAsync($"/api/msels/{msel.Id}/execution/paused", new { paused = true }, Ct));

        Assert.True(execution.AutomationPaused);
        Assert.True((await ReadBack(db => db.Msels.SingleAsync(m => m.Id == msel.Id, Ct))).AutomationPaused);
    }

    // ---------------------------------------------------------------------------------------------
    // helpers
    // ---------------------------------------------------------------------------------------------

    private async Task<(MselEntity, ScenarioEventEntity)> SeedEvent(Guid? playerViewId = null)
    {
        var msel = TestData.Msel();
        msel.PlayerViewId = playerViewId;
        await Seed(msel);
        return (msel, await SeedEventOf(msel));
    }

    private async Task<ScenarioEventEntity> SeedEventOf(MselEntity msel)
    {
        var scenarioEvent = TestData.ScenarioEvent(msel.Id);
        await Seed(scenarioEvent);
        return scenarioEvent;
    }

    private async Task<ScenarioEventEntity> SeedMselWithEvent() => (await SeedEvent()).Item2;

    private async Task<(MselEntity, ScenarioEventEntity)> SeedDeployed(Guid? createdBy = null, bool useIntegrations = false)
    {
        var msel = TestData.Msel(createdBy: createdBy, status: MselItemStatus.Deployed);
        msel.DeploymentId = Guid.NewGuid();
        msel.PlayerViewId = Guid.NewGuid();
        if (useIntegrations)
        {
            msel.UseCite = true;
            msel.CiteEvaluationId = Guid.NewGuid();
            msel.UseGallery = true;
            msel.GalleryExhibitId = Guid.NewGuid();
        }
        await Seed(msel);
        return (msel, await SeedEventOf(msel));
    }

    private async Task<AutomationTaskEntity> SeedTask(MselEntity msel, ScenarioEventEntity scenarioEvent)
    {
        var task = TestData.AutomationTask(msel.Id, scenarioEvent.Id);
        await Seed(task);
        return task;
    }

    private async Task<AutomationTaskEntity> SeedTask(ScenarioEventEntity scenarioEvent)
    {
        var task = TestData.AutomationTask(scenarioEvent.MselId, scenarioEvent.Id);
        await Seed(task);
        return task;
    }

    private async Task<AutomationRunEntity> SeedRun(AutomationTaskEntity task, MselEntity msel, AutomationStatus status,
        AutomationRunKind kind = AutomationRunKind.Exercise)
    {
        var run = AutomationRuns.New(task, msel.Id, kind, AutomationRunSource.Manual, DateTime.UtcNow, msel.DeploymentId, msel.PlayerViewId, null);
        run.Status = status;
        await Seed(run);
        return run;
    }

    private static object Task(string name, string action, Dictionary<string, string> parameters, Guid? id = null,
        AutomationTrigger trigger = AutomationTrigger.Manual, Guid? triggerTaskId = null, Dictionary<string, string> secrets = null,
        string vmMask = null, int iterations = 1) => new
        {
            id,
            name,
            action,
            actionParameters = parameters,
            secretParameters = secrets ?? new(),
            vmMask,
            iterations,
            triggerCondition = trigger.ToString(),
            triggerTaskId,
            enabled = true,
        };

    private static object Notification(string name, Guid? id = null, AutomationTrigger trigger = AutomationTrigger.Manual,
        Guid? triggerTaskId = null, string text = "Move 2 begins", int iterations = 1) =>
        Task(name, "player_notification", text == null ? new() : new() { ["Text"] = text }, id, trigger, triggerTaskId, iterations: iterations);

    private static object Http(string name, Guid? id = null, Dictionary<string, string> secrets = null) =>
        Task(name, "http_get", new() { ["Url"] = "http://localhost:4720/api/health" }, id, secrets: secrets);

    private static object Vm(string name) => Task(name, "vm_hw_power_on", new(), vmMask: "web");

    private Task<HttpResponseMessage> PutEvent(HttpClient client, ScenarioEventEntity scenarioEvent, params object[] tasks) =>
        client.PutAsJsonAsync($"/api/scenarioEvents/{scenarioEvent.Id}", new
        {
            groupOrder = scenarioEvent.GroupOrder,
            deltaSeconds = scenarioEvent.DeltaSeconds,
            scenarioEventType = "Inject",
            dataValues = Array.Empty<object>(),
            automationTasks = tasks,
        }, Ct);

    private Task<HttpResponseMessage> Test(HttpClient client, ScenarioEventEntity scenarioEvent, object task, Guid? playerViewId = null,
        Guid? taskId = null, bool includeChain = false) =>
        client.PostAsJsonAsync("/api/automationTasks/test", new { scenarioEventId = scenarioEvent.Id, taskId, task, playerViewId, includeChain }, Ct);

    private Task<HttpResponseMessage> PreviewVms(HttpClient client, Guid mselId, Guid playerViewId, string vmMask) =>
        client.PostAsJsonAsync($"/api/msels/{mselId}/automationTasks/vmPreview", new { playerViewId, vmMask }, Ct);

    private Task<HttpResponseMessage> RunTask(HttpClient client, Guid taskId) =>
        client.PostAsJsonAsync($"/api/automationTasks/{taskId}/run", new { }, Ct);

    private async Task<List<AutomationTask>> GetTasks(HttpClient client, Guid mselId) =>
        await ReadAsync<List<AutomationTask>>(await client.GetAsync($"/api/msels/{mselId}/automationTasks", Ct));

    private static async Task<AutomationRun> Created(HttpResponseMessage response)
    {
        await AssertStatus(HttpStatusCode.Created, response);
        return await ReadAsync<AutomationRun>(response);
    }
}
