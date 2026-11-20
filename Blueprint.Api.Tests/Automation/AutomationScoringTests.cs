// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using Blueprint.Api.Automation;
using Blueprint.Api.Data.Enumerations;
using Blueprint.Api.Data.Models;
using Blueprint.Api.Tests.Support;
using Xunit;

namespace Blueprint.Api.Tests.Automation;

/// <summary>How a MSEL's tasks are scored, as Steamfitter scored a scenario's.</summary>
public class AutomationScoringTests
{
    private static readonly Guid MselId = Guid.NewGuid();
    private static readonly Guid EventId = Guid.NewGuid();

    private static AutomationTaskEntity Scored(int score, AutomationTrigger trigger = AutomationTrigger.Manual, AutomationTaskEntity after = null)
    {
        var task = TestData.AutomationTask(MselId, EventId, trigger: trigger, triggerTaskId: after?.Id);
        task.Score = score;
        return task;
    }

    private static Dictionary<Guid, AutomationStatus> Statuses(params (AutomationTaskEntity Task, AutomationStatus Status)[] runs)
    {
        var statuses = new Dictionary<Guid, AutomationStatus>();
        foreach (var (task, status) in runs)
            statuses[task.Id] = status;
        return statuses;
    }

    /// <summary>
    /// A task's possible points take the better of its success and failure branches, with what follows on completion
    /// either way, unless what follows on expiration is more.
    /// </summary>
    [Fact]
    public void ATaskCanEarn_ItsOwnPoints_AndTheBestWayItsRunCanGo()
    {
        var root = Scored(10);
        var onSuccess = Scored(20, AutomationTrigger.OnSuccess, root);
        var onFailure = Scored(5, AutomationTrigger.OnFailure, root);
        var onCompletion = Scored(3, AutomationTrigger.OnCompletion, root);
        var onExpiration = Scored(15, AutomationTrigger.OnExpiration, root);
        var afterSuccess = Scored(4, AutomationTrigger.OnSuccess, onSuccess);

        var score = AutomationScoring.Calculate([root, onSuccess, onFailure, onCompletion, onExpiration, afterSuccess], Statuses());

        // 10 + max(3 + max(20 + 4, 5), 15)
        Assert.Equal(37, score.Tasks[root.Id].TotalScore);
        Assert.Equal(24, score.Tasks[onSuccess.Id].TotalScore);
        Assert.Equal(37, score.Score);
        Assert.Equal(0, score.ScoreEarned);
    }

    /// <summary>
    /// A task earns its points when its newest run succeeded, and a task's earned total adds what the tasks it
    /// triggers earned; the MSEL's score adds up the tasks no task triggers.
    /// </summary>
    [Fact]
    public void ATaskEarns_ItsPoints_WhenItsNewestRunSucceeded()
    {
        var root = Scored(10);
        var onSuccess = Scored(20, AutomationTrigger.OnSuccess, root);
        var failed = Scored(7);
        var notRun = Scored(8, AutomationTrigger.AtEventTime);

        var score = AutomationScoring.Calculate(
            [root, onSuccess, failed, notRun],
            Statuses((root, AutomationStatus.Succeeded), (onSuccess, AutomationStatus.Succeeded), (failed, AutomationStatus.Failed)));

        Assert.Equal(30, score.Tasks[root.Id].TotalScoreEarned);
        Assert.Equal(0, score.Tasks[failed.Id].TotalScoreEarned);
        Assert.Equal(45, score.Score);
        Assert.Equal(30, score.ScoreEarned);
    }

    /// <summary>A disabled task offers no points of its own; the tasks it triggers still count.</summary>
    [Fact]
    public void ADisabledTask_OffersNoPointsOfItsOwn()
    {
        var root = Scored(10);
        root.Enabled = false;
        var child = Scored(5, AutomationTrigger.OnCompletion, root);

        var score = AutomationScoring.Calculate([root, child], Statuses((child, AutomationStatus.Succeeded)));

        Assert.Equal(5, score.Score);
        Assert.Equal(5, score.ScoreEarned);
        Assert.Equal(0, score.Tasks[root.Id].Score);
    }

    /// <summary>
    /// A trigger on a task outside the set, or one kept on a task run by hand, leaves the task a root; a loop, which the
    /// writer refuses, counts each task once rather than overflowing.
    /// </summary>
    [Fact]
    public void ScoringCopesWith_TriggersItDoesNotKnow_AndLoops()
    {
        var outside = Scored(100);
        var orphan = Scored(3, AutomationTrigger.OnSuccess, outside);
        var manual = Scored(2, AutomationTrigger.Manual, orphan);
        var a = Scored(1, AutomationTrigger.OnSuccess);
        var b = Scored(1, AutomationTrigger.OnSuccess, a);
        a.TriggerTaskId = b.Id;

        var score = AutomationScoring.Calculate([orphan, manual, a, b], Statuses());

        Assert.Equal(3, score.Tasks[orphan.Id].TotalScore);
        Assert.Equal(2, score.Tasks[manual.Id].TotalScore);
        Assert.Equal(5, score.Score);
        Assert.Equal(2, score.Tasks[a.Id].TotalScore);
    }

    /// <summary>Only the deployment's exercise runs count, and each task's newest.</summary>
    [Fact]
    public void LatestStatus_TakesEachTasksNewestExerciseRun_OfTheDeployment()
    {
        var task = Scored(1);
        var deployment = Guid.NewGuid();
        AutomationRunEntity Run(AutomationStatus status, int minute, AutomationRunKind kind = AutomationRunKind.Exercise, Guid? dep = null)
        {
            var run = AutomationRuns.New(task, MselId, kind, AutomationRunSource.Manual, DateTime.UtcNow, dep ?? deployment, null, null);
            run.Status = status;
            run.DateCreated = new DateTime(2026, 1, 1, 10, minute, 0, DateTimeKind.Utc);
            return run;
        }

        var latest = AutomationScoring.LatestStatus(
            [
                Run(AutomationStatus.Succeeded, 1),
                Run(AutomationStatus.Failed, 2),
                Run(AutomationStatus.Succeeded, 3, AutomationRunKind.Test),
                Run(AutomationStatus.Succeeded, 4, dep: Guid.NewGuid()),
            ],
            deployment);

        Assert.Equal(AutomationStatus.Failed, latest[task.Id]);
        // a skipped run records that the task was not run: it leaves the newest real outcome, and the points it earned
        var skipped = Run(AutomationStatus.Skipped, 5);
        Assert.Equal(AutomationStatus.Failed, AutomationScoring.LatestStatus([Run(AutomationStatus.Failed, 2), skipped], deployment)[task.Id]);
        Assert.Empty(AutomationScoring.LatestStatus([skipped], deployment));
        Assert.Empty(AutomationScoring.LatestStatus([Run(AutomationStatus.Succeeded, 1)], null));
    }
}
