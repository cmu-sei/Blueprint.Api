// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using Blueprint.Api.Data.Enumerations;
using Blueprint.Api.Data.Models;

namespace Blueprint.Api.Automation
{
    /// <summary>A task's points: its own, and with the tasks its outcome triggers.</summary>
    /// <param name="Score">The task's own points, which a disabled task does not offer.</param>
    /// <param name="TotalScore">The most the task and the tasks it triggers can earn together.</param>
    /// <param name="ScoreEarned">The task's own points, once its newest run succeeded.</param>
    /// <param name="TotalScoreEarned">What the task and the tasks it triggers have earned.</param>
    public record AutomationTaskScore(int Score, int TotalScore, int ScoreEarned, int TotalScoreEarned);

    /// <summary>A deployment's points, and each task's.</summary>
    public record AutomationScore(int Score, int ScoreEarned, IReadOnlyDictionary<Guid, AutomationTaskScore> Tasks);

    /// <summary>
    /// Scores a MSEL's tasks as Steamfitter scored a scenario's. A task earns its points when its newest run in the
    /// deployment succeeded. The points it can earn with the tasks it triggers take the best way its run can go: the
    /// tasks on completion count either way, with the larger of the tasks on success and on failure, unless the tasks on
    /// expiration offer more. The MSEL's score adds up the tasks no task triggers.
    /// </summary>
    public static class AutomationScoring
    {
        public static AutomationScore Calculate(
            IReadOnlyCollection<AutomationTaskEntity> tasks,
            IReadOnlyDictionary<Guid, AutomationStatus> latestStatus)
        {
            var ids = tasks.Select(t => t.Id).ToHashSet();
            // a trigger the writer did not keep, or on a task outside the set, leaves the task a root
            Guid? TriggeredBy(AutomationTaskEntity t) =>
                t.TriggerTaskId is Guid id && ids.Contains(id) && t.TriggerCondition is not (AutomationTrigger.Manual or AutomationTrigger.AtEventTime)
                    ? id
                    : null;
            var children = tasks
                .Where(t => TriggeredBy(t) != null)
                .GroupBy(t => TriggeredBy(t).Value)
                .ToDictionary(g => g.Key, g => g.ToList());
            var scores = new Dictionary<Guid, AutomationTaskScore>();

            AutomationTaskScore Score(AutomationTaskEntity task, HashSet<Guid> path)
            {
                if (scores.TryGetValue(task.Id, out var known))
                    return known;
                var own = task.Enabled ? Math.Max(0, task.Score) : 0;
                var earned = latestStatus.TryGetValue(task.Id, out var status) && status == AutomationStatus.Succeeded ? own : 0;
                // the writer refuses a loop; one in old data counts each task once
                var kids = (children.GetValueOrDefault(task.Id) ?? new())
                    .Where(c => !path.Contains(c.Id))
                    .Select(c => (Task: c, Score: Score(c, new HashSet<Guid>(path) { c.Id })))
                    .ToList();
                int Sum(AutomationTrigger trigger) =>
                    kids.Where(k => k.Task.TriggerCondition == trigger).Sum(k => k.Score.TotalScore);
                var completion = Sum(AutomationTrigger.OnCompletion) +
                    Math.Max(Sum(AutomationTrigger.OnSuccess), Sum(AutomationTrigger.OnFailure));
                var total = own + Math.Max(completion, Sum(AutomationTrigger.OnExpiration));
                var totalEarned = earned + kids.Sum(k => k.Score.TotalScoreEarned);
                return scores[task.Id] = new AutomationTaskScore(own, total, earned, totalEarned);
            }

            var roots = tasks.Where(t => TriggeredBy(t) == null).ToList();
            foreach (var root in roots)
                Score(root, new HashSet<Guid> { root.Id });
            // a task on a loop no root reaches is still scored
            foreach (var task in tasks.Where(t => !scores.ContainsKey(t.Id)))
                Score(task, new HashSet<Guid> { task.Id });
            return new AutomationScore(
                roots.Sum(r => scores[r.Id].TotalScore),
                roots.Sum(r => scores[r.Id].TotalScoreEarned),
                scores);
        }

        /// <summary>Each task's newest exercise run status in the deployment, from the MSEL's runs.</summary>
        public static Dictionary<Guid, AutomationStatus> LatestStatus(IEnumerable<AutomationRunEntity> runs, Guid? deploymentId) =>
            runs
                .Where(r => r.Kind == AutomationRunKind.Exercise && r.AutomationTaskId != null && deploymentId != null && r.DeploymentId == deploymentId)
                .GroupBy(r => r.AutomationTaskId.Value)
                .ToDictionary(g => g.Key, g => g.OrderByDescending(r => r.DateCreated).ThenByDescending(r => r.DueAt).First().Status);
    }
}
