// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Blueprint.Api.Data;
using Blueprint.Api.Data.Enumerations;
using Blueprint.Api.Data.Models;
using Blueprint.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Blueprint.Api.Automation
{
    public interface IAutomationRunExecutor
    {
        /// <summary>Runs one iteration of a run this instance has claimed, then queues its next iteration or ends it.</summary>
        Task ExecuteIterationAsync(Guid runId, CancellationToken stopping);
    }

    /// <summary>
    /// Runs one iteration of a run: resolves its VMs, performs its action on each (at most
    /// Automation:MaxConcurrentVmOperationsPerRun at once), each with its own expiration, and records a result per VM.
    /// It is the only writer of its run while it holds it, so a status it sets is the status the run has; a cancel
    /// asked for elsewhere reaches it through the database, read on every lease renewal.
    /// </summary>
    public class AutomationRunExecutor : IAutomationRunExecutor
    {
        private readonly BlueprintContext _db;
        private readonly IAutomationActionCatalog _catalog;
        private readonly IAutomationActionRunner _runner;
        private readonly IAutomationVmResolver _vms;
        private readonly IAutomationSecretProtector _secrets;
        private readonly AutomationRunRegistry _registry;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly IOptionsMonitor<AutomationOptions> _options;
        private readonly TimeProvider _time;
        private readonly ILogger<AutomationRunExecutor> _logger;
        private readonly SemaphoreSlim _save = new(1, 1);

        public AutomationRunExecutor(
            BlueprintContext db,
            IAutomationActionCatalog catalog,
            IAutomationActionRunner runner,
            IAutomationVmResolver vms,
            IAutomationSecretProtector secrets,
            AutomationRunRegistry registry,
            IServiceScopeFactory scopeFactory,
            IOptionsMonitor<AutomationOptions> options,
            TimeProvider time,
            ILogger<AutomationRunExecutor> logger)
        {
            _db = db;
            _catalog = catalog;
            _runner = runner;
            _vms = vms;
            _secrets = secrets;
            _registry = registry;
            _scopeFactory = scopeFactory;
            _options = options;
            _time = time;
            _logger = logger;
        }

        private DateTime Now => _time.GetUtcNow().UtcDateTime;
        private AutomationOptions Options => _options.CurrentValue;

        public async Task ExecuteIterationAsync(Guid runId, CancellationToken stopping)
        {
            var owner = _registry.InstanceId;
            var run = await _db.AutomationRuns.SingleOrDefaultAsync(r => r.Id == runId && r.Status == AutomationStatus.Queued && r.LeaseOwner == owner, stopping);
            if (run == null)
                return;
            var task = AutomationRuns.Snapshot(run);
            if (run.CancelRequested)
            {
                await FinishAsync(run, task, AutomationStatus.Cancelled, "Cancelled before it started.", stopping);
                return;
            }
            if (task == null)
            {
                await FinishAsync(run, null, AutomationStatus.Error, "The run has no task to run.", stopping);
                return;
            }
            var msel = await _db.Msels.AsNoTracking().SingleOrDefaultAsync(m => m.Id == run.MselId, stopping);
            if (msel == null)
                return;
            // an exercise run of a deployment that has ended, or is being taken down, does not act on what it leaves
            if (run.Kind == AutomationRunKind.Exercise && (msel.Status != MselItemStatus.Deployed || msel.DeploymentId != run.DeploymentId))
            {
                await FinishAsync(run, task, AutomationStatus.Cancelled, "The exercise's deployment ended before it started.", stopping);
                return;
            }

            // started only while this instance still holds the run: one that waited past its lease may have been taken
            // back and claimed by another, and one whose cancel was asked for meanwhile ends here
            var cancelledMeanwhile = false;
            var started = await WhileOwnedAsync(run, r =>
            {
                cancelledMeanwhile = r.Status == AutomationStatus.Queued && r.CancelRequested;
                return r.Status == AutomationStatus.Queued && !r.CancelRequested;
            }, async () =>
            {
                run.Status = AutomationStatus.Running;
                run.StartedAt ??= Now;
                run.CurrentIteration += 1;
                run.LeaseUntil = Now.AddSeconds(LeaseSeconds);
                run.StatusDetail = null;
                await _db.SaveChangesAsync(stopping);
            }, stopping);
            if (cancelledMeanwhile)
            {
                await FinishAsync(run, task, AutomationStatus.Cancelled, "Cancelled before it started.", stopping);
                return;
            }
            if (!started)
                return;

            using var runCts = _registry.Register(run.Id, stopping);
            using var heartbeatStop = new CancellationTokenSource();
            var heartbeat = HeartbeatAsync(run.Id, owner, runCts, heartbeatStop.Token);
            List<AutomationResultEntity> results;
            bool leaseLost;
            try
            {
                results = await ExecuteResultsAsync(run, task, msel, runCts, stopping);
            }
            finally
            {
                heartbeatStop.Cancel();
                leaseLost = await heartbeat;
                _registry.Unregister(run.Id);
            }
            if (leaseLost)
            {
                // another instance has the run now, or has recorded it as interrupted: this one writes nothing more
                _logger.LogWarning("Automation run {RunId} was taken back while this instance ran it; its outcome here is dropped", run.Id);
                return;
            }

            var status = AutomationRuns.Overall(results.Select(r => r.Status));
            var cancelAsked = runCts.IsCancellationRequested && !stopping.IsCancellationRequested ||
                await _db.AutomationRuns.AnyAsync(r => r.Id == run.Id && r.CancelRequested, CancellationToken.None);
            if (cancelAsked)
            {
                await FinishAsync(run, task, AutomationStatus.Cancelled, "Cancelled.", CancellationToken.None);
                return;
            }
            if (stopping.IsCancellationRequested)
                return;     // the lease runs out, and the next instance records the run as interrupted
            if (AutomationRuns.RunsAgain(task, run.CurrentIteration, status))
            {
                var iteration = run.CurrentIteration;
                if (await WhileOwnedAsync(run, r => r.Status == AutomationStatus.Running, async () =>
                    {
                        run.Status = AutomationStatus.Pending;
                        run.DueAt = Now.AddSeconds(Math.Max(0, task.IntervalSeconds));
                        run.LeaseOwner = null;
                        run.LeaseUntil = null;
                        run.StatusDetail = $"Iteration {iteration} of {task.Iterations}: {status}.";
                        await _db.SaveChangesAsync(CancellationToken.None);
                    }, CancellationToken.None))
                    _registry.Wake();
                return;
            }
            await FinishAsync(run, task, status, results.Count == 1 && status == AutomationStatus.Error ? Truncate(results[0].ActualOutput, 500) : null,
                CancellationToken.None);
        }

        private int LeaseSeconds => Math.Max(5, Options.LeaseSeconds);

        /// <summary>The run's results for this iteration: one per VM its mask matched, or one for an action without VMs.</summary>
        private async Task<List<AutomationResultEntity>> ExecuteResultsAsync(AutomationRunEntity run, AutomationTaskSnapshot task, MselEntity msel,
            CancellationTokenSource runCts, CancellationToken stopping)
        {
            var definition = _catalog.Get(task.Action);
            var expiration = task.ExpirationSeconds > 0
                ? Math.Min(task.ExpirationSeconds, Math.Max(1, Options.MaxExpirationSeconds))
                : Math.Max(1, Options.DefaultExpirationSeconds);
            var results = new List<AutomationResultEntity>();
            AutomationResultEntity NewResult(AutomationVm vm) => new()
            {
                Id = Guid.NewGuid(),
                RunId = run.Id,
                Iteration = run.CurrentIteration,
                VmId = vm?.Id,
                VmName = vm?.Name,
                Status = AutomationStatus.Running,
                ExpectedOutput = task.ExpectedOutput,
                ExpirationSeconds = expiration,
                SentAt = Now,
                CreatedBy = run.RequestedBy ?? Guid.Empty,
            };

            // the VMs, when the action needs them
            string refusal = null;
            var vms = new List<AutomationVm>();
            if (definition == null)
                refusal = $"{task.Action} is not an automation action.";
            else if (definition.RequiresVm)
            {
                if (run.PlayerViewId == null)
                    refusal = run.Kind == AutomationRunKind.Test ? "Choose a Player view to test against." : "The MSEL has no Player view; push it to Player first.";
                else if (string.IsNullOrWhiteSpace(task.VmMask))
                    refusal = "The task runs on VMs, and has no VM mask.";
                else
                {
                    try
                    {
                        vms = (await _vms.ResolveAsync(run.PlayerViewId.Value, task.VmMask, runCts.Token)).ToList();
                        if (vms.Count == 0)
                            refusal = $"No VM of the Player view matches the mask \"{task.VmMask}\".";
                        else if (vms.Count > Math.Max(1, Options.MaxVmsPerTask))
                            refusal = $"The mask \"{task.VmMask}\" matches {vms.Count} VMs; a task may run on at most {Options.MaxVmsPerTask}.";
                    }
                    catch (OperationCanceledException) when (runCts.IsCancellationRequested)
                    {
                        refusal = "Cancelled.";
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Listing the VMs of Player view {ViewId} failed", run.PlayerViewId);
                        refusal = $"Blueprint could not list the VMs of Player view {run.PlayerViewId}: {ex.Message}";
                    }
                }
            }
            if (refusal != null)
            {
                var result = NewResult(null);
                result.Status = runCts.IsCancellationRequested ? AutomationStatus.Cancelled : AutomationStatus.Error;
                result.ActualOutput = refusal;
                result.StatusAt = Now;
                _db.AutomationResults.Add(result);
                await SaveWhileRunningAsync(run);
                return new List<AutomationResultEntity> { result };
            }
            results.AddRange(definition.RequiresVm ? vms.Select(NewResult) : new[] { NewResult(null) });
            _db.AutomationResults.AddRange(results);
            await SaveWhileRunningAsync(run);

            // the parameters
            Dictionary<string, string> secrets;
            try
            {
                secrets = _secrets.Unprotect(task.SecretParameters);
            }
            catch (AutomationConfigurationException ex)
            {
                foreach (var result in results)
                    Record(result, AutomationStatus.Error, ex.Message);
                await SaveWhileRunningAsync(run);
                return results;
            }
            var substitutions = await SubstitutionsAsync(run, task, msel, CancellationToken.None);
            var secretKeys = _catalog.SecretKeys(task.Action);
            var dryRun = run.Kind == AutomationRunKind.Test && definition.IsDryRunWhenTested;

            await Parallel.ForEachAsync(results,
                new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Options.MaxConcurrentVmOperationsPerRun) },
                async (result, _) =>
                {
                    var bound = AutomationParameterBinder.Bind(task.ActionParameters, secrets, substitutions, result.VmId, result.VmName);
                    var input = AutomationParameterBinder.Describe(bound, secretKeys);
                    AutomationStatus status;
                    string output;
                    var context = new AutomationActionContext
                    {
                        MselId = msel.Id,
                        Action = task.Action,
                        Parameters = bound,
                        PlayerViewId = run.PlayerViewId,
                        CiteEvaluationId = msel.CiteEvaluationId,
                        GalleryExhibitId = msel.GalleryExhibitId,
                        VmId = result.VmId,
                        VmName = result.VmName,
                        IsTest = run.Kind == AutomationRunKind.Test,
                        DryRun = dryRun,
                    };
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(expiration), _time);
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(runCts.Token, timeout.Token);
                    try
                    {
                        var outcome = await _runner.ExecuteAsync(context, linked.Token);
                        var succeeded = outcome.Succeeded ?? Matches(outcome.Output, task.ExpectedOutput);
                        (status, output) = (succeeded ? AutomationStatus.Succeeded : AutomationStatus.Failed, outcome.Output);
                    }
                    catch (OperationCanceledException) when (runCts.IsCancellationRequested)
                    {
                        (status, output) = (AutomationStatus.Cancelled, "Cancelled.");
                    }
                    catch (OperationCanceledException) when (timeout.IsCancellationRequested)
                    {
                        (status, output) = (AutomationStatus.Expired, $"No answer within {expiration} s.");
                    }
                    catch (AutomationActionFailedException ex)
                    {
                        (status, output) = (AutomationStatus.Failed, ex.Message);
                    }
                    catch (AutomationConfigurationException ex)
                    {
                        (status, output) = (AutomationStatus.Error, ex.Message);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Automation task {TaskId} failed on {Vm}", task.TaskId, result.VmName ?? "no VM");
                        (status, output) = (AutomationStatus.Failed, ex.Message);
                    }
                    // the context and its tracked results are written by one thread at a time
                    await _save.WaitAsync(CancellationToken.None);
                    try
                    {
                        result.InputString = input;
                        Record(result, status, output);
                        await SaveWhileRunningAsync(run);
                    }
                    finally
                    {
                        _save.Release();
                    }
                });
            return results;
        }

        private void Record(AutomationResultEntity result, AutomationStatus status, string output)
        {
            var max = Math.Max(1024, Options.MaxOutputBytes);
            output ??= string.Empty;
            var truncated = Encoding.UTF8.GetByteCount(output) > max;
            result.ActualOutput = truncated ? TruncateBytes(output, max) : output;
            result.OutputTruncated = truncated;
            result.Status = status;
            result.StatusAt = Now;
        }

        /// <summary>Steamfitter's rule: the output contains the expected output, ignoring case; an empty expected output always matches.</summary>
        public static bool Matches(string output, string expected) =>
            string.IsNullOrEmpty(expected) || (output ?? string.Empty).Contains(expected, StringComparison.OrdinalIgnoreCase);

        private async Task<AutomationSubstitutions> SubstitutionsAsync(AutomationRunEntity run, AutomationTaskSnapshot task, MselEntity msel, CancellationToken ct)
        {
            int? move = null, group = null;
            var usesPosition = task.ActionParameters.Values.Any(v => v != null &&
                (v.Contains("{moveNumber}", StringComparison.OrdinalIgnoreCase) || v.Contains("{groupNumber}", StringComparison.OrdinalIgnoreCase)));
            if (usesPosition)
            {
                var events = await _db.ScenarioEvents.AsNoTracking().Where(e => e.MselId == msel.Id).ToListAsync(ct);
                var moves = await _db.Moves.AsNoTracking().Where(m => m.MselId == msel.Id).ToListAsync(ct);
                if (MselTimeline.Positions(events, moves).TryGetValue(task.ScenarioEventId, out var position))
                {
                    move = position[0];
                    group = position[1];
                }
            }
            return new AutomationSubstitutions(msel.Id, run.PlayerViewId, msel.CiteEvaluationId, msel.GalleryExhibitId, move, group);
        }

        /// <summary>Renews the lease while the run works, and cancels it when a cancel is asked for on another instance.</summary>
        /// <summary>
        /// Renews the run's lease while it works, and stops the work when a cancel is asked for. True when the lease was
        /// lost: the run was taken back from this instance, which must then write nothing more.
        /// </summary>
        private async Task<bool> HeartbeatAsync(Guid runId, string owner, CancellationTokenSource runCts, CancellationToken stop)
        {
            var every = TimeSpan.FromSeconds(Math.Max(1, LeaseSeconds / 3));
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(every, _time, stop);
                    using var scope = _scopeFactory.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<BlueprintContext>();
                    var leaseUntil = Now.AddSeconds(LeaseSeconds);
                    var renewed = await db.AutomationRuns.Where(r => r.Id == runId && r.LeaseOwner == owner && r.Status == AutomationStatus.Running)
                        .ExecuteUpdateAsync(s => s.SetProperty(r => r.LeaseUntil, leaseUntil), stop);
                    if (renewed == 0)
                    {
                        runCts.Cancel();
                        return true;
                    }
                    if (await db.AutomationRuns.AnyAsync(r => r.Id == runId && r.CancelRequested, stop))
                        runCts.Cancel();
                }
                catch (OperationCanceledException)
                {
                    return false;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Renewing the lease of automation run {RunId} failed", runId);
                }
            }
            return false;
        }

        /// <summary>
        /// Saves the run's results only while this instance still holds the running run: once a recovery has recorded it
        /// interrupted, or another instance has it, its results are theirs to write.
        /// </summary>
        private Task<bool> SaveWhileRunningAsync(AutomationRunEntity run) =>
            WhileOwnedAsync(run, r => r.Status == AutomationStatus.Running, () => _db.SaveChangesAsync(CancellationToken.None), CancellationToken.None);

        /// <summary>
        /// Runs <paramref name="write"/> in a transaction holding the run's row, after reading the run again, only while
        /// this instance still holds its lease and <paramref name="state"/> holds: a cancel, a recovery or another
        /// instance may have changed it since. False when it was not written.
        /// </summary>
        private async Task<bool> WhileOwnedAsync(AutomationRunEntity run, Func<AutomationRunEntity, bool> state, Func<Task> write, CancellationToken ct)
        {
            await using var transaction = await _db.Database.BeginTransactionAsync(ct);
            await AutomationRuns.LockRunsAsync(_db, new[] { run.Id }, ct);
            await _db.Entry(run).ReloadAsync(ct);
            if (run.LeaseOwner != _registry.InstanceId || !state(run))
                return false;
            await write();
            await transaction.CommitAsync(ct);
            return true;
        }

        /// <summary>Ends a run, and makes the runs that follow from it.</summary>
        private async Task FinishAsync(AutomationRunEntity run, AutomationTaskSnapshot task, AutomationStatus status, string detail, CancellationToken ct)
        {
            var next = new List<AutomationRunEntity>();
            var finished = await WhileOwnedAsync(run, r => !AutomationRuns.IsFinal(r.Status), async () =>
            {
                run.Status = status;
                run.StatusDetail = detail ?? run.StatusDetail;
                run.EndedAt = Now;
                run.LeaseOwner = null;
                run.LeaseUntil = null;
                next = await AutomationChainer.NextRunsAsync(_db, run, status, Now, ct);
                _db.AutomationRuns.AddRange(next);
                await _db.SaveChangesAsync(ct);
            }, ct);
            if (!finished)
            {
                _logger.LogWarning("Automation run {RunId} was taken back before this instance could end it", run.Id);
                return;
            }
            if (next.Any(r => !AutomationRuns.IsFinal(r.Status)))
                _registry.Wake();
            // the exercise's record, with the points the task earned; a record that cannot be made does not fail the run
            if (run.Kind == AutomationRunKind.Exercise && run.AutomationTaskId != null)
            {
                try
                {
                    using var scope = _scopeFactory.CreateScope();
                    await scope.ServiceProvider.GetRequiredService<Services.IXApiService>().AutomationRunEndedAsync(run.Id, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "The xAPI statement of automation run {RunId} could not be made", run.Id);
                }
            }
        }

        private static string Truncate(string s, int max) => s == null || s.Length <= max ? s : s.Substring(0, max) + "…";

        private static string TruncateBytes(string s, int maxBytes)
        {
            var encoder = Encoding.UTF8;
            var length = s.Length;
            while (length > 0 && encoder.GetByteCount(s.AsSpan(0, length)) > maxBytes)
                length = Math.Max(0, length - Math.Max(1, (encoder.GetByteCount(s.AsSpan(0, length)) - maxBytes) / 4));
            return s.Substring(0, length);
        }
    }

    /// <summary>The runs a finished run starts: the tasks it triggers, and the next of a group start's manual tasks.</summary>
    public static class AutomationChainer
    {
        public static async Task<List<AutomationRunEntity>> NextRunsAsync(BlueprintContext db, AutomationRunEntity run, AutomationStatus status, DateTime now, CancellationToken ct)
        {
            var next = new List<AutomationRunEntity>();
            if (status == AutomationStatus.Cancelled)
                return next;
            var msel = await db.Msels.AsNoTracking().SingleOrDefaultAsync(m => m.Id == run.MselId, ct);
            if (msel == null)
                return next;
            // an exercise run's chain stops when its deployment does, checked while holding it as every path that makes
            // a run does
            if (run.Kind == AutomationRunKind.Exercise && !await AutomationRuns.HoldDeploymentAsync(db, run.MselId, run.DeploymentId, ct))
                return next;

            var triggers = run.TriggerChain && run.AutomationTaskId != null ? AutomationRuns.TriggersFor(status) : Array.Empty<AutomationTrigger>();
            var children = triggers.Length == 0
                ? new List<AutomationTaskEntity>()
                : await db.AutomationTasks.AsNoTracking()
                    .Where(t => t.TriggerTaskId == run.AutomationTaskId && t.Enabled && triggers.Contains(t.TriggerCondition))
                    .OrderBy(t => t.DisplayOrder)
                    .ToListAsync(ct);
            var followOn = AutomationRuns.FollowOn(run).ToList();
            // An exercise run of a task is made as a run by hand is: not while one is going, and not again once a task
            // that runs only once has succeeded. The tasks are locked first, as every path that runs them locks them.
            if (run.Kind == AutomationRunKind.Exercise)
                await AutomationRuns.LockTasksAsync(db, children.Select(c => c.Id).Concat(followOn), ct);
            async Task<string> WhyNot(AutomationTaskEntity task) =>
                run.Kind == AutomationRunKind.Exercise ? await AutomationRuns.WhyNotRunAsync(db, task, run.DeploymentId, ct) : null;

            foreach (var child in children)
            {
                var why = await WhyNot(child);
                next.Add(why != null
                    ? AutomationRuns.Skipped(child, run, AutomationRunSource.Chain, why, now)
                    : AutomationRuns.New(child, run.MselId, run.Kind, AutomationRunSource.Chain,
                        now.AddSeconds(Math.Max(0, child.DelaySeconds)), run.DeploymentId, run.PlayerViewId, run.RequestedBy,
                        parentRunId: run.Id, triggerChain: true));
            }

            // a group start runs its chosen manual tasks one after another, whatever each one's outcome; one that may not
            // run is recorded as skipped, and the next one runs instead
            while (followOn.Count > 0)
            {
                var id = followOn[0];
                followOn.RemoveAt(0);
                var task = await db.AutomationTasks.AsNoTracking().SingleOrDefaultAsync(t => t.Id == id && t.Enabled, ct);
                if (task == null)
                    continue;
                var why = await WhyNot(task);
                if (why != null)
                {
                    next.Add(AutomationRuns.Skipped(task, run, AutomationRunSource.GroupStart, why, now));
                    continue;
                }
                next.Add(AutomationRuns.New(task, run.MselId, run.Kind, AutomationRunSource.GroupStart,
                    now.AddSeconds(Math.Max(0, task.DelaySeconds)), run.DeploymentId, run.PlayerViewId, run.RequestedBy,
                    parentRunId: run.Id, triggerChain: true, followOnTaskIds: followOn));
                break;
            }
            return next;
        }
    }
}
