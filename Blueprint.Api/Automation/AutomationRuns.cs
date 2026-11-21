// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Blueprint.Api.Data;
using Blueprint.Api.Data.Enumerations;
using Blueprint.Api.Data.Models;
using Microsoft.EntityFrameworkCore;

namespace Blueprint.Api.Automation
{
    /// <summary>A task as a run saw it when it was made: what the run executes, whatever happens to the task afterwards.</summary>
    public sealed class AutomationTaskSnapshot
    {
        public Guid TaskId { get; set; }
        public Guid ScenarioEventId { get; set; }
        public string Name { get; set; }
        public AutomationAction Action { get; set; }
        public Dictionary<string, string> ActionParameters { get; set; } = new();
        // still encrypted
        public string SecretParameters { get; set; }
        public string VmMask { get; set; }
        public string ExpectedOutput { get; set; }
        public int ExpirationSeconds { get; set; }
        public int DelaySeconds { get; set; }
        public int IntervalSeconds { get; set; }
        public int Iterations { get; set; } = 1;
        public AutomationIterationTermination IterationTermination { get; set; }
        public AutomationTrigger TriggerCondition { get; set; }
        public Guid? TriggerTaskId { get; set; }
        public bool Repeatable { get; set; }

        public static AutomationTaskSnapshot Of(AutomationTaskEntity task) => new()
        {
            TaskId = task.Id,
            ScenarioEventId = task.ScenarioEventId,
            Name = task.Name,
            Action = task.Action,
            ActionParameters = new Dictionary<string, string>(task.ActionParameters ?? new()),
            SecretParameters = task.SecretParameters,
            VmMask = task.VmMask,
            ExpectedOutput = task.ExpectedOutput,
            ExpirationSeconds = task.ExpirationSeconds,
            DelaySeconds = task.DelaySeconds,
            IntervalSeconds = task.IntervalSeconds,
            Iterations = Math.Max(1, task.Iterations),
            IterationTermination = task.IterationTermination,
            TriggerCondition = task.TriggerCondition,
            TriggerTaskId = task.TriggerTaskId,
            Repeatable = task.Repeatable,
        };
    }

    /// <summary>Makes runs of tasks, and reads the task back out of a run.</summary>
    public static class AutomationRuns
    {
        private static readonly JsonSerializerOptions Json = new() { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };

        public static AutomationRunEntity New(
            AutomationTaskEntity task,
            Guid mselId,
            AutomationRunKind kind,
            AutomationRunSource source,
            DateTime dueAt,
            Guid? deploymentId,
            Guid? playerViewId,
            Guid? requestedBy,
            Guid? parentRunId = null,
            Guid? rerunOfRunId = null,
            bool triggerChain = true,
            IEnumerable<Guid> followOnTaskIds = null) => new()
            {
                Id = Guid.NewGuid(),
                MselId = mselId,
                AutomationTaskId = task.Id,
                ScenarioEventId = task.ScenarioEventId,
                TaskName = task.Name,
                Action = task.Action,
                ConfigSnapshot = JsonSerializer.Serialize(AutomationTaskSnapshot.Of(task), Json),
                Kind = kind,
                Source = source,
                ParentRunId = parentRunId,
                RerunOfRunId = rerunOfRunId,
                DeploymentId = kind == AutomationRunKind.Test ? null : deploymentId,
                PlayerViewId = playerViewId,
                RequestedBy = requestedBy,
                TriggerChain = triggerChain,
                FollowOnTaskIds = followOnTaskIds == null ? null : string.Join(",", followOnTaskIds),
                Status = AutomationStatus.Pending,
                DueAt = dueAt,
                CreatedBy = requestedBy ?? Guid.Empty,
            };

        public static AutomationTaskSnapshot Snapshot(AutomationRunEntity run) =>
            string.IsNullOrEmpty(run.ConfigSnapshot) ? null : JsonSerializer.Deserialize<AutomationTaskSnapshot>(run.ConfigSnapshot, Json);

        public static IReadOnlyList<Guid> FollowOn(AutomationRunEntity run) =>
            string.IsNullOrEmpty(run.FollowOnTaskIds)
                ? Array.Empty<Guid>()
                : run.FollowOnTaskIds.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(Guid.Parse).ToList();

        /// <summary>
        /// Locks the tasks' rows until the transaction ends, so two requests that each check a task has no run going and
        /// then make one cannot both make it. Every path that makes an exercise run of a task takes this lock first.
        /// </summary>
        public static Task LockTasksAsync(BlueprintContext db, IEnumerable<Guid> taskIds, CancellationToken ct)
        {
            if (db.Database.CurrentTransaction == null)
                throw new InvalidOperationException("Locking automation tasks needs a transaction.");
            var ids = taskIds.Distinct().ToArray();
            return ids.Length == 0
                ? Task.CompletedTask
                : db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM automation_tasks WHERE id = ANY({ids}) FOR UPDATE", ct);
        }

        /// <summary>Locks the runs' rows until the transaction ends, so a cancel, a claim, a recovery and a worker never write over each other.</summary>
        public static Task LockRunsAsync(BlueprintContext db, IEnumerable<Guid> runIds, CancellationToken ct)
        {
            if (db.Database.CurrentTransaction == null)
                throw new InvalidOperationException("Locking automation runs needs a transaction.");
            var ids = runIds.Distinct().ToArray();
            return ids.Length == 0
                ? Task.CompletedTask
                : db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM automation_runs WHERE id = ANY({ids}) FOR UPDATE", ct);
        }

        /// <summary>
        /// Holds the MSEL's row, shared, until the transaction ends, and says whether it is still deployed in the given
        /// deployment. Ending a deployment changes that row, so it waits for the holder: a run made under this lock is
        /// seen by the cancel that follows, and one asked for after it is refused. A caller that goes on to change the
        /// MSEL holds it <paramref name="forChange"/>, as that change would: two holders of a shared lock that both then
        /// change the row would wait on each other. Take the tasks' locks first: every path locks tasks, then the MSEL.
        /// </summary>
        public static async Task<bool> HoldDeploymentAsync(BlueprintContext db, Guid mselId, Guid? deploymentId, CancellationToken ct,
            bool forChange = false)
        {
            if (db.Database.CurrentTransaction == null)
                throw new InvalidOperationException("Holding a deployment needs a transaction.");
            if (forChange)
                await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM msels WHERE id = {mselId} FOR NO KEY UPDATE", ct);
            else
                await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM msels WHERE id = {mselId} FOR SHARE", ct);
            return deploymentId != null && await db.Msels.AsNoTracking()
                .AnyAsync(m => m.Id == mselId && m.Status == MselItemStatus.Deployed && m.DeploymentId == deploymentId, ct);
        }

        /// <summary>
        /// Why an exercise run of a task should not be made now, or null: one is already going, or the task runs only once
        /// and has succeeded in this deployment. Every way a task is run asks this, under <see cref="LockTasksAsync"/>.
        /// </summary>
        public static async Task<string> WhyNotRunAsync(BlueprintContext db, AutomationTaskEntity task, Guid? deploymentId, CancellationToken ct)
        {
            var runs = db.AutomationRuns.Where(r => r.AutomationTaskId == task.Id && r.Kind == AutomationRunKind.Exercise && r.DeploymentId == deploymentId);
            if (await runs.AnyAsync(r => r.Status == AutomationStatus.Pending || r.Status == AutomationStatus.Queued || r.Status == AutomationStatus.Running, ct))
                return "is already running";
            if (!task.Repeatable && await runs.AnyAsync(r => r.Status == AutomationStatus.Succeeded, ct))
                return "has succeeded and runs only once";
            return null;
        }

        /// <summary>A run of a task that was not run, recorded so the facilitator sees why.</summary>
        public static AutomationRunEntity Skipped(AutomationTaskEntity task, AutomationRunEntity parent, AutomationRunSource source, string why, DateTime now)
        {
            var run = New(task, parent.MselId, parent.Kind, source, now, parent.DeploymentId, parent.PlayerViewId, parent.RequestedBy,
                parentRunId: parent.Id, triggerChain: false);
            run.Status = AutomationStatus.Skipped;
            run.EndedAt = now;
            run.StatusDetail = $"{task.Name} {why}, so it was not run again.";
            return run;
        }

        /// <summary>Whether a status ends a run.</summary>
        public static bool IsFinal(AutomationStatus status) =>
            status is AutomationStatus.Succeeded or AutomationStatus.Failed or AutomationStatus.Expired
                or AutomationStatus.Cancelled or AutomationStatus.Error or AutomationStatus.Skipped;

        /// <summary>The triggers of the tasks a run's outcome starts: Steamfitter's rules.</summary>
        public static AutomationTrigger[] TriggersFor(AutomationStatus status) => status switch
        {
            AutomationStatus.Succeeded => new[] { AutomationTrigger.OnSuccess, AutomationTrigger.OnCompletion },
            AutomationStatus.Failed => new[] { AutomationTrigger.OnFailure, AutomationTrigger.OnCompletion },
            AutomationStatus.Expired => new[] { AutomationTrigger.OnExpiration },
            _ => Array.Empty<AutomationTrigger>(),
        };

        /// <summary>The status of a run from its results: Failed if any failed, else the first that did not succeed, else Succeeded.</summary>
        public static AutomationStatus Overall(IEnumerable<AutomationStatus> results)
        {
            var list = results.ToList();
            if (list.Count == 0)
                return AutomationStatus.Error;
            if (list.Contains(AutomationStatus.Failed))
                return AutomationStatus.Failed;
            var others = list.Where(s => s != AutomationStatus.Succeeded).ToList();
            return others.Count == 0 ? AutomationStatus.Succeeded : others[0];
        }

        /// <summary>Whether a run that ended an iteration with this status runs another.</summary>
        public static bool RunsAgain(AutomationTaskSnapshot task, int iterationsDone, AutomationStatus status)
        {
            if (status is AutomationStatus.Cancelled or AutomationStatus.Error)
                return false;
            if (iterationsDone >= Math.Max(1, task.Iterations))
                return false;
            return task.IterationTermination switch
            {
                AutomationIterationTermination.UntilSuccess => status != AutomationStatus.Succeeded,
                AutomationIterationTermination.UntilFailure => status != AutomationStatus.Failed,
                _ => true,
            };
        }
    }
}
