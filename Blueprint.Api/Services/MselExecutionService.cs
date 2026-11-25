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
    /// Running a deployed MSEL from the Facilitator View: where it is, starting a move or a group, and pausing its
    /// scheduled automation. Starting a move or group is what Steamfitter's manual "Move Change" and "Inject Change"
    /// tasks did: it moves the MSEL's CITE evaluation and Gallery exhibit, as Blueprint's service account, then runs the
    /// manual tasks the facilitator chose, one after another.
    /// </summary>
    public interface IMselExecutionService
    {
        Task<MselExecution> GetAsync(Guid mselId, bool hasViewMsels, CancellationToken ct);
        Task<MselAdvanceResult> StartMoveAsync(Guid mselId, int moveNumber, MselAdvanceForm form, bool hasEditMsels, CancellationToken ct);
        Task<MselAdvanceResult> StartGroupAsync(Guid mselId, int moveNumber, int groupNumber, MselAdvanceForm form, bool hasEditMsels, CancellationToken ct);
        Task<MselExecution> SetPausedAsync(Guid mselId, bool paused, bool hasEditMsels, CancellationToken ct);
    }

    public class MselExecutionService : IMselExecutionService
    {
        private readonly BlueprintContext _context;
        private readonly ClaimsPrincipal _user;
        private readonly IAutomationClientFactory _clients;
        private readonly AutomationRunRegistry _registry;
        private readonly TimeProvider _time;
        private readonly ILogger<MselExecutionService> _logger;

        public MselExecutionService(BlueprintContext context, IPrincipal user, IAutomationClientFactory clients,
            AutomationRunRegistry registry, TimeProvider time, ILogger<MselExecutionService> logger)
        {
            _context = context;
            _user = user as ClaimsPrincipal;
            _clients = clients;
            _registry = registry;
            _time = time;
            _logger = logger;
        }

        private DateTime Now => _time.GetUtcNow().UtcDateTime;
        private Guid UserId => _user.GetId();

        public async Task<MselExecution> GetAsync(Guid mselId, bool hasViewMsels, CancellationToken ct)
        {
            var msel = await _context.Msels.AsNoTracking().SingleOrDefaultAsync(m => m.Id == mselId, ct)
                ?? throw new EntityNotFoundException<MselEntity>($"MSEL not found {mselId}.");
            if (!hasViewMsels && !await MselFacilitatorViewRequirement.IsMet(UserId, mselId, _context))
                throw new ForbiddenException();
            return await ExecutionAsync(msel, ct);
        }

        public Task<MselAdvanceResult> StartMoveAsync(Guid mselId, int moveNumber, MselAdvanceForm form, bool hasEditMsels, CancellationToken ct) =>
            AdvanceAsync(mselId, moveNumber, 0, true, form, hasEditMsels, ct);

        public Task<MselAdvanceResult> StartGroupAsync(Guid mselId, int moveNumber, int groupNumber, MselAdvanceForm form, bool hasEditMsels, CancellationToken ct) =>
            AdvanceAsync(mselId, moveNumber, groupNumber, false, form, hasEditMsels, ct);

        public async Task<MselExecution> SetPausedAsync(Guid mselId, bool paused, bool hasEditMsels, CancellationToken ct)
        {
            var msel = await RequireFacilitatorOfDeployedAsync(mselId, hasEditMsels, ct);
            msel.AutomationPaused = paused;
            msel.ModifiedBy = UserId;
            await _context.SaveChangesAsync(ct);
            return await ExecutionAsync(msel, ct);
        }

        private async Task<MselAdvanceResult> AdvanceAsync(Guid mselId, int moveNumber, int groupNumber, bool isMove,
            MselAdvanceForm form, bool hasEditMsels, CancellationToken ct)
        {
            var msel = await RequireFacilitatorOfDeployedAsync(mselId, hasEditMsels, ct);
            var events = await _context.ScenarioEvents.AsNoTracking().Where(e => e.MselId == mselId).ToListAsync(ct);
            var moves = await _context.Moves.AsNoTracking().Where(m => m.MselId == mselId).ToListAsync(ct);
            if (moves.Count > 0 ? !moves.Any(m => m.MoveNumber == moveNumber) : moveNumber != 0)
                throw new BadRequestException($"The MSEL has no move {moveNumber}.");
            var positions = MselTimeline.Positions(events, moves);
            if (!isMove && groupNumber != 0 && !positions.Values.Any(p => p[0] == moveNumber && p[1] == groupNumber))
                throw new BadRequestException($"Move {moveNumber} has no group {groupNumber}.");

            // the manual tasks to run must be the MSEL's own, enabled, and run by hand
            var taskIds = (form?.RunTaskIds ?? new()).Distinct().ToList();
            var tasks = await _context.AutomationTasks.AsNoTracking().Where(t => taskIds.Contains(t.Id)).ToListAsync(ct);
            var refused = taskIds.Where(id =>
            {
                var task = tasks.SingleOrDefault(t => t.Id == id);
                return task == null || task.MselId != mselId || !task.Enabled || task.TriggerCondition != AutomationTrigger.Manual;
            }).ToList();
            if (refused.Count > 0)
                throw new BadRequestException($"Task {refused[0]} is not an enabled manual task of this MSEL.");

            // move the integrations the MSEL uses, as Blueprint's service account
            var messages = new List<string>();
            var succeeded = true;
            if (isMove && msel.UseCite && msel.CiteEvaluationId != null)
            {
                try
                {
                    var cite = await _clients.CiteAsync(ct);
                    var evaluation = await cite.SetEvaluationCurrentMoveAsync(msel.CiteEvaluationId.Value, moveNumber, ct);
                    messages.Add($"The CITE evaluation is at move {evaluation.CurrentMoveNumber}.");
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "Moving the CITE evaluation of MSEL {MselId} to move {Move} failed", mselId, moveNumber);
                    messages.Add($"The CITE evaluation could not be moved: {ex.Message}");
                    succeeded = false;
                }
            }
            if (msel.UseGallery && msel.GalleryExhibitId != null)
            {
                try
                {
                    var gallery = await _clients.GalleryAsync(ct);
                    var exhibit = await gallery.SetExhibitMoveAndInjectAsync(msel.GalleryExhibitId.Value, moveNumber, groupNumber, ct);
                    messages.Add($"The Gallery exhibit is at move {exhibit.CurrentMove}, inject {exhibit.CurrentInject}.");
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "Moving the Gallery exhibit of MSEL {MselId} to {Move}/{Group} failed", mselId, moveNumber, groupNumber);
                    messages.Add($"The Gallery exhibit could not be moved: {ex.Message}");
                    succeeded = false;
                }
            }
            var label = isMove ? $"Start Move {moveNumber}" : $"Start Move {moveNumber} Group {groupNumber}";
            if (messages.Count == 0)
                messages.Add("The MSEL uses no integration that follows its moves.");

            // the facilitator's position, and a record of the start in the run history
            msel.CurrentMoveNumber = moveNumber;
            msel.CurrentGroupNumber = groupNumber;
            msel.CurrentGroupStartedAt = Now;
            msel.ModifiedBy = UserId;
            var record = new AutomationRunEntity
            {
                Id = Guid.NewGuid(),
                MselId = mselId,
                TaskName = label,
                Action = isMove ? AutomationAction.cite_move_set : AutomationAction.gallery_move_inject_set,
                Kind = AutomationRunKind.Exercise,
                Source = AutomationRunSource.GroupStart,
                DeploymentId = msel.DeploymentId,
                PlayerViewId = msel.PlayerViewId,
                RequestedBy = UserId,
                TriggerChain = false,
                Status = succeeded ? AutomationStatus.Succeeded : AutomationStatus.Failed,
                StatusDetail = string.Join(" ", messages),
                CurrentIteration = 1,
                DueAt = Now,
                StartedAt = Now,
                EndedAt = Now,
                CreatedBy = UserId,
            };
            _context.AutomationRuns.Add(record);

            // the chosen manual tasks, one after another, in the order given; a task that is already running, or runs once
            // per deployment and has succeeded in this one, is left out, as a run by hand would refuse it. The tasks are
            // held while this is decided, as every path that runs them holds them.
            AutomationRunEntity first = null;
            await using (var transaction = await _context.Database.BeginTransactionAsync(ct))
            {
                await AutomationRuns.LockTasksAsync(_context, taskIds, ct);
                // the MSEL's position changes in this transaction
                if (!await AutomationRuns.HoldDeploymentAsync(_context, mselId, msel.DeploymentId, ct, forChange: true))
                    throw new ConflictException("The MSEL is not deployed.");
                var ordered = new List<AutomationTaskEntity>();
                foreach (var task in taskIds.Select(id => tasks.Single(t => t.Id == id)))
                {
                    var why = await AutomationRuns.WhyNotRunAsync(_context, task, msel.DeploymentId, ct);
                    if (why != null)
                        messages.Add($"{task.Name} {why}, so it was not run again.");
                    else
                        ordered.Add(task);
                }
                record.StatusDetail = string.Join(" ", messages);
                if (ordered.Count > 0)
                {
                    first = AutomationRuns.New(ordered[0], mselId, AutomationRunKind.Exercise, AutomationRunSource.GroupStart,
                        Now.AddSeconds(Math.Max(0, ordered[0].DelaySeconds)), msel.DeploymentId, msel.PlayerViewId, UserId,
                        parentRunId: record.Id, triggerChain: true, followOnTaskIds: ordered.Skip(1).Select(t => t.Id));
                    _context.AutomationRuns.Add(first);
                }
                await _context.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
            }
            if (first != null)
                _registry.Wake();

            return new MselAdvanceResult
            {
                Execution = await ExecutionAsync(msel, ct),
                Messages = messages,
                Succeeded = succeeded,
                FirstRun = first?.ToAutomationRun(),
            };
        }

        private async Task<MselEntity> RequireFacilitatorOfDeployedAsync(Guid mselId, bool hasEditMsels, CancellationToken ct)
        {
            var msel = await _context.Msels.SingleOrDefaultAsync(m => m.Id == mselId, ct)
                ?? throw new EntityNotFoundException<MselEntity>($"MSEL not found {mselId}.");
            if (!hasEditMsels && !await MselFacilitatorRequirement.IsMet(UserId, mselId, _context))
                throw new ForbiddenException();
            if (msel.Status != MselItemStatus.Deployed || msel.DeploymentId == null)
                throw new ConflictException("The MSEL is not deployed.");
            return msel;
        }

        private async Task<MselExecution> ExecutionAsync(MselEntity msel, CancellationToken ct)
        {
            var events = await _context.ScenarioEvents.AsNoTracking().Where(e => e.MselId == msel.Id).ToListAsync(ct);
            var moves = await _context.Moves.AsNoTracking().Where(m => m.MselId == msel.Id).ToListAsync(ct);
            return new MselExecution
            {
                MselId = msel.Id,
                Status = msel.Status,
                DeploymentId = msel.DeploymentId,
                StartTime = msel.StartTime,
                CurrentMoveNumber = msel.CurrentMoveNumber,
                CurrentGroupNumber = msel.CurrentGroupNumber,
                CurrentGroupStartedAt = msel.CurrentGroupStartedAt,
                AutomationPaused = msel.AutomationPaused,
                Positions = MselTimeline.Positions(events, moves)
                    .Select(p => new ScenarioEventPosition { ScenarioEventId = p.Key, MoveNumber = p.Value[0], GroupNumber = p.Value[1] })
                    .ToList(),
            };
        }
    }
}
