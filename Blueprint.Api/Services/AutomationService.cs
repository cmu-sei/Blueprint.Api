// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using Blueprint.Api.Automation;
using Blueprint.Api.Data;
using Blueprint.Api.Data.Enumerations;
using Blueprint.Api.Data.Models;
using Blueprint.Api.Infrastructure.Authorization;
using Blueprint.Api.Infrastructure.Exceptions;
using Blueprint.Api.Infrastructure.Extensions;
using Blueprint.Api.Infrastructure.Mappings;
using Blueprint.Api.ViewModels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blueprint.Api.Services
{
    /// <summary>
    /// Automation tasks and their runs. Who may do what:
    /// <list type="bullet">
    /// <item>read a MSEL's tasks: anyone who may read the MSEL's events, a participant excepted;</item>
    /// <item>test a task: whoever may edit the MSEL's events, against a Player view they can see themselves;</item>
    /// <item>read runs: whoever may watch the Facilitator View (<see cref="MselFacilitatorViewRequirement"/>);</item>
    /// <item>run, rerun and cancel an exercise run: whoever may run the MSEL (<see cref="MselFacilitatorRequirement"/>),
    /// while it is deployed; cancel a test: whoever may test;</item>
    /// <item>run a task marked for participants, and read the MSEL's Tasks page: anyone who may read the MSEL, a
    /// participant included, while it is deployed and its automation is not paused.</item>
    /// </list>
    /// </summary>
    public interface IAutomationService
    {
        IReadOnlyList<AutomationActionDefinition> GetActions();
        Task<IEnumerable<AutomationTask>> GetTasksByMselAsync(Guid mselId, bool hasViewMsels, bool hasCreateMsels, CancellationToken ct);
        Task<IEnumerable<AutomationTask>> GetTasksByScenarioEventAsync(Guid scenarioEventId, bool hasViewMsels, bool hasCreateMsels, CancellationToken ct);
        Task<AutomationRun> TestAsync(AutomationTestForm form, bool hasEditMsels, CancellationToken ct);
        Task<AutomationVmPreview> PreviewVmsAsync(Guid mselId, AutomationVmPreviewForm form, bool hasEditMsels, CancellationToken ct);
        Task<AutomationRun> RunAsync(Guid taskId, AutomationRunForm form, bool hasEditMsels, CancellationToken ct);
        Task<AutomationRun> RerunAsync(Guid runId, AutomationRunForm form, bool hasEditMsels, CancellationToken ct);
        Task<AutomationRun> CancelAsync(Guid runId, bool hasEditMsels, CancellationToken ct);
        Task<IEnumerable<AutomationRun>> GetRunsByMselAsync(Guid mselId, AutomationRunKind? kind, Guid? taskId, DateTime? since, bool hasViewMsels, CancellationToken ct);
        Task<AutomationRun> GetRunAsync(Guid runId, bool hasViewMsels, CancellationToken ct);
        Task<ParticipantTasks> GetParticipantTasksAsync(Guid mselId, bool hasViewMsels, CancellationToken ct);
        Task<ParticipantTask> RunAsParticipantAsync(Guid mselId, Guid taskId, bool hasEditMsels, CancellationToken ct);
    }

    public class AutomationService : IAutomationService
    {
        private readonly BlueprintContext _context;
        private readonly ClaimsPrincipal _user;
        private readonly IAutomationActionCatalog _catalog;
        private readonly IAutomationTaskWriter _writer;
        private readonly IAutomationVmResolver _vms;
        private readonly IAutomationRunControl _control;
        private readonly AutomationRunRegistry _registry;
        private readonly IPlayerService _playerService;
        private readonly TimeProvider _time;
        private readonly ILogger<AutomationService> _logger;

        public AutomationService(
            BlueprintContext context,
            IPrincipal user,
            IAutomationActionCatalog catalog,
            IAutomationTaskWriter writer,
            IAutomationVmResolver vms,
            IAutomationRunControl control,
            AutomationRunRegistry registry,
            IPlayerService playerService,
            TimeProvider time,
            ILogger<AutomationService> logger)
        {
            _context = context;
            _user = user as ClaimsPrincipal;
            _catalog = catalog;
            _writer = writer;
            _vms = vms;
            _control = control;
            _registry = registry;
            _playerService = playerService;
            _time = time;
            _logger = logger;
        }

        private DateTime Now => _time.GetUtcNow().UtcDateTime;
        private Guid UserId => _user.GetId();

        public IReadOnlyList<AutomationActionDefinition> GetActions() => _catalog.Actions;

        public async Task<IEnumerable<AutomationTask>> GetTasksByMselAsync(Guid mselId, bool hasViewMsels, bool hasCreateMsels, CancellationToken ct)
        {
            if (!hasViewMsels && !await MselViewRequirement.IsMet(UserId, mselId, hasCreateMsels, _context))
                throw new ForbiddenException();
            // a participant sees the Exercise View, which shows no automation
            if (await MselParticipantScope.IsLimitedAsync(UserId, mselId, hasViewMsels, _context))
                return new List<AutomationTask>();
            var tasks = await _context.AutomationTasks.AsNoTracking()
                .Where(t => t.MselId == mselId)
                .OrderBy(t => t.ScenarioEventId).ThenBy(t => t.DisplayOrder)
                .ToListAsync(ct);
            return tasks.Select(t => t.ToAutomationTask()).ToList();
        }

        public async Task<IEnumerable<AutomationTask>> GetTasksByScenarioEventAsync(Guid scenarioEventId, bool hasViewMsels, bool hasCreateMsels, CancellationToken ct)
        {
            var mselId = await _context.ScenarioEvents.Where(e => e.Id == scenarioEventId).Select(e => (Guid?)e.MselId).SingleOrDefaultAsync(ct)
                ?? throw new EntityNotFoundException<ScenarioEventEntity>($"ScenarioEvent not found {scenarioEventId}.");
            if (!hasViewMsels && !await MselViewRequirement.IsMet(UserId, mselId, hasCreateMsels, _context))
                throw new ForbiddenException();
            if (await MselParticipantScope.IsLimitedAsync(UserId, mselId, hasViewMsels, _context))
                return new List<AutomationTask>();
            var tasks = await _context.AutomationTasks.AsNoTracking()
                .Where(t => t.ScenarioEventId == scenarioEventId)
                .OrderBy(t => t.DisplayOrder)
                .ToListAsync(ct);
            return tasks.Select(t => t.ToAutomationTask()).ToList();
        }

        public async Task<AutomationRun> TestAsync(AutomationTestForm form, bool hasEditMsels, CancellationToken ct)
        {
            var scenarioEvent = await _context.ScenarioEvents.AsNoTracking().SingleOrDefaultAsync(e => e.Id == form.ScenarioEventId, ct)
                ?? throw new EntityNotFoundException<ScenarioEventEntity>($"ScenarioEvent not found {form.ScenarioEventId}.");
            await RequireEventEditorAsync(scenarioEvent.MselId, hasEditMsels);
            AutomationTaskEntity stored = null;
            if (form.TaskId != null)
            {
                stored = await _context.AutomationTasks.AsNoTracking().SingleOrDefaultAsync(t => t.Id == form.TaskId, ct)
                    ?? throw new EntityNotFoundException<AutomationTaskEntity>($"Automation task not found {form.TaskId}.");
                if (stored.ScenarioEventId != scenarioEvent.Id)
                    throw new BadRequestException("The task is not one of the event's.");
            }
            var draft = _writer.Draft(scenarioEvent.MselId, scenarioEvent.Id, form.Task, stored);
            var msel = await _context.Msels.SingleAsync(m => m.Id == scenarioEvent.MselId, ct);
            var definition = _catalog.Get(draft.Action);
            if (form.PlayerViewId != null)
                await RequireOwnViewAsync(form.PlayerViewId.Value, msel, ct);
            else if (definition.RequiresVm || definition.Requires.Contains("playerView"))
                throw new BadRequestException("Choose a Player view to test against.");

            var run = AutomationRuns.New(draft, msel.Id, AutomationRunKind.Test, AutomationRunSource.Manual, Now, null, form.PlayerViewId, UserId,
                triggerChain: form.IncludeChain && stored != null);
            // a draft that is not saved has no task to point at
            if (stored == null)
                run.AutomationTaskId = null;
            run.ScenarioEventId = scenarioEvent.Id;
            _context.AutomationRuns.Add(run);
            if (form.PlayerViewId != null)
                msel.AutomationTestPlayerViewId = form.PlayerViewId;
            await _context.SaveChangesAsync(ct);
            _registry.Wake();
            return run.ToAutomationRun();
        }

        public async Task<AutomationVmPreview> PreviewVmsAsync(Guid mselId, AutomationVmPreviewForm form, bool hasEditMsels, CancellationToken ct)
        {
            var msel = await _context.Msels.AsNoTracking().SingleOrDefaultAsync(m => m.Id == mselId, ct)
                ?? throw new EntityNotFoundException<MselEntity>($"MSEL not found {mselId}.");
            await RequireEventEditorAsync(mselId, hasEditMsels);
            await RequireOwnViewAsync(form.PlayerViewId, msel, ct);
            var preview = new AutomationVmPreview
            {
                MatchMode = (form.VmMask ?? string.Empty).Split(',').Any(p => Guid.TryParse(p.Trim(), out _)) ? "ids" : "name",
            };
            if (string.IsNullOrWhiteSpace(form.VmMask))
                return preview;
            try
            {
                preview.Vms = (await _vms.ResolveAsync(form.PlayerViewId, form.VmMask, ct))
                    .Select(v => new AutomationVmSummary { Id = v.Id, Name = v.Name })
                    .ToList();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Listing the VMs of Player view {ViewId} failed", form.PlayerViewId);
                preview.Error = $"Blueprint's service account could not list the VMs of this Player view: {ex.Message}";
            }
            return preview;
        }

        public async Task<AutomationRun> RunAsync(Guid taskId, AutomationRunForm form, bool hasEditMsels, CancellationToken ct)
        {
            var task = await _context.AutomationTasks.AsNoTracking().SingleOrDefaultAsync(t => t.Id == taskId, ct)
                ?? throw new EntityNotFoundException<AutomationTaskEntity>($"Automation task not found {taskId}.");
            var msel = await RequireFacilitatorOfDeployedAsync(task.MselId, hasEditMsels, ct);
            if (!task.Enabled)
                throw new ConflictException("The task is disabled.");
            await RequireNotRunningAsync(task, msel, ct);
            await RequireRepeatableAsync(task, msel, ct);
            var run = AutomationRuns.New(task, msel.Id, AutomationRunKind.Exercise, AutomationRunSource.Manual, Now,
                msel.DeploymentId, msel.PlayerViewId, UserId, triggerChain: form?.TriggerChain ?? true);
            _context.AutomationRuns.Add(run);
            await _context.SaveChangesAsync(ct);
            _registry.Wake();
            return run.ToAutomationRun();
        }

        public async Task<AutomationRun> RerunAsync(Guid runId, AutomationRunForm form, bool hasEditMsels, CancellationToken ct)
        {
            var original = await _context.AutomationRuns.AsNoTracking().SingleOrDefaultAsync(r => r.Id == runId, ct)
                ?? throw new EntityNotFoundException<AutomationRunEntity>($"Automation run not found {runId}.");
            if (original.Kind != AutomationRunKind.Exercise || original.AutomationTaskId == null)
                throw new BadRequestException("Only an exercise run of a task can be run again; test a task from its event.");
            var msel = await RequireFacilitatorOfDeployedAsync(original.MselId, hasEditMsels, ct);
            if (!AutomationRuns.IsFinal(original.Status))
                throw new ConflictException("The run has not ended.");
            var task = await _context.AutomationTasks.AsNoTracking().SingleOrDefaultAsync(t => t.Id == original.AutomationTaskId, ct)
                ?? throw new ConflictException("The task this run ran has been deleted.");
            await RequireRepeatableAsync(task, msel, ct);
            var run = AutomationRuns.New(task, msel.Id, AutomationRunKind.Exercise, AutomationRunSource.Rerun, Now,
                msel.DeploymentId, msel.PlayerViewId, UserId, rerunOfRunId: original.Id, triggerChain: form?.TriggerChain ?? true);
            _context.AutomationRuns.Add(run);
            await _context.SaveChangesAsync(ct);
            _registry.Wake();
            return run.ToAutomationRun();
        }

        public async Task<AutomationRun> CancelAsync(Guid runId, bool hasEditMsels, CancellationToken ct)
        {
            var run = await _context.AutomationRuns.AsNoTracking().SingleOrDefaultAsync(r => r.Id == runId, ct)
                ?? throw new EntityNotFoundException<AutomationRunEntity>($"Automation run not found {runId}.");
            if (run.Kind == AutomationRunKind.Test)
                await RequireEventEditorAsync(run.MselId, hasEditMsels);
            else if (!hasEditMsels && !await MselFacilitatorRequirement.IsMet(UserId, run.MselId, _context))
                throw new ForbiddenException();
            if (!await _control.CancelAsync(runId, ct))
                throw new ConflictException("The run has already ended.");
            var stored = await _context.AutomationRuns.AsNoTracking().SingleAsync(r => r.Id == runId, ct);
            return stored.ToAutomationRun();
        }

        public async Task<IEnumerable<AutomationRun>> GetRunsByMselAsync(Guid mselId, AutomationRunKind? kind, Guid? taskId, DateTime? since, bool hasViewMsels, CancellationToken ct)
        {
            if (!await _context.Msels.AnyAsync(m => m.Id == mselId, ct))
                throw new EntityNotFoundException<MselEntity>($"MSEL not found {mselId}.");
            await RequireWatcherAsync(mselId, hasViewMsels);
            var query = _context.AutomationRuns.AsNoTracking().Where(r => r.MselId == mselId);
            if (kind != null)
                query = query.Where(r => r.Kind == kind);
            if (taskId != null)
                query = query.Where(r => r.AutomationTaskId == taskId);
            if (since != null)
            {
                var from = DateTime.SpecifyKind(since.Value, DateTimeKind.Utc);
                query = query.Where(r => r.DateCreated >= from || r.DateModified >= from);
            }
            var runs = await query
                .Include(r => r.Results)
                .OrderByDescending(r => r.DateCreated)
                .Take(2000)
                .AsSplitQuery()
                .ToListAsync(ct);
            return runs.Select(r => r.ToAutomationRun()).ToList();
        }

        public async Task<AutomationRun> GetRunAsync(Guid runId, bool hasViewMsels, CancellationToken ct)
        {
            var run = await _context.AutomationRuns.AsNoTracking().Include(r => r.Results).SingleOrDefaultAsync(r => r.Id == runId, ct)
                ?? throw new EntityNotFoundException<AutomationRunEntity>($"Automation run not found {runId}.");
            await RequireWatcherAsync(run.MselId, hasViewMsels);
            return run.ToAutomationRun();
        }

        // ---------------------------------------------------------------------------------------------

        /// <summary>The check an edit of a scenario event makes: EditMsels, or the MSEL's Owner, Approver or Editor.</summary>
        private async Task RequireEventEditorAsync(Guid mselId, bool hasEditMsels)
        {
            if (hasEditMsels ||
                await MselOwnerRequirement.IsMet(UserId, mselId, _context) ||
                await MselApproverRequirement.IsMet(UserId, mselId, _context) ||
                await MselEditorRequirement.IsMet(UserId, mselId, _context))
                return;
            throw new ForbiddenException();
        }

        private async Task RequireWatcherAsync(Guid mselId, bool hasViewMsels)
        {
            if (!hasViewMsels && !await MselFacilitatorViewRequirement.IsMet(UserId, mselId, _context))
                throw new ForbiddenException();
        }

        private async Task<MselEntity> RequireFacilitatorOfDeployedAsync(Guid mselId, bool hasEditMsels, CancellationToken ct)
        {
            if (!hasEditMsels && !await MselFacilitatorRequirement.IsMet(UserId, mselId, _context))
                throw new ForbiddenException();
            return await RequireDeployedAsync(mselId, ct);
        }

        private async Task<MselEntity> RequireDeployedAsync(Guid mselId, CancellationToken ct)
        {
            var msel = await _context.Msels.AsNoTracking().SingleAsync(m => m.Id == mselId, ct);
            if (msel.Status != MselItemStatus.Deployed || msel.DeploymentId == null)
                throw new ConflictException("The MSEL is not deployed.");
            return msel;
        }

        /// <summary>A run by hand waits for the task's run that has not ended, so two do not run at once.</summary>
        private async Task RequireNotRunningAsync(AutomationTaskEntity task, MselEntity msel, CancellationToken ct)
        {
            var running = await _context.AutomationRuns.AnyAsync(r =>
                r.AutomationTaskId == task.Id && r.Kind == AutomationRunKind.Exercise && r.DeploymentId == msel.DeploymentId &&
                (r.Status == AutomationStatus.Pending || r.Status == AutomationStatus.Queued || r.Status == AutomationStatus.Running), ct);
            if (running)
                throw new ConflictException("The task is already running.");
        }

        /// <summary>
        /// Runs a task marked for participants, as the MSEL's Tasks page does, and answers the task as that page shows it:
        /// a participant is not given the run itself. The tasks it triggers run too.
        /// </summary>
        public async Task<ParticipantTask> RunAsParticipantAsync(Guid mselId, Guid taskId, bool hasEditMsels, CancellationToken ct)
        {
            // ViewMsels only reads: a run takes the MSEL itself, through a team or a role, or EditMsels
            if (!hasEditMsels && !await MselViewRequirement.IsMet(UserId, mselId, _context))
                throw new ForbiddenException();
            var task = await _context.AutomationTasks.AsNoTracking()
                .SingleOrDefaultAsync(t => t.Id == taskId && t.MselId == mselId && t.UserExecutable && t.Enabled, ct)
                ?? throw new EntityNotFoundException<AutomationTaskEntity>($"Participant task not found {taskId}.");
            var msel = await RequireDeployedAsync(mselId, ct);
            if (msel.AutomationPaused)
                throw new ConflictException("The exercise's automation is paused.");
            await RequireNotRunningAsync(task, msel, ct);
            await RequireRepeatableAsync(task, msel, ct);
            var run = AutomationRuns.New(task, msel.Id, AutomationRunKind.Exercise, AutomationRunSource.Participant, Now,
                msel.DeploymentId, msel.PlayerViewId, UserId, triggerChain: true);
            _context.AutomationRuns.Add(run);
            await _context.SaveChangesAsync(ct);
            _registry.Wake();
            return (await GetParticipantTasksAsync(mselId, true, ct)).Tasks.Single(t => t.Id == taskId);
        }

        public async Task<ParticipantTasks> GetParticipantTasksAsync(Guid mselId, bool hasViewMsels, CancellationToken ct)
        {
            if (!hasViewMsels && !await MselViewRequirement.IsMet(UserId, mselId, _context))
                throw new ForbiddenException();
            var msel = await _context.Msels.AsNoTracking().SingleOrDefaultAsync(m => m.Id == mselId, ct)
                ?? throw new EntityNotFoundException<MselEntity>($"MSEL not found {mselId}.");
            var tasks = await _context.AutomationTasks.AsNoTracking()
                .Where(t => t.MselId == mselId)
                .Include(t => t.ScenarioEvent)
                .ToListAsync(ct);
            var runs = msel.DeploymentId == null
                ? new List<AutomationRunEntity>()
                : await _context.AutomationRuns.AsNoTracking()
                    .Where(r => r.MselId == mselId && r.Kind == AutomationRunKind.Exercise && r.DeploymentId == msel.DeploymentId && r.AutomationTaskId != null)
                    .ToListAsync(ct);
            var latest = runs
                .GroupBy(r => r.AutomationTaskId.Value)
                .ToDictionary(g => g.Key, g => g.OrderByDescending(r => r.DateCreated).ThenByDescending(r => r.DueAt).First());
            var score = AutomationScoring.Calculate(tasks, AutomationScoring.LatestStatus(runs, msel.DeploymentId));
            var succeeded = runs.Where(r => r.Status == AutomationStatus.Succeeded).Select(r => r.AutomationTaskId.Value).ToHashSet();
            var isDeployed = msel.Status == MselItemStatus.Deployed && msel.DeploymentId != null;

            string CannotRun(AutomationTaskEntity task)
            {
                if (!isDeployed) return "The exercise is not running.";
                if (msel.AutomationPaused) return "The exercise's automation is paused.";
                if (latest.TryGetValue(task.Id, out var run) && !AutomationRuns.IsFinal(run.Status)) return "The task is running.";
                if (!task.Repeatable && succeeded.Contains(task.Id)) return "The task has succeeded, and runs only once.";
                return null;
            }

            return new ParticipantTasks
            {
                MselId = msel.Id,
                MselName = msel.Name,
                IsDeployed = isDeployed,
                AutomationPaused = msel.AutomationPaused,
                Score = score.Score,
                ScoreEarned = score.ScoreEarned,
                Tasks = tasks
                    .Where(t => t.UserExecutable && t.Enabled)
                    .OrderBy(t => t.ScenarioEvent.DeltaSeconds)
                    .ThenBy(t => t.ScenarioEvent.GroupOrder)
                    .ThenBy(t => t.DisplayOrder)
                    .Select(t =>
                    {
                        var reason = CannotRun(t);
                        var run = latest.GetValueOrDefault(t.Id);
                        return new ParticipantTask
                        {
                            Id = t.Id,
                            ScenarioEventId = t.ScenarioEventId,
                            Name = t.Name,
                            Description = t.Description,
                            Status = run?.Status,
                            StatusAt = run?.EndedAt ?? run?.StartedAt ?? run?.DateCreated,
                            CanRun = reason == null,
                            CannotRunReason = reason,
                            TotalScore = score.Tasks[t.Id].TotalScore,
                            TotalScoreEarned = score.Tasks[t.Id].TotalScoreEarned,
                        };
                    })
                    .ToList(),
            };
        }

        /// <summary>A task that is not repeatable runs once per deployment.</summary>
        private async Task RequireRepeatableAsync(AutomationTaskEntity task, MselEntity msel, CancellationToken ct)
        {
            if (task.Repeatable)
                return;
            var succeeded = await _context.AutomationRuns.AnyAsync(r =>
                r.AutomationTaskId == task.Id && r.Kind == AutomationRunKind.Exercise &&
                r.DeploymentId == msel.DeploymentId && r.Status == AutomationStatus.Succeeded, ct);
            if (succeeded)
                throw new ConflictException("The task is not repeatable, and has already succeeded in this deployment.");
        }

        /// <summary>
        /// A test acts as Blueprint's service account, so it may use only a Player view the caller can see themselves, or
        /// the MSEL's own.
        /// </summary>
        private async Task RequireOwnViewAsync(Guid playerViewId, MselEntity msel, CancellationToken ct)
        {
            if (playerViewId == msel.PlayerViewId)
                return;
            var views = await _playerService.GetMyViewsAsync(ct);
            if (!(views ?? Enumerable.Empty<Player.Api.Client.View>()).Any(v => v.Id == playerViewId))
                throw new ForbiddenException("You can only test against a Player view you can see.");
        }
    }
}
