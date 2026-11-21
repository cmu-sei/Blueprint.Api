// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Blueprint.Api.Automation;
using Blueprint.Api.Data;
using Blueprint.Api.Data.Enumerations;
using Blueprint.Api.Data.Models;
using Blueprint.Api.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace Blueprint.Api.Tests.Automation;

/// <summary>
/// The automation engine over a real database: the scheduler's passes and one iteration of a run at a time, with the
/// actions and the VM listing substituted. The dispatcher only loops over these.
/// </summary>
public class AutomationEngineTests(DatabaseFixture fixture) : DatabaseTestBase(fixture)
{
    private readonly TestClock _clock = new();
    private readonly AutomationRunRegistry _registry = new();
    private readonly IAutomationActionRunner _runner = Substitute.For<IAutomationActionRunner>();
    private readonly IAutomationVmResolver _vms = Substitute.For<IAutomationVmResolver>();
    private AutomationOptions Options { get; } = new() { LeaseSeconds = 60, MissedScheduleGraceSeconds = 300, TestRunsKeptPerTask = 2 };

    private AutomationScheduler Scheduler(BlueprintContext db = null, AutomationRunRegistry registry = null) =>
        new(db ?? Db, registry ?? _registry, AutomationTestOptions.Monitor(Options), _clock, NullLogger<AutomationScheduler>.Instance);

    private AutomationRunExecutor Executor(IAutomationSecretProtector secrets = null)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => NewContext());
        return new AutomationRunExecutor(Db, new AutomationActionCatalog(), _runner, _vms,
            secrets ?? new AutomationSecretProtector(AutomationTestOptions.Automation()), _registry,
            services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            AutomationTestOptions.Monitor(Options), _clock, NullLogger<AutomationRunExecutor>.Instance);
    }

    /// <summary>A deployed MSEL that started ten minutes ago, with one scenario event at its start.</summary>
    private async Task<(MselEntity Msel, ScenarioEventEntity Event)> Deployed(int deltaSeconds = 0)
    {
        var msel = TestData.Msel(status: MselItemStatus.Deployed);
        msel.DeploymentId = Guid.NewGuid();
        msel.PlayerViewId = Guid.NewGuid();
        msel.StartTime = _clock.UtcNow.AddMinutes(-10);
        await Seed(msel);
        var scenarioEvent = TestData.ScenarioEvent(msel.Id, deltaSeconds);
        await Seed(scenarioEvent);
        return (msel, scenarioEvent);
    }

    private async Task<AutomationRunEntity> Run(AutomationTaskEntity task, MselEntity msel,
        AutomationRunKind kind = AutomationRunKind.Exercise, AutomationRunSource source = AutomationRunSource.Manual, bool triggerChain = true,
        IEnumerable<Guid> followOn = null)
    {
        var run = AutomationRuns.New(task, msel.Id, kind, source, _clock.UtcNow, msel.DeploymentId, msel.PlayerViewId, Guid.NewGuid(),
            triggerChain: triggerChain, followOnTaskIds: followOn);
        await Seed(run);
        return run;
    }

    /// <summary>Claims what is due and runs one iteration of each claimed run, until nothing is due.</summary>
    private async Task Drain(int passes = 10)
    {
        for (var i = 0; i < passes; i++)
        {
            var claimed = await Scheduler().ClaimAsync(10, Ct);
            if (claimed.Count == 0)
                return;
            foreach (var id in claimed)
                await Execute(id);
        }
    }

    /// <summary>
    /// Runs one iteration over a cold change tracker, as production's fresh scope does: a run seeded through
    /// <c>Db</c> stays tracked with the values it was seeded with.
    /// </summary>
    private async Task Execute(Guid runId, IAutomationSecretProtector secrets = null)
    {
        Db.ChangeTracker.Clear();
        await Executor(secrets).ExecuteIterationAsync(runId, Ct);
        Db.ChangeTracker.Clear();
    }

    private Task<AutomationRunEntity> Stored(Guid runId) =>
        ReadBack(db => db.AutomationRuns.Include(r => r.Results).SingleAsync(r => r.Id == runId, Ct));

    private void Answers(string output, bool? succeeded = null) =>
        _runner.ExecuteAsync(Arg.Any<AutomationActionContext>(), Arg.Any<CancellationToken>()).Returns(new ActionOutcome(output, succeeded));

    // ---------------------------------------------------------------------------------------------
    // scheduling
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Schedule_MakesARun_ForAnAtEventTimeTaskWhoseTimeHasCome()
    {
        var (msel, scenarioEvent) = await Deployed(deltaSeconds: 595);
        var task = TestData.AutomationTask(msel.Id, scenarioEvent.Id, trigger: AutomationTrigger.AtEventTime);
        task.DelaySeconds = 5;
        await Seed(task);

        Assert.Equal(1, await Scheduler().ScheduleAsync(Ct));

        var run = await ReadBack(db => db.AutomationRuns.SingleAsync(r => r.AutomationTaskId == task.Id, Ct));
        Assert.Equal(AutomationStatus.Pending, run.Status);
        Assert.Equal(AutomationRunSource.Schedule, run.Source);
        Assert.Equal(msel.DeploymentId, run.DeploymentId);
        Assert.Equal(msel.PlayerViewId, run.PlayerViewId);
        Assert.Equal(msel.StartTime.AddSeconds(600), run.DueAt);
    }

    [Fact]
    public async Task Schedule_MakesNothing_BeforeTheTasksTime()
    {
        var (msel, scenarioEvent) = await Deployed(deltaSeconds: 3600);
        await Seed(TestData.AutomationTask(msel.Id, scenarioEvent.Id, trigger: AutomationTrigger.AtEventTime));

        Assert.Equal(0, await Scheduler().ScheduleAsync(Ct));
    }

    [Theory]
    [InlineData("paused")]
    [InlineData("not deployed")]
    [InlineData("no deployment")]
    [InlineData("disabled")]
    [InlineData("manual")]
    public async Task Schedule_MakesNothing_ForATaskItDoesNotRun(string why)
    {
        var (msel, scenarioEvent) = await Deployed();
        var task = TestData.AutomationTask(msel.Id, scenarioEvent.Id, trigger: why == "manual" ? AutomationTrigger.Manual : AutomationTrigger.AtEventTime);
        task.Enabled = why != "disabled";
        await Seed(task);
        await Db.Msels.Where(m => m.Id == msel.Id).ExecuteUpdateAsync(s => s
            .SetProperty(m => m.AutomationPaused, why == "paused")
            .SetProperty(m => m.Status, why == "not deployed" ? MselItemStatus.Approved : MselItemStatus.Deployed)
            .SetProperty(m => m.DeploymentId, why == "no deployment" ? null : msel.DeploymentId), Ct);

        Assert.Equal(0, await Scheduler().ScheduleAsync(Ct));
    }

    /// <summary>A task runs at its time once per deployment, however many passes or instances schedule it.</summary>
    [Fact]
    public async Task Schedule_FromTwoInstances_MakesOneRun()
    {
        var (msel, scenarioEvent) = await Deployed();
        var task = TestData.AutomationTask(msel.Id, scenarioEvent.Id, trigger: AutomationTrigger.AtEventTime);
        await Seed(task);
        await using var other = NewContext();

        var made = await Task.WhenAll(Scheduler().ScheduleAsync(Ct), Scheduler(other, new AutomationRunRegistry()).ScheduleAsync(Ct));
        made = made.Append(await Scheduler().ScheduleAsync(Ct)).ToArray();

        Assert.Equal(1, made.Sum());
        Assert.Equal(1, await ReadBack(db => db.AutomationRuns.CountAsync(r => r.AutomationTaskId == task.Id, Ct)));
    }

    /// <summary>A new deployment runs the task again.</summary>
    [Fact]
    public async Task Schedule_InANewDeployment_MakesAnotherRun()
    {
        var (msel, scenarioEvent) = await Deployed();
        var task = TestData.AutomationTask(msel.Id, scenarioEvent.Id, trigger: AutomationTrigger.AtEventTime);
        await Seed(task);
        await Scheduler().ScheduleAsync(Ct);
        await Db.Msels.Where(m => m.Id == msel.Id).ExecuteUpdateAsync(s => s.SetProperty(m => m.DeploymentId, Guid.NewGuid()), Ct);

        Assert.Equal(1, await Scheduler().ScheduleAsync(Ct));
    }

    [Fact]
    public async Task Schedule_ForATaskPastTheGracePeriod_RecordsItSkipped()
    {
        var (msel, scenarioEvent) = await Deployed();
        Options.MissedScheduleGraceSeconds = 60;
        var task = TestData.AutomationTask(msel.Id, scenarioEvent.Id, trigger: AutomationTrigger.AtEventTime);
        await Seed(task);

        await Scheduler().ScheduleAsync(Ct);

        var run = await ReadBack(db => db.AutomationRuns.SingleAsync(r => r.AutomationTaskId == task.Id, Ct));
        Assert.Equal(AutomationStatus.Skipped, run.Status);
        Assert.NotNull(run.EndedAt);
        Assert.Empty(await Scheduler().ClaimAsync(10, Ct));
    }

    // ---------------------------------------------------------------------------------------------
    // claiming and recovering
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Claim_TakesADueRunOnce_AcrossInstances()
    {
        var (msel, scenarioEvent) = await Deployed();
        var task = TestData.AutomationTask(msel.Id, scenarioEvent.Id);
        await Seed(task);
        var run = await Run(task, msel);
        await using var other = NewContext();

        var claims = await Task.WhenAll(Scheduler().ClaimAsync(10, Ct), Scheduler(other, new AutomationRunRegistry()).ClaimAsync(10, Ct));

        Assert.Equal([run.Id], claims.SelectMany(c => c));
        var stored = await Stored(run.Id);
        Assert.Equal(AutomationStatus.Queued, stored.Status);
        Assert.NotNull(stored.LeaseOwner);
    }

    [Fact]
    public async Task Claim_LeavesARunThatIsNotDueYet()
    {
        var (msel, scenarioEvent) = await Deployed();
        var task = TestData.AutomationTask(msel.Id, scenarioEvent.Id);
        await Seed(task);
        var run = await Run(task, msel);
        await Db.AutomationRuns.Where(r => r.Id == run.Id).ExecuteUpdateAsync(s => s.SetProperty(r => r.DueAt, _clock.UtcNow.AddMinutes(1)), Ct);

        Assert.Empty(await Scheduler().ClaimAsync(10, Ct));
    }

    [Fact]
    public async Task Recover_QueuesAgain_ARunAStoppedInstanceHadOnlyQueued()
    {
        var (msel, scenarioEvent) = await Deployed();
        var task = TestData.AutomationTask(msel.Id, scenarioEvent.Id);
        await Seed(task);
        var run = await Run(task, msel);
        await Scheduler(registry: new AutomationRunRegistry()).ClaimAsync(10, Ct);
        _clock.Advance(TimeSpan.FromMinutes(5));

        await Scheduler().RecoverAsync(Ct);

        Assert.Equal(AutomationStatus.Pending, (await Stored(run.Id)).Status);
        Assert.Equal([run.Id], await Scheduler().ClaimAsync(10, Ct));
    }

    /// <summary>A run that was running when its instance stopped may have taken effect, so it is not run again.</summary>
    [Fact]
    public async Task Recover_RecordsAnError_ForARunAStoppedInstanceWasRunning_AndTriggersNothing()
    {
        var (msel, scenarioEvent) = await Deployed();
        var task = TestData.AutomationTask(msel.Id, scenarioEvent.Id);
        await Seed(task);
        await Seed(TestData.AutomationTask(msel.Id, scenarioEvent.Id, trigger: AutomationTrigger.OnCompletion, triggerTaskId: task.Id));
        var run = await Run(task, msel);
        await Db.AutomationRuns.Where(r => r.Id == run.Id).ExecuteUpdateAsync(s => s
            .SetProperty(r => r.Status, AutomationStatus.Running)
            .SetProperty(r => r.LeaseOwner, "gone")
            .SetProperty(r => r.LeaseUntil, _clock.UtcNow.AddSeconds(-1)), Ct);
        await Seed(new AutomationResultEntity { Id = Guid.NewGuid(), RunId = run.Id, Iteration = 1, Status = AutomationStatus.Running });

        await Scheduler().RecoverAsync(Ct);

        var stored = await Stored(run.Id);
        Assert.Equal(AutomationStatus.Error, stored.Status);
        Assert.Equal(AutomationStatus.Error, Assert.Single(stored.Results).Status);
        Assert.Equal(1, await ReadBack(db => db.AutomationRuns.CountAsync(r => r.MselId == msel.Id, Ct)));
    }

    [Fact]
    public async Task Retain_KeepsEachTasksNewestTestRuns()
    {
        var (msel, scenarioEvent) = await Deployed();
        var task = TestData.AutomationTask(msel.Id, scenarioEvent.Id);
        await Seed(task);
        var runs = new List<AutomationRunEntity>();
        for (var i = 0; i < 4; i++)
        {
            var run = await Run(task, msel, AutomationRunKind.Test);
            await Db.AutomationRuns.Where(r => r.Id == run.Id).ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Status, AutomationStatus.Succeeded)
                .SetProperty(r => r.DateCreated, _clock.UtcNow.AddMinutes(i)), Ct);
            runs.Add(run);
        }
        var exercise = await Run(task, msel);

        Assert.Equal(2, await Scheduler().RetainAsync(Ct));

        var left = await ReadBack(db => db.AutomationRuns.Where(r => r.AutomationTaskId == task.Id).Select(r => r.Id).ToListAsync(Ct));
        Assert.Equal(new[] { runs[2].Id, runs[3].Id, exercise.Id }.OrderBy(x => x), left.OrderBy(x => x));
    }

    /// <summary>A test of a task that was not saved has no task, so its event's newest are kept.</summary>
    [Fact]
    public async Task Retain_KeepsEachEventsNewestTestsOfDrafts()
    {
        var (msel, scenarioEvent) = await Deployed();
        var draft = TestData.AutomationTask(msel.Id, scenarioEvent.Id);
        for (var i = 0; i < 3; i++)
        {
            var run = AutomationRuns.New(draft, msel.Id, AutomationRunKind.Test, AutomationRunSource.Manual, _clock.UtcNow, null, null, null);
            run.AutomationTaskId = null;
            run.Status = AutomationStatus.Succeeded;
            run.DateCreated = _clock.UtcNow.AddMinutes(i);
            await Seed(run);
        }

        Assert.Equal(1, await Scheduler().RetainAsync(Ct));
        Assert.Equal(2, await ReadBack(db => db.AutomationRuns.CountAsync(r => r.ScenarioEventId == scenarioEvent.Id, Ct)));
    }

    // ---------------------------------------------------------------------------------------------
    // running
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task ARun_ThatMatchesItsExpectedOutput_Succeeds()
    {
        var (msel, scenarioEvent) = await Deployed();
        var task = TestData.AutomationTask(msel.Id, scenarioEvent.Id, AutomationAction.http_get);
        task.ActionParameters = new() { ["Url"] = "http://cite.example/api" };
        task.ExpectedOutput = "OK";
        await Seed(task);
        var run = await Run(task, msel);
        Answers("all ok");

        await Drain();

        var stored = await Stored(run.Id);
        Assert.Equal(AutomationStatus.Succeeded, stored.Status);
        Assert.NotNull(stored.EndedAt);
        Assert.Null(stored.LeaseOwner);
        var result = Assert.Single(stored.Results);
        Assert.Equal(AutomationStatus.Succeeded, result.Status);
        Assert.Equal("all ok", result.ActualOutput);
        Assert.Null(result.VmId);
    }

    [Fact]
    public async Task ARun_WhoseOutputLacksTheExpectedOutput_Fails()
    {
        var (msel, scenarioEvent) = await Deployed();
        var task = TestData.AutomationTask(msel.Id, scenarioEvent.Id);
        task.ExpectedOutput = "beacon started";
        await Seed(task);
        var run = await Run(task, msel);
        Answers("nothing happened");

        await Drain();

        Assert.Equal(AutomationStatus.Failed, (await Stored(run.Id)).Status);
    }

    /// <summary>An action's own verdict wins over the expected output.</summary>
    [Fact]
    public async Task ARun_TheActionSaysFailed_FailsWhateverItsOutput()
    {
        var (msel, scenarioEvent) = await Deployed();
        var task = TestData.AutomationTask(msel.Id, scenarioEvent.Id);
        await Seed(task);
        var run = await Run(task, msel);
        Answers("404 Not Found", succeeded: false);

        await Drain();

        Assert.Equal(AutomationStatus.Failed, (await Stored(run.Id)).Status);
    }

    [Fact]
    public async Task ARun_HandsTheActionItsBoundParameters_AndRecordsThemWithSecretsRedacted()
    {
        var (msel, scenarioEvent) = await Deployed();
        var protector = new AutomationSecretProtector(AutomationTestOptions.Automation(o => o.SecretKeys = new() { ["k"] = AutomationTestOptions.NewKey() }));
        var task = TestData.AutomationTask(msel.Id, scenarioEvent.Id, AutomationAction.http_post);
        task.ActionParameters = new() { ["Url"] = "http://cite.example/api/msels/{mselId}" };
        task.SecretParameters = protector.Protect(new Dictionary<string, string> { ["Headers"] = "{\"X-Key\":\"s3cret\"}" });
        await Seed(task);
        var run = await Run(task, msel);
        AutomationActionContext seen = null;
        _runner.ExecuteAsync(Arg.Do<AutomationActionContext>(c => seen = c), Arg.Any<CancellationToken>()).Returns(new ActionOutcome("ok"));

        var claimed = await Scheduler().ClaimAsync(10, Ct);
        await Execute(Assert.Single(claimed), protector);

        Assert.Equal($"http://cite.example/api/msels/{msel.Id}", seen.Get("Url"));
        Assert.Equal("{\"X-Key\":\"s3cret\"}", seen.Get("Headers"));
        var input = JsonSerializer.Deserialize<Dictionary<string, string>>(Assert.Single((await Stored(run.Id)).Results).InputString);
        Assert.Equal(AutomationParameterBinder.Redacted, input["Headers"]);
        Assert.DoesNotContain("s3cret", Assert.Single((await Stored(run.Id)).Results).InputString);
    }

    [Fact]
    public async Task ARun_ThatOutlastsItsExpiration_Expires_AndTriggersTheExpirationTasks()
    {
        var (msel, scenarioEvent) = await Deployed();
        var task = TestData.AutomationTask(msel.Id, scenarioEvent.Id);
        task.ExpirationSeconds = 1;
        await Seed(task);
        var onExpiration = TestData.AutomationTask(msel.Id, scenarioEvent.Id, trigger: AutomationTrigger.OnExpiration, triggerTaskId: task.Id);
        var onCompletion = TestData.AutomationTask(msel.Id, scenarioEvent.Id, trigger: AutomationTrigger.OnCompletion, triggerTaskId: task.Id);
        await Seed(onExpiration, onCompletion);
        var run = await Run(task, msel);
        _runner.ExecuteAsync(Arg.Any<AutomationActionContext>(), Arg.Any<CancellationToken>())
            .Returns(async call => { await Task.Delay(Timeout.Infinite, call.Arg<CancellationToken>()); return new ActionOutcome("never"); });

        var claimed = await Scheduler().ClaimAsync(10, Ct);
        await Execute(Assert.Single(claimed));

        var stored = await Stored(run.Id);
        Assert.Equal(AutomationStatus.Expired, stored.Status);
        Assert.Equal("No answer within 1 s.", Assert.Single(stored.Results).ActualOutput);
        var chained = await ReadBack(db => db.AutomationRuns.Where(r => r.ParentRunId == run.Id).Select(r => r.AutomationTaskId).ToListAsync(Ct));
        Assert.Equal([onExpiration.Id], chained);
    }

    [Fact]
    public async Task ARun_CancelledWhileItRuns_IsCancelled_AndTriggersNothing()
    {
        var (msel, scenarioEvent) = await Deployed();
        var task = TestData.AutomationTask(msel.Id, scenarioEvent.Id);
        await Seed(task);
        await Seed(TestData.AutomationTask(msel.Id, scenarioEvent.Id, trigger: AutomationTrigger.OnCompletion, triggerTaskId: task.Id));
        var run = await Run(task, msel);
        var started = new TaskCompletionSource();
        _runner.ExecuteAsync(Arg.Any<AutomationActionContext>(), Arg.Any<CancellationToken>())
            .Returns(async call => { started.SetResult(); await Task.Delay(Timeout.Infinite, call.Arg<CancellationToken>()); return new ActionOutcome("never"); });

        var claimed = await Scheduler().ClaimAsync(10, Ct);
        Db.ChangeTracker.Clear();
        var executing = Executor().ExecuteIterationAsync(Assert.Single(claimed), Ct);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        await using (var other = NewContext())
            Assert.True(await new AutomationRunControl(other, _registry, _clock).CancelAsync(run.Id, Ct));
        await executing;

        var stored = await Stored(run.Id);
        Assert.Equal(AutomationStatus.Cancelled, stored.Status);
        Assert.Equal(AutomationStatus.Cancelled, Assert.Single(stored.Results).Status);
        Assert.Equal(1, await ReadBack(db => db.AutomationRuns.CountAsync(r => r.MselId == msel.Id, Ct)));
    }

    [Fact]
    public async Task ARun_CancelledBeforeItStarts_EndsAtTheNextClaim()
    {
        var (msel, scenarioEvent) = await Deployed();
        var task = TestData.AutomationTask(msel.Id, scenarioEvent.Id);
        await Seed(task);
        var run = await Run(task, msel);

        await using (var other = NewContext())
            Assert.True(await new AutomationRunControl(other, _registry, _clock).CancelAsync(run.Id, Ct));

        Assert.Equal(AutomationStatus.Cancelled, (await Stored(run.Id)).Status);
        Assert.Empty(await Scheduler().ClaimAsync(10, Ct));
        await using (var again = NewContext())
            Assert.False(await new AutomationRunControl(again, _registry, _clock).CancelAsync(run.Id, Ct));
    }

    [Theory]
    [InlineData(AutomationIterationTermination.IterationCount, 3)]
    [InlineData(AutomationIterationTermination.UntilSuccess, 2)]
    [InlineData(AutomationIterationTermination.UntilFailure, 1)]
    public async Task ARun_Iterates_UntilItsTerminationSaysStop(AutomationIterationTermination termination, int iterations)
    {
        var (msel, scenarioEvent) = await Deployed();
        var task = TestData.AutomationTask(msel.Id, scenarioEvent.Id);
        task.Iterations = 3;
        task.IterationTermination = termination;
        task.IntervalSeconds = 30;
        task.ExpectedOutput = "up";
        await Seed(task);
        var run = await Run(task, msel);
        _runner.ExecuteAsync(Arg.Any<AutomationActionContext>(), Arg.Any<CancellationToken>())
            .Returns(new ActionOutcome("down"), new ActionOutcome("up"), new ActionOutcome("up"));

        for (var i = 0; i < 4; i++)
        {
            await Drain();
            _clock.Advance(TimeSpan.FromSeconds(30));
        }

        var stored = await Stored(run.Id);
        Assert.Equal(iterations, stored.CurrentIteration);
        Assert.Equal(Enumerable.Range(1, iterations), stored.Results.Select(r => r.Iteration).OrderBy(x => x));
        Assert.True(AutomationRuns.IsFinal(stored.Status));
    }

    /// <summary>The next iteration waits for the interval, without a worker waiting through it.</summary>
    [Fact]
    public async Task BetweenIterations_TheRunIsPending_UntilItsIntervalPasses()
    {
        var (msel, scenarioEvent) = await Deployed();
        var task = TestData.AutomationTask(msel.Id, scenarioEvent.Id);
        task.Iterations = 2;
        task.IntervalSeconds = 30;
        await Seed(task);
        var run = await Run(task, msel);
        Answers("ok");

        await Drain();

        var stored = await Stored(run.Id);
        Assert.Equal(AutomationStatus.Pending, stored.Status);
        Assert.Equal(_clock.UtcNow.AddSeconds(30), stored.DueAt);
        Assert.Null(stored.LeaseOwner);
    }

    [Theory]
    [InlineData(true, new[] { AutomationTrigger.OnSuccess, AutomationTrigger.OnCompletion })]
    [InlineData(false, new[] { AutomationTrigger.OnFailure, AutomationTrigger.OnCompletion })]
    public async Task AFinishedRun_StartsTheTasksItsOutcomeTriggers(bool succeeds, AutomationTrigger[] started)
    {
        var (msel, scenarioEvent) = await Deployed();
        var task = TestData.AutomationTask(msel.Id, scenarioEvent.Id);
        await Seed(task);
        var children = Enum.GetValues<AutomationTrigger>()
            .Where(t => t is not (AutomationTrigger.Manual or AutomationTrigger.AtEventTime))
            .Select(t => TestData.AutomationTask(msel.Id, scenarioEvent.Id, trigger: t, triggerTaskId: task.Id))
            .ToList();
        children[0].DelaySeconds = 20;
        await Seed(children.Cast<object>().ToArray());
        var run = await Run(task, msel);
        Answers("done", succeeds);

        await Drain(passes: 1);

        var chained = await ReadBack(db => db.AutomationRuns.Where(r => r.ParentRunId == run.Id).ToListAsync(Ct));
        Assert.Equal(started.OrderBy(t => t), chained.Select(r => children.Single(c => c.Id == r.AutomationTaskId).TriggerCondition).OrderBy(t => t));
        Assert.All(chained, r =>
        {
            Assert.Equal(AutomationRunSource.Chain, r.Source);
            Assert.Equal(msel.DeploymentId, r.DeploymentId);
        });
    }

    [Fact]
    public async Task AChainedTask_WaitsForItsDelay()
    {
        var (msel, scenarioEvent) = await Deployed();
        var task = TestData.AutomationTask(msel.Id, scenarioEvent.Id);
        await Seed(task);
        var child = TestData.AutomationTask(msel.Id, scenarioEvent.Id, trigger: AutomationTrigger.OnSuccess, triggerTaskId: task.Id);
        child.DelaySeconds = 20;
        await Seed(child);
        var run = await Run(task, msel);
        Answers("ok");

        await Drain(passes: 1);

        Assert.Equal(_clock.UtcNow.AddSeconds(20), (await ReadBack(db => db.AutomationRuns.SingleAsync(r => r.ParentRunId == run.Id, Ct))).DueAt);
    }

    [Fact]
    public async Task ADisabledTask_IsNotTriggered()
    {
        var (msel, scenarioEvent) = await Deployed();
        var task = TestData.AutomationTask(msel.Id, scenarioEvent.Id);
        await Seed(task);
        var child = TestData.AutomationTask(msel.Id, scenarioEvent.Id, trigger: AutomationTrigger.OnSuccess, triggerTaskId: task.Id);
        child.Enabled = false;
        await Seed(child);
        await Run(task, msel);
        Answers("ok");

        await Drain();

        Assert.Equal(1, await ReadBack(db => db.AutomationRuns.CountAsync(r => r.MselId == msel.Id, Ct)));
    }

    /// <summary>A chain stops when its deployment does: a task finishing after the integrations were removed triggers nothing.</summary>
    [Fact]
    public async Task AnExerciseRunFinishingAfterItsDeploymentEnded_TriggersNothing()
    {
        var (msel, scenarioEvent) = await Deployed();
        var task = TestData.AutomationTask(msel.Id, scenarioEvent.Id);
        await Seed(task);
        await Seed(TestData.AutomationTask(msel.Id, scenarioEvent.Id, trigger: AutomationTrigger.OnCompletion, triggerTaskId: task.Id));
        await Run(task, msel);
        await Db.Msels.Where(m => m.Id == msel.Id).ExecuteUpdateAsync(s => s.SetProperty(m => m.DeploymentId, Guid.NewGuid()), Ct);
        Answers("ok");

        await Drain();

        Assert.Equal(1, await ReadBack(db => db.AutomationRuns.CountAsync(r => r.MselId == msel.Id, Ct)));
    }

    /// <summary>A test chains only when asked to, and its chained runs are tests too.</summary>
    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 1)]
    public async Task ATestRun_ChainsOnlyWhenAsked(bool triggerChain, int chained)
    {
        var (msel, scenarioEvent) = await Deployed();
        var task = TestData.AutomationTask(msel.Id, scenarioEvent.Id);
        await Seed(task);
        await Seed(TestData.AutomationTask(msel.Id, scenarioEvent.Id, trigger: AutomationTrigger.OnSuccess, triggerTaskId: task.Id));
        var run = await Run(task, msel, AutomationRunKind.Test, triggerChain: triggerChain);
        Answers("ok");

        await Drain(passes: 1);

        var children = await ReadBack(db => db.AutomationRuns.Where(r => r.ParentRunId == run.Id).ToListAsync(Ct));
        Assert.Equal(chained, children.Count);
        Assert.All(children, c => Assert.Equal(AutomationRunKind.Test, c.Kind));
    }

    /// <summary>A group start runs the manual tasks it was given one after another, whatever each one's outcome.</summary>
    [Fact]
    public async Task AGroupStartsManualTasks_RunOneAfterAnother()
    {
        var (msel, scenarioEvent) = await Deployed();
        var tasks = Enumerable.Range(0, 3).Select(i => TestData.AutomationTask(msel.Id, scenarioEvent.Id, displayOrder: i)).ToList();
        await Seed(tasks.Cast<object>().ToArray());
        var first = await Run(tasks[0], msel, source: AutomationRunSource.GroupStart, followOn: [tasks[1].Id, tasks[2].Id]);
        _runner.ExecuteAsync(Arg.Any<AutomationActionContext>(), Arg.Any<CancellationToken>())
            .Returns(new ActionOutcome("no", false), new ActionOutcome("yes", true), new ActionOutcome("yes", true));

        var order = new List<Guid?>();
        for (var i = 0; i < 3; i++)
        {
            var claimed = await Scheduler().ClaimAsync(10, Ct);
            var id = Assert.Single(claimed);
            order.Add((await Stored(id)).AutomationTaskId);
            await Execute(id);
        }

        Assert.Equal(tasks.Select(t => (Guid?)t.Id), order);
        Assert.Empty(await Scheduler().ClaimAsync(10, Ct));
        Assert.Equal(AutomationStatus.Failed, (await Stored(first.Id)).Status);
    }

    /// <summary>
    /// A task a run triggers is run as a run by hand is: one that runs only once and has succeeded in the deployment, or
    /// one that is already running, is recorded as skipped instead, so the facilitator sees why.
    /// </summary>
    [Theory]
    [InlineData(AutomationStatus.Succeeded, "has succeeded and runs only once")]
    [InlineData(AutomationStatus.Running, "is already running")]
    public async Task ATriggeredTask_ThatMayNotRunAgain_IsRecordedSkipped(AutomationStatus childsRun, string why)
    {
        var (msel, scenarioEvent) = await Deployed();
        var parent = TestData.AutomationTask(msel.Id, scenarioEvent.Id);
        parent.Repeatable = true;
        var child = TestData.AutomationTask(msel.Id, scenarioEvent.Id, displayOrder: 1, trigger: AutomationTrigger.OnSuccess, triggerTaskId: parent.Id);
        child.Name = "child";
        await Seed(parent, child);
        var earlier = await Run(child, msel);
        await Db.AutomationRuns.Where(r => r.Id == earlier.Id).ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, childsRun), Ct);
        var run = await Run(parent, msel);
        Answers("ok", true);

        await Execute(Assert.Single(await Scheduler().ClaimAsync(10, Ct)));

        var skipped = Assert.Single(await ReadBack(db => db.AutomationRuns.Where(r => r.ParentRunId == run.Id).ToListAsync(Ct)));
        Assert.Equal(AutomationStatus.Skipped, skipped.Status);
        Assert.Equal($"child {why}, so it was not run again.", skipped.StatusDetail);
        Assert.Empty(await Scheduler().ClaimAsync(10, Ct));
    }

    /// <summary>A group start leaves out a task that may not run again, recording it skipped, and goes on to the next one.</summary>
    [Fact]
    public async Task AGroupStart_SkipsATaskThatRanOnce_AndRunsTheNext()
    {
        var (msel, scenarioEvent) = await Deployed();
        var tasks = Enumerable.Range(0, 3).Select(i => TestData.AutomationTask(msel.Id, scenarioEvent.Id, displayOrder: i)).ToList();
        await Seed(tasks.Cast<object>().ToArray());
        var once = await Run(tasks[1], msel);
        await Db.AutomationRuns.Where(r => r.Id == once.Id).ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, AutomationStatus.Succeeded), Ct);
        var first = await Run(tasks[0], msel, source: AutomationRunSource.GroupStart, followOn: [tasks[1].Id, tasks[2].Id]);
        Answers("ok", true);

        await Execute(Assert.Single(await Scheduler().ClaimAsync(10, Ct)));

        var next = await ReadBack(db => db.AutomationRuns.Where(r => r.ParentRunId == first.Id).ToListAsync(Ct));
        Assert.Equal(AutomationStatus.Skipped, Assert.Single(next, r => r.AutomationTaskId == tasks[1].Id).Status);
        Assert.Equal(AutomationStatus.Pending, Assert.Single(next, r => r.AutomationTaskId == tasks[2].Id).Status);
    }

    /// <summary>A pause holds a scheduled run that is due, as it holds the scheduling; a run by hand goes ahead.</summary>
    [Fact]
    public async Task APausedMsel_HoldsItsScheduledRuns_ButNotARunByHand()
    {
        var (msel, scenarioEvent) = await Deployed();
        var task = TestData.AutomationTask(msel.Id, scenarioEvent.Id);
        await Seed(task);
        var scheduled = await Run(task, msel, source: AutomationRunSource.Schedule);
        var byHand = await Run(task, msel);
        await Db.Msels.Where(m => m.Id == msel.Id).ExecuteUpdateAsync(s => s.SetProperty(m => m.AutomationPaused, true), Ct);

        Assert.Equal([byHand.Id], await Scheduler().ClaimAsync(10, Ct));

        await Db.Msels.Where(m => m.Id == msel.Id).ExecuteUpdateAsync(s => s.SetProperty(m => m.AutomationPaused, false), Ct);
        Assert.Equal([scheduled.Id], await Scheduler().ClaimAsync(10, Ct));
    }

    /// <summary>
    /// A group start's run that was interrupted by a restart is an error, which triggers nothing, but the group goes on
    /// to its next task, as it does after any outcome.
    /// </summary>
    [Fact]
    public async Task Recover_OfAGroupStartsRun_GoesOnToTheNextTask()
    {
        var (msel, scenarioEvent) = await Deployed();
        var tasks = Enumerable.Range(0, 2).Select(i => TestData.AutomationTask(msel.Id, scenarioEvent.Id, displayOrder: i)).ToList();
        await Seed(tasks.Cast<object>().ToArray());
        var first = await Run(tasks[0], msel, source: AutomationRunSource.GroupStart, followOn: [tasks[1].Id]);
        await Db.AutomationRuns.Where(r => r.Id == first.Id).ExecuteUpdateAsync(s => s
            .SetProperty(r => r.Status, AutomationStatus.Running)
            .SetProperty(r => r.LeaseOwner, "gone")
            .SetProperty(r => r.LeaseUntil, _clock.UtcNow.AddSeconds(-1)), Ct);

        await Scheduler().RecoverAsync(Ct);

        Assert.Equal(AutomationStatus.Error, (await Stored(first.Id)).Status);
        var next = Assert.Single(await ReadBack(db => db.AutomationRuns.Where(r => r.ParentRunId == first.Id).ToListAsync(Ct)));
        Assert.Equal((tasks[1].Id, AutomationStatus.Pending), (next.AutomationTaskId.Value, next.Status));
    }

    /// <summary>A run another instance holds is not started here, though it is still queued.</summary>
    [Fact]
    public async Task ARunAnotherInstanceHolds_IsNotStartedHere()
    {
        var (msel, scenarioEvent) = await Deployed();
        var task = TestData.AutomationTask(msel.Id, scenarioEvent.Id);
        await Seed(task);
        var run = await Run(task, msel);
        var claimed = await Scheduler(registry: new AutomationRunRegistry()).ClaimAsync(10, Ct);

        await Execute(Assert.Single(claimed));

        Assert.Equal(AutomationStatus.Queued, (await Stored(run.Id)).Status);
        await _runner.DidNotReceive().ExecuteAsync(Arg.Any<AutomationActionContext>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// A run taken back while this instance worked it, here by a recovery that recorded it interrupted, keeps what the
    /// recovery wrote: the worker's outcome is dropped, and it triggers nothing.
    /// </summary>
    [Fact]
    public async Task ARunTakenBackWhileItRan_KeepsWhatTheRecoveryWrote()
    {
        var (msel, scenarioEvent) = await Deployed();
        var task = TestData.AutomationTask(msel.Id, scenarioEvent.Id);
        await Seed(task);
        await Seed(TestData.AutomationTask(msel.Id, scenarioEvent.Id, trigger: AutomationTrigger.OnSuccess, triggerTaskId: task.Id));
        var run = await Run(task, msel);
        _runner.ExecuteAsync(Arg.Any<AutomationActionContext>(), Arg.Any<CancellationToken>()).Returns(async _ =>
        {
            // as RecoverAsync records it: the run and its unfinished results interrupted
            await using var other = NewContext();
            await other.AutomationRuns.Where(r => r.Id == run.Id).ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Status, AutomationStatus.Error)
                .SetProperty(r => r.LeaseOwner, (string)null), Ct);
            await other.AutomationResults.Where(r => r.RunId == run.Id).ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Status, AutomationStatus.Error)
                .SetProperty(r => r.ActualOutput, "interrupted"), Ct);
            return new ActionOutcome("ok", true);
        });

        await Execute(Assert.Single(await Scheduler().ClaimAsync(10, Ct)));

        var stored = await Stored(run.Id);
        Assert.Equal(AutomationStatus.Error, stored.Status);
        Assert.Equal((AutomationStatus.Error, "interrupted"), (Assert.Single(stored.Results).Status, stored.Results.Single().ActualOutput));
        Assert.Equal(1, await ReadBack(db => db.AutomationRuns.CountAsync(r => r.MselId == msel.Id, Ct)));
    }

    /// <summary>
    /// A run taken back while its VMs were listed starts no action: this instance learns it no longer has the run when
    /// it writes the results, before acting on any VM.
    /// </summary>
    [Fact]
    public async Task ARunTakenBackWhileItsVmsAreListed_ActsOnNoVm()
    {
        var (msel, scenarioEvent) = await Deployed();
        var task = TestData.AutomationTask(msel.Id, scenarioEvent.Id, AutomationAction.vm_hw_power_off);
        task.VmMask = "web";
        await Seed(task);
        var run = await Run(task, msel);
        _vms.ResolveAsync(msel.PlayerViewId.Value, "web", Arg.Any<CancellationToken>()).Returns(async _ =>
        {
            await TakeBack(run.Id);
            return (IReadOnlyList<AutomationVm>)[new AutomationVm(Guid.NewGuid(), "web-01")];
        });

        await Execute(Assert.Single(await Scheduler().ClaimAsync(10, Ct)));

        await _runner.DidNotReceive().ExecuteAsync(Arg.Any<AutomationActionContext>(), Arg.Any<CancellationToken>());
        var stored = await Stored(run.Id);
        Assert.Equal(AutomationStatus.Error, stored.Status);
        Assert.Empty(stored.Results);
    }

    /// <summary>A run taken back while it acted on one VM acts on no further VM.</summary>
    [Fact]
    public async Task ARunTakenBackWhileItActsOnAVm_ActsOnNoOtherVm()
    {
        var (msel, scenarioEvent) = await Deployed();
        var task = TestData.AutomationTask(msel.Id, scenarioEvent.Id, AutomationAction.vm_hw_power_off);
        task.VmMask = "web";
        await Seed(task);
        var run = await Run(task, msel);
        _vms.ResolveAsync(msel.PlayerViewId.Value, "web", Arg.Any<CancellationToken>())
            .Returns([new AutomationVm(Guid.NewGuid(), "web-01"), new AutomationVm(Guid.NewGuid(), "web-02")]);
        var acted = 0;
        _runner.ExecuteAsync(Arg.Any<AutomationActionContext>(), Arg.Any<CancellationToken>()).Returns(async _ =>
        {
            if (Interlocked.Increment(ref acted) == 1)
                await TakeBack(run.Id);
            return new ActionOutcome("off", true);
        });

        Options.MaxConcurrentVmOperationsPerRun = 1;
        await Execute(Assert.Single(await Scheduler().ClaimAsync(10, Ct)));

        Assert.Equal(1, acted);
        Assert.Equal(AutomationStatus.Error, (await Stored(run.Id)).Status);
    }

    /// <summary>As RecoverAsync records a run whose instance stopped: interrupted, and no instance's.</summary>
    private async Task TakeBack(Guid runId)
    {
        await using var other = NewContext();
        await other.AutomationRuns.Where(r => r.Id == runId).ExecuteUpdateAsync(s => s
            .SetProperty(r => r.Status, AutomationStatus.Error)
            .SetProperty(r => r.LeaseOwner, (string)null), Ct);
    }

    /// <summary>
    /// A task due at its event time is scheduled as a run by hand is admitted: one that is already going at that time, or
    /// that runs only once and has succeeded, is recorded as skipped, once, rather than run again.
    /// </summary>
    [Theory]
    [InlineData(AutomationStatus.Running, "is already running")]
    [InlineData(AutomationStatus.Succeeded, "has succeeded and runs only once")]
    public async Task Schedule_OfATaskThatMayNotRunNow_RecordsItSkipped(AutomationStatus earlier, string why)
    {
        // its time came a minute ago, within the grace period
        var (msel, scenarioEvent) = await Deployed(deltaSeconds: 9 * 60);
        var task = TestData.AutomationTask(msel.Id, scenarioEvent.Id, trigger: AutomationTrigger.AtEventTime);
        task.Name = "at its time";
        await Seed(task);
        var byHand = await Run(task, msel);
        await Db.AutomationRuns.Where(r => r.Id == byHand.Id).ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, earlier), Ct);

        Assert.Equal(1, await Scheduler().ScheduleAsync(Ct));

        var scheduled = await ReadBack(db => db.AutomationRuns.SingleAsync(r => r.AutomationTaskId == task.Id && r.Source == AutomationRunSource.Schedule, Ct));
        Assert.Equal(AutomationStatus.Skipped, scheduled.Status);
        Assert.Equal($"at its time {why} at its event time, so it was not run again.", scheduled.StatusDetail);
        Assert.Equal(0, await Scheduler().ScheduleAsync(Ct));
    }

    /// <summary>A run whose deployment ended before it started does not act: it is cancelled, and triggers nothing.</summary>
    [Fact]
    public async Task ARunOfADeploymentThatEnded_IsCancelled_WithoutActing()
    {
        var (msel, scenarioEvent) = await Deployed();
        var task = TestData.AutomationTask(msel.Id, scenarioEvent.Id);
        await Seed(task);
        var run = await Run(task, msel);
        var claimed = Assert.Single(await Scheduler().ClaimAsync(10, Ct));
        await Db.Msels.Where(m => m.Id == msel.Id).ExecuteUpdateAsync(s => s.SetProperty(m => m.Status, MselItemStatus.Pulling), Ct);

        await Execute(claimed);

        var stored = await Stored(run.Id);
        Assert.Equal(AutomationStatus.Cancelled, stored.Status);
        Assert.Equal("The exercise's deployment ended before it started.", stored.StatusDetail);
        await _runner.DidNotReceive().ExecuteAsync(Arg.Any<AutomationActionContext>(), Arg.Any<CancellationToken>());
    }

    /// <summary>A run a restart interrupted is recorded in the exercise's xAPI as one the executor ends would be.</summary>
    [Fact]
    public async Task Recover_RecordsTheInterruptedRunInXApi()
    {
        var (msel, scenarioEvent) = await Deployed();
        var task = TestData.AutomationTask(msel.Id, scenarioEvent.Id);
        await Seed(task);
        var run = await Run(task, msel);
        await Db.AutomationRuns.Where(r => r.Id == run.Id).ExecuteUpdateAsync(s => s
            .SetProperty(r => r.Status, AutomationStatus.Running)
            .SetProperty(r => r.LeaseOwner, "gone")
            .SetProperty(r => r.LeaseUntil, _clock.UtcNow.AddSeconds(-1)), Ct);
        var xApi = Substitute.For<Blueprint.Api.Services.IXApiService>();
        var services = new ServiceCollection();
        services.AddSingleton(xApi);
        var scopes = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        var scheduler = new AutomationScheduler(Db, _registry, AutomationTestOptions.Monitor(Options), _clock,
            NullLogger<AutomationScheduler>.Instance, scopes);

        await scheduler.RecoverAsync(Ct);

        await xApi.Received(1).AutomationRunEndedAsync(run.Id, Arg.Any<CancellationToken>());
    }

    /// <summary>A cancel of a run a worker has claimed only asks it to stop: the worker ends it.</summary>
    [Fact]
    public async Task ACancel_OfAClaimedRun_OnlyAsksItToStop()
    {
        var (msel, scenarioEvent) = await Deployed();
        var task = TestData.AutomationTask(msel.Id, scenarioEvent.Id);
        await Seed(task);
        var run = await Run(task, msel);
        var claimed = Assert.Single(await Scheduler().ClaimAsync(10, Ct));

        await using (var other = NewContext())
            Assert.True(await new AutomationRunControl(other, _registry, _clock).CancelAsync(run.Id, Ct));
        var asked = await Stored(run.Id);
        Assert.Equal((AutomationStatus.Queued, true), (asked.Status, asked.CancelRequested));

        await Execute(claimed);
        Assert.Equal(AutomationStatus.Cancelled, (await Stored(run.Id)).Status);
    }

    // ---------------------------------------------------------------------------------------------
    // VMs
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task AVmTask_RunsOncePerMatchedVm()
    {
        var (msel, scenarioEvent) = await Deployed();
        var task = TestData.AutomationTask(msel.Id, scenarioEvent.Id, AutomationAction.vm_hw_power_on);
        task.VmMask = "web";
        await Seed(task);
        var run = await Run(task, msel);
        var web1 = new AutomationVm(Guid.NewGuid(), "web-01");
        var web2 = new AutomationVm(Guid.NewGuid(), "web-02");
        _vms.ResolveAsync(msel.PlayerViewId.Value, "web", Arg.Any<CancellationToken>()).Returns([web1, web2]);
        _runner.ExecuteAsync(Arg.Any<AutomationActionContext>(), Arg.Any<CancellationToken>())
            .Returns(call => new ActionOutcome("on " + call.Arg<AutomationActionContext>().VmName));

        await Drain();

        var results = (await Stored(run.Id)).Results.OrderBy(r => r.VmName).ToList();
        Assert.Equal(new[] { (Guid?)web1.Id, web2.Id }, results.Select(r => r.VmId));
        Assert.Equal(["on web-01", "on web-02"], results.Select(r => r.ActualOutput));
    }

    /// <summary>One VM's failure fails the run.</summary>
    [Fact]
    public async Task AVmTask_ThatFailsOnOneVm_Fails()
    {
        var (msel, scenarioEvent) = await Deployed();
        var task = TestData.AutomationTask(msel.Id, scenarioEvent.Id, AutomationAction.guest_process_run);
        task.VmMask = "web";
        await Seed(task);
        var run = await Run(task, msel);
        _vms.ResolveAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns([new AutomationVm(Guid.NewGuid(), "web-01"), new AutomationVm(Guid.NewGuid(), "web-02")]);
        _runner.ExecuteAsync(Arg.Is<AutomationActionContext>(c => c.VmName == "web-01"), Arg.Any<CancellationToken>()).Returns(new ActionOutcome("ok"));
        _runner.ExecuteAsync(Arg.Is<AutomationActionContext>(c => c.VmName == "web-02"), Arg.Any<CancellationToken>())
            .Throws(new AutomationActionFailedException("exit code 1"));

        await Drain();

        Assert.Equal(AutomationStatus.Failed, (await Stored(run.Id)).Status);
    }

    [Theory]
    [InlineData("nomatch")]
    [InlineData("")]
    [InlineData("toomany")]
    public async Task AVmTask_ThatMatchesNoUsableVms_IsAnError_AndRunsNoAction(string mask)
    {
        var (msel, scenarioEvent) = await Deployed();
        Options.MaxVmsPerTask = 1;
        var task = TestData.AutomationTask(msel.Id, scenarioEvent.Id, AutomationAction.vm_hw_power_off);
        task.VmMask = mask;
        await Seed(task);
        var run = await Run(task, msel);
        _vms.ResolveAsync(Arg.Any<Guid>(), "nomatch", Arg.Any<CancellationToken>()).Returns([]);
        _vms.ResolveAsync(Arg.Any<Guid>(), "toomany", Arg.Any<CancellationToken>())
            .Returns([new AutomationVm(Guid.NewGuid(), "a"), new AutomationVm(Guid.NewGuid(), "b")]);

        await Drain();

        var stored = await Stored(run.Id);
        Assert.Equal(AutomationStatus.Error, stored.Status);
        Assert.Equal(AutomationStatus.Error, Assert.Single(stored.Results).Status);
        await _runner.DidNotReceive().ExecuteAsync(Arg.Any<AutomationActionContext>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AVmTask_WithNoPlayerView_IsAnError()
    {
        var (msel, scenarioEvent) = await Deployed();
        var task = TestData.AutomationTask(msel.Id, scenarioEvent.Id, AutomationAction.vm_hw_power_off);
        task.VmMask = "web";
        await Seed(task);
        var run = AutomationRuns.New(task, msel.Id, AutomationRunKind.Exercise, AutomationRunSource.Manual, _clock.UtcNow, msel.DeploymentId, null, null);
        await Seed(run);

        await Drain();

        Assert.Equal("The MSEL has no Player view; push it to Player first.", Assert.Single((await Stored(run.Id)).Results).ActualOutput);
    }

    /// <summary>A test of an action that only reports what it would do is a dry run.</summary>
    [Theory]
    [InlineData(AutomationAction.cite_move_set, true)]
    [InlineData(AutomationAction.player_notification, false)]
    public async Task ATestRun_IsADryRun_ForTheActionsThatSaySo(AutomationAction action, bool dryRun)
    {
        var (msel, scenarioEvent) = await Deployed();
        var task = TestData.AutomationTask(msel.Id, scenarioEvent.Id, action);
        await Seed(task);
        await Run(task, msel, AutomationRunKind.Test);
        AutomationActionContext seen = null;
        _runner.ExecuteAsync(Arg.Do<AutomationActionContext>(c => seen = c), Arg.Any<CancellationToken>()).Returns(new ActionOutcome("ok"));

        await Drain();

        Assert.True(seen.IsTest);
        Assert.Equal(dryRun, seen.DryRun);
    }

    /// <summary>The run uses the task as it was when the run was made.</summary>
    [Fact]
    public async Task ARun_UsesTheTaskAsItWasWhenTheRunWasMade()
    {
        var (msel, scenarioEvent) = await Deployed();
        var task = TestData.AutomationTask(msel.Id, scenarioEvent.Id);
        task.ActionParameters = new() { ["Text"] = "before" };
        await Seed(task);
        await Run(task, msel);
        await Db.AutomationTasks.Where(t => t.Id == task.Id).ExecuteDeleteAsync(Ct);
        AutomationActionContext seen = null;
        _runner.ExecuteAsync(Arg.Do<AutomationActionContext>(c => seen = c), Arg.Any<CancellationToken>()).Returns(new ActionOutcome("ok"));

        await Drain();

        Assert.Equal("before", seen.Get("Text"));
    }

    // ---------------------------------------------------------------------------------------------
    // deployments
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task CancelAll_EndsAMselsPendingRuns_AndFlagsItsRunningOnes_LeavingTests()
    {
        var (msel, scenarioEvent) = await Deployed();
        var task = TestData.AutomationTask(msel.Id, scenarioEvent.Id);
        await Seed(task);
        var pending = await Run(task, msel);
        var running = await Run(task, msel);
        var test = await Run(task, msel, AutomationRunKind.Test);
        await Db.AutomationRuns.Where(r => r.Id == running.Id).ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, AutomationStatus.Running), Ct);

        await using (var other = NewContext())
            Assert.Equal(2, await AutomationRunControl.CancelAllAsync(other, null, msel.Id, "pulled", _clock.UtcNow, Ct));

        Assert.Equal(AutomationStatus.Cancelled, (await Stored(pending.Id)).Status);
        var flagged = await Stored(running.Id);
        Assert.Equal(AutomationStatus.Running, flagged.Status);
        Assert.True(flagged.CancelRequested);
        Assert.Equal(AutomationStatus.Pending, (await Stored(test.Id)).Status);
    }

    [Fact]
    public async Task StartingADeployment_PutsTheMselAtItsFirstMove_AndARetryKeepsIt()
    {
        var msel = TestData.Msel(status: MselItemStatus.Deployed);
        await Seed(msel);
        await Seed(TestData.Move(msel.Id, moveNumber: 2, deltaSeconds: 60), TestData.Move(msel.Id, moveNumber: 1, deltaSeconds: 0));

        var entity = await Db.Msels.SingleAsync(m => m.Id == msel.Id, Ct);
        await AutomationDeployment.StartAsync(Db, entity, _clock.UtcNow, Ct);
        var deployment = entity.DeploymentId;
        entity.CurrentMoveNumber = 2;
        await AutomationDeployment.StartAsync(Db, entity, _clock.UtcNow, Ct);

        Assert.NotNull(deployment);
        Assert.Equal(deployment, entity.DeploymentId);
        Assert.Equal(2, entity.CurrentMoveNumber);
        AutomationDeployment.End(entity);
        Assert.Null(entity.DeploymentId);
        Assert.Null(entity.CurrentMoveNumber);
    }

    [Fact]
    public async Task StartingADeployment_OfAMselWithNoMoves_PutsItAtMoveZero()
    {
        var msel = TestData.Msel(status: MselItemStatus.Deployed);
        await Seed(msel);

        await AutomationDeployment.StartAsync(Db, msel, _clock.UtcNow, Ct);

        Assert.Equal(0, msel.CurrentMoveNumber);
        Assert.Equal(0, msel.CurrentGroupNumber);
    }
}
