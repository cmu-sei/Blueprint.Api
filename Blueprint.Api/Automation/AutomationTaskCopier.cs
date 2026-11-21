// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using Blueprint.Api.Data.Enumerations;
using Blueprint.Api.Data.Models;

namespace Blueprint.Api.Automation
{
    /// <summary>
    /// Copies of automation tasks, for scenario events copied to another MSEL or a whole MSEL copied. A copied task
    /// triggered by another copied task is triggered by that one's copy; one whose trigger was not copied with it runs
    /// by hand. A task's secrets are copied as they are stored, when the caller keeps them: they are bound to their
    /// parameter, not their task.
    /// </summary>
    public static class AutomationTaskCopier
    {
        public static List<AutomationTaskEntity> Copy(IEnumerable<(AutomationTaskEntity Source, Guid ScenarioEventId)> tasks, Guid mselId, Guid createdBy)
        {
            var list = tasks.ToList();
            var ids = list.ToDictionary(t => t.Source.Id, _ => Guid.NewGuid());
            return list.Select(t =>
            {
                var source = t.Source;
                var copied = source.TriggerTaskId is Guid trigger && ids.TryGetValue(trigger, out var copy) ? copy : (Guid?)null;
                var keepsTrigger = copied != null || source.TriggerCondition is AutomationTrigger.Manual or AutomationTrigger.AtEventTime;
                return new AutomationTaskEntity
                {
                    Id = ids[source.Id],
                    MselId = mselId,
                    ScenarioEventId = t.ScenarioEventId,
                    DisplayOrder = source.DisplayOrder,
                    Name = source.Name,
                    Description = source.Description,
                    Action = source.Action,
                    ActionParameters = new Dictionary<string, string>(source.ActionParameters ?? new()),
                    SecretParameters = source.SecretParameters,
                    VmMask = source.VmMask,
                    ExpectedOutput = source.ExpectedOutput,
                    ExpirationSeconds = source.ExpirationSeconds,
                    DelaySeconds = source.DelaySeconds,
                    IntervalSeconds = source.IntervalSeconds,
                    Iterations = source.Iterations,
                    IterationTermination = source.IterationTermination,
                    TriggerCondition = keepsTrigger ? source.TriggerCondition : AutomationTrigger.Manual,
                    TriggerTaskId = copied,
                    Enabled = source.Enabled,
                    Repeatable = source.Repeatable,
                    Score = source.Score,
                    UserExecutable = source.UserExecutable,
                    CreatedBy = createdBy,
                };
            }).ToList();
        }

        /// <summary>
        /// Makes Manual each task on a loop of triggers, which would run its tasks over and over: the task writer refuses
        /// one, but a hand-edited file can carry it. Returns the names of the tasks changed.
        /// </summary>
        public static List<string> BreakTriggerLoops(IReadOnlyCollection<AutomationTaskEntity> tasks)
        {
            var byId = tasks.GroupBy(t => t.Id).ToDictionary(g => g.Key, g => g.First());
            var onLoops = new List<AutomationTaskEntity>();
            foreach (var task in tasks)
            {
                var seen = new HashSet<Guid>();
                for (var at = task.TriggerTaskId; at is Guid id && byId.TryGetValue(id, out var next) && seen.Add(id); at = next.TriggerTaskId)
                {
                    if (id == task.Id)
                    {
                        onLoops.Add(task);
                        break;
                    }
                }
            }
            foreach (var task in onLoops)
            {
                task.TriggerCondition = AutomationTrigger.Manual;
                task.TriggerTaskId = null;
            }
            return onLoops.Select(t => t.Name).ToList();
        }
    }
}
