// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Blueprint.Api.Data;
using Blueprint.Api.Data.Enumerations;
using Blueprint.Api.Data.Models;
using Blueprint.Api.Infrastructure.Exceptions;
using Blueprint.Api.ViewModels;
using Microsoft.EntityFrameworkCore;

namespace Blueprint.Api.Automation
{
    /// <summary>
    /// Saves a scenario event's automation tasks as an edit of the event lists them: checks them against the action
    /// catalog, encrypts their secrets, and keeps every trigger inside the MSEL and free of loops. The caller saves.
    /// </summary>
    public interface IAutomationTaskWriter
    {
        Task ApplyAsync(Guid mselId, Guid scenarioEventId, IReadOnlyList<AutomationTaskFields> tasks, Guid userId, CancellationToken ct);

        /// <summary>
        /// A task the form describes, checked as a save would check it but not saved: what a test runs. Its secrets start
        /// from <paramref name="stored"/>'s, when it is a saved task.
        /// </summary>
        AutomationTaskEntity Draft(Guid mselId, Guid scenarioEventId, AutomationTaskFields fields, AutomationTaskEntity stored);
    }

    public class AutomationTaskWriter : IAutomationTaskWriter
    {
        public const int MaxSeconds = 7 * 24 * 3600;
        public const int MaxIterations = 1000;
        public const int MaxScore = 100000;

        /// <summary>
        /// Brings a task read from a file within what the writer accepts: its numbers into their ranges, and an unknown
        /// trigger or termination to the default. True when anything changed.
        /// </summary>
        public static bool BringWithinLimits(AutomationTaskEntity task)
        {
            var changed = false;
            int Clamp(int value, int min, int max)
            {
                var within = Math.Clamp(value, min, max);
                changed |= within != value;
                return within;
            }
            task.Score = Clamp(task.Score, 0, MaxScore);
            task.Iterations = Clamp(task.Iterations, 1, MaxIterations);
            task.DelaySeconds = Clamp(task.DelaySeconds, 0, MaxSeconds);
            task.ExpirationSeconds = Clamp(task.ExpirationSeconds, 0, MaxSeconds);
            task.IntervalSeconds = Clamp(task.IntervalSeconds, 0, MaxSeconds);
            if (!Enum.IsDefined(task.TriggerCondition))
            {
                task.TriggerCondition = AutomationTrigger.Manual;
                task.TriggerTaskId = null;
                changed = true;
            }
            if (!Enum.IsDefined(task.IterationTermination))
            {
                task.IterationTermination = AutomationIterationTermination.IterationCount;
                changed = true;
            }
            return changed;
        }
        private readonly BlueprintContext _db;
        private readonly IAutomationActionCatalog _catalog;
        private readonly IAutomationSecretProtector _secrets;

        public AutomationTaskWriter(BlueprintContext db, IAutomationActionCatalog catalog, IAutomationSecretProtector secrets)
        {
            _db = db;
            _catalog = catalog;
            _secrets = secrets;
        }

        public async Task ApplyAsync(Guid mselId, Guid scenarioEventId, IReadOnlyList<AutomationTaskFields> tasks, Guid userId, CancellationToken ct)
        {
            tasks ??= Array.Empty<AutomationTaskFields>();
            foreach (var task in tasks)
                task.Id ??= Guid.NewGuid();
            var ids = tasks.Select(t => t.Id.Value).ToList();
            if (ids.Distinct().Count() != ids.Count)
                throw new BadRequestException("Two of the event's automation tasks have the same id.");

            var existing = await _db.AutomationTasks.Where(t => t.ScenarioEventId == scenarioEventId).ToListAsync(ct);
            var elsewhere = await _db.AutomationTasks.AsNoTracking()
                .Where(t => ids.Contains(t.Id) && t.ScenarioEventId != scenarioEventId)
                .Select(t => t.Id)
                .ToListAsync(ct);
            if (elsewhere.Count > 0)
                throw new BadRequestException($"Automation task {elsewhere[0]} belongs to another scenario event.");
            // the triggers of the MSEL's other tasks, for finding loops
            var others = await _db.AutomationTasks.AsNoTracking()
                .Where(t => t.MselId == mselId && t.ScenarioEventId != scenarioEventId)
                .Select(t => new { t.Id, t.TriggerTaskId })
                .ToDictionaryAsync(t => t.Id, t => t.TriggerTaskId, ct);

            var errors = new List<string>();
            var entities = new List<AutomationTaskEntity>();
            for (var i = 0; i < tasks.Count; i++)
            {
                var fields = tasks[i];
                var entity = existing.SingleOrDefault(t => t.Id == fields.Id) ?? new AutomationTaskEntity { Id = fields.Id.Value, CreatedBy = userId };
                var label = $"Task {i + 1}{(string.IsNullOrWhiteSpace(fields.Name) ? "" : $" ({fields.Name})")}";
                errors.AddRange(Apply(fields, entity, mselId, scenarioEventId, i).Select(e => $"{label}: {e}"));
                // the trigger Apply kept: a Manual or At Event Time task's old trigger id is dropped, not checked
                if (entity.TriggerTaskId is Guid trigger && !ids.Contains(trigger) && !others.ContainsKey(trigger))
                    errors.Add($"{label}: the task that triggers it is not one of this MSEL's.");
                entity.ModifiedBy = existing.Contains(entity) ? userId : null;
                entities.Add(entity);
            }

            // a trigger that leads back to its own task would run forever
            var triggers = new Dictionary<Guid, Guid?>(others);
            foreach (var entity in entities)
                triggers[entity.Id] = entity.TriggerTaskId;
            foreach (var entity in entities)
            {
                // a task that only leads into a loop of others is not in it; that loop is reported on its own tasks
                var seen = new HashSet<Guid>();
                for (var at = entity.TriggerTaskId; at != null && triggers.ContainsKey(at.Value) && seen.Add(at.Value); at = triggers[at.Value])
                {
                    if (at == entity.Id)
                    {
                        errors.Add($"{entity.Name}: its triggers lead back to it.");
                        break;
                    }
                }
            }
            if (errors.Count > 0)
                throw new BadRequestException(string.Join(" ", errors.Distinct()));

            // tasks the list leaves out are deleted, and a task they triggered is now run by hand
            var removed = existing.Where(t => !ids.Contains(t.Id)).ToList();
            if (removed.Count > 0)
            {
                var removedIds = removed.Select(t => t.Id).ToList();
                var orphans = await _db.AutomationTasks
                    .Where(t => t.TriggerTaskId != null && removedIds.Contains(t.TriggerTaskId.Value) && !ids.Contains(t.Id))
                    .ToListAsync(ct);
                foreach (var orphan in orphans)
                {
                    orphan.TriggerTaskId = null;
                    orphan.TriggerCondition = AutomationTrigger.Manual;
                    orphan.ModifiedBy = userId;
                }
                _db.AutomationTasks.RemoveRange(removed);
            }
            foreach (var entity in entities.Where(e => !existing.Contains(e)))
                _db.AutomationTasks.Add(entity);
        }

        public AutomationTaskEntity Draft(Guid mselId, Guid scenarioEventId, AutomationTaskFields fields, AutomationTaskEntity stored)
        {
            if (fields == null)
                throw new BadRequestException("The test has no task.");
            var draft = new AutomationTaskEntity
            {
                Id = stored?.Id ?? fields.Id ?? Guid.NewGuid(),
                SecretParameters = stored?.SecretParameters,
                Action = stored?.Action ?? fields.Action,
            };
            var errors = Apply(fields, draft, mselId, scenarioEventId, 0, checkTrigger: false);
            if (errors.Count > 0)
                throw new BadRequestException(string.Join(" ", errors));
            return draft;
        }

        /// <summary>Copies the fields onto the task, and says what is wrong with them.</summary>
        private List<string> Apply(AutomationTaskFields fields, AutomationTaskEntity entity, Guid mselId, Guid scenarioEventId, int order, bool checkTrigger = true)
        {
            var errors = new List<string>();
            var definition = Enum.IsDefined(fields.Action) ? _catalog.Get(fields.Action) : null;
            if (definition == null)
                return new List<string> { $"{fields.Action} is not an automation action." };
            if (string.IsNullOrWhiteSpace(fields.Name))
                errors.Add("a name is required.");

            // a secret of another action does not survive a change of action
            var allowedSecrets = _catalog.SecretKeys(fields.Action);
            var given = (fields.SecretParameters ?? new())
                .Where(kv => allowedSecrets.Contains(kv.Key))
                .GroupBy(kv => allowedSecrets.First(k => string.Equals(k, kv.Key, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            foreach (var twice in given.Where(g => g.Count() > 1))
                errors.Add($"the secret {twice.Key} is given more than once.");
            var changes = given.ToDictionary(g => g.Key, g => g.Last().Value ?? string.Empty);
            foreach (var key in _secrets.Keys(entity.SecretParameters).Where(k => !allowedSecrets.Contains(k)))
                changes[key] = string.Empty;
            try
            {
                entity.SecretParameters = _secrets.Update(entity.SecretParameters, changes);
            }
            catch (AutomationConfigurationException ex)
            {
                errors.Add(ex.Message);
            }

            entity.MselId = mselId;
            entity.ScenarioEventId = scenarioEventId;
            entity.DisplayOrder = order;
            entity.Name = fields.Name?.Trim();
            entity.Description = fields.Description;
            entity.Action = fields.Action;
            entity.ActionParameters = _catalog.Normalize(fields.Action, fields.ActionParameters);
            entity.VmMask = string.IsNullOrWhiteSpace(fields.VmMask) ? null : fields.VmMask.Trim();
            entity.ExpectedOutput = string.IsNullOrEmpty(fields.ExpectedOutput) ? null : fields.ExpectedOutput;
            entity.ExpirationSeconds = fields.ExpirationSeconds;
            entity.DelaySeconds = fields.DelaySeconds;
            entity.IntervalSeconds = fields.IntervalSeconds;
            entity.Iterations = fields.Iterations;
            entity.IterationTermination = fields.IterationTermination;
            entity.TriggerCondition = fields.TriggerCondition;
            entity.TriggerTaskId = fields.TriggerCondition is AutomationTrigger.Manual or AutomationTrigger.AtEventTime ? null : fields.TriggerTaskId;
            entity.Enabled = fields.Enabled;
            entity.Repeatable = fields.Repeatable;
            entity.Score = fields.Score;
            entity.UserExecutable = fields.UserExecutable;

            errors.AddRange(_catalog.Validate(fields.Action, entity.ActionParameters, _secrets.Keys(entity.SecretParameters)));
            if (definition.RequiresVm && entity.VmMask == null)
                errors.Add("a VM mask is required, since the action runs on VMs.");
            if (!Enum.IsDefined(fields.TriggerCondition))
                errors.Add($"{fields.TriggerCondition} is not a trigger.");
            if (!Enum.IsDefined(fields.IterationTermination))
                errors.Add($"{fields.IterationTermination} is not an iteration termination.");
            if (checkTrigger && fields.TriggerCondition is not (AutomationTrigger.Manual or AutomationTrigger.AtEventTime))
            {
                if (fields.TriggerTaskId == null)
                    errors.Add("the task that triggers it is required.");
                else if (fields.TriggerTaskId == entity.Id)
                    errors.Add("it cannot trigger itself.");
            }
            if (fields.Iterations < 1 || fields.Iterations > MaxIterations)
                errors.Add($"iterations must be from 1 to {MaxIterations}.");
            foreach (var (name, value) in new[] { ("the expiration", fields.ExpirationSeconds), ("the delay", fields.DelaySeconds), ("the interval", fields.IntervalSeconds) })
                if (value < 0 || value > MaxSeconds)
                    errors.Add($"{name} must be from 0 to {MaxSeconds} seconds.");
            if (fields.Score < 0 || fields.Score > MaxScore)
                errors.Add($"the score must be from 0 to {MaxScore}.");
            return errors;
        }
    }
}
