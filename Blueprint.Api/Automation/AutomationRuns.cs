// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Blueprint.Api.Data.Enumerations;
using Blueprint.Api.Data.Models;

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
