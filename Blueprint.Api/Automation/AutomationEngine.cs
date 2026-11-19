// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Blueprint.Api.Data;
using Blueprint.Api.Data.Enumerations;
using Blueprint.Api.Data.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Blueprint.Api.Automation
{
    /// <summary>
    /// What this API instance is working on: the runs it holds, by id, with the token that cancels each, and a signal
    /// that wakes the dispatcher early when there is new work.
    /// </summary>
    public class AutomationRunRegistry
    {
        private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _running = new();
        private readonly SemaphoreSlim _wake = new(0, int.MaxValue);

        /// <summary>This instance's name on the runs it holds.</summary>
        public string InstanceId { get; } = $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}";

        public CancellationTokenSource Register(Guid runId, CancellationToken stopping)
        {
            var cts = CancellationTokenSource.CreateLinkedTokenSource(stopping);
            _running[runId] = cts;
            return cts;
        }

        public void Unregister(Guid runId) => _running.TryRemove(runId, out _);

        /// <summary>Cancels a run this instance is working; false when it is not working it.</summary>
        public bool TryCancel(Guid runId)
        {
            if (!_running.TryGetValue(runId, out var cts))
                return false;
            try { cts.Cancel(); } catch (ObjectDisposedException) { }
            return true;
        }

        public void Wake() => _wake.Release();

        /// <summary>Waits for <paramref name="interval"/> or a <see cref="Wake"/>, whichever is first.</summary>
        public async Task WaitAsync(TimeSpan interval, TimeProvider time, CancellationToken ct)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(interval);
            try
            {
                await _wake.WaitAsync(timeout.Token);
                // a burst of wakes needs one tick
                while (_wake.CurrentCount > 0 && _wake.Wait(0)) { }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
            }
        }
    }

    /// <summary>
    /// The scheduling half of the engine, one database pass at a time: makes the runs tasks at their event time are due,
    /// takes over runs an instance that stopped was holding, claims runs that are due for this instance, and keeps the
    /// test history short. Every change of a run's status that a client should see is a tracked save, so it is broadcast.
    /// </summary>
    public class AutomationScheduler
    {
        private readonly BlueprintContext _db;
        private readonly AutomationRunRegistry _registry;
        private readonly IOptionsMonitor<AutomationOptions> _options;
        private readonly TimeProvider _time;
        private readonly ILogger<AutomationScheduler> _logger;

        public AutomationScheduler(BlueprintContext db, AutomationRunRegistry registry, IOptionsMonitor<AutomationOptions> options,
            TimeProvider time, ILogger<AutomationScheduler> logger)
        {
            _db = db;
            _registry = registry;
            _options = options;
            _time = time;
            _logger = logger;
        }

        private DateTime Now => _time.GetUtcNow().UtcDateTime;

        /// <summary>
        /// Makes a run for each enabled At Event Time task of a deployed, unpaused MSEL whose time has come and that has no
        /// scheduled run in this deployment. One that is later than the grace period is recorded as skipped instead.
        /// </summary>
        public async Task<int> ScheduleAsync(CancellationToken ct)
        {
            var now = Now;
            var due = await _db.AutomationTasks
                .AsNoTracking()
                .Where(t => t.Enabled && t.TriggerCondition == AutomationTrigger.AtEventTime)
                .Where(t => t.Msel.Status == MselItemStatus.Deployed && !t.Msel.AutomationPaused && t.Msel.DeploymentId != null)
                .Where(t => !_db.AutomationRuns.Any(r => r.AutomationTaskId == t.Id && r.Source == AutomationRunSource.Schedule && r.DeploymentId == t.Msel.DeploymentId))
                .Select(t => new { Task = t, t.Msel.StartTime, t.Msel.DeploymentId, t.Msel.PlayerViewId, t.ScenarioEvent.DeltaSeconds })
                .ToListAsync(ct);
            var grace = TimeSpan.FromSeconds(Math.Max(0, _options.CurrentValue.MissedScheduleGraceSeconds));
            var made = 0;
            foreach (var item in due)
            {
                var at = DateTime.SpecifyKind(item.StartTime, DateTimeKind.Utc).AddSeconds(item.DeltaSeconds);
                if (at > now)
                    continue;
                var run = AutomationRuns.New(item.Task, item.Task.MselId, AutomationRunKind.Exercise, AutomationRunSource.Schedule,
                    at.AddSeconds(Math.Max(0, item.Task.DelaySeconds)), item.DeploymentId, item.PlayerViewId, null);
                if (now - at > grace)
                {
                    run.Status = AutomationStatus.Skipped;
                    run.EndedAt = now;
                    run.StatusDetail = $"Its time, {at:u}, had passed by {Math.Floor((now - at).TotalSeconds)} s when Blueprint could run it.";
                }
                _db.AutomationRuns.Add(run);
                try
                {
                    await _db.SaveChangesAsync(ct);
                    made++;
                }
                catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
                {
                    // another instance scheduled it first
                    _db.Entry(run).State = EntityState.Detached;
                }
            }
            return made;
        }

        /// <summary>
        /// Takes back runs an instance held past its lease. A run that was only queued is due again. A run that was
        /// running is an error, and is not run again: its action may have taken effect, and powering a VM off or
        /// sending an email twice is not safe.
        /// </summary>
        public async Task<int> RecoverAsync(CancellationToken ct)
        {
            var now = Now;
            var requeued = await _db.AutomationRuns
                .Where(r => r.Status == AutomationStatus.Queued && r.LeaseUntil < now)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(r => r.Status, AutomationStatus.Pending)
                    .SetProperty(r => r.LeaseOwner, (string)null)
                    .SetProperty(r => r.LeaseUntil, (DateTime?)null), ct);
            var stale = await _db.AutomationRuns
                .Include(r => r.Results)
                .Where(r => r.Status == AutomationStatus.Running && r.LeaseUntil < now)
                .ToListAsync(ct);
            foreach (var run in stale)
            {
                const string detail = "Blueprint stopped while this task ran, so whether it took effect is not known.";
                foreach (var result in run.Results.Where(r => !AutomationRuns.IsFinal(r.Status)))
                {
                    result.Status = AutomationStatus.Error;
                    result.ActualOutput = detail;
                    result.StatusAt = now;
                }
                run.Status = AutomationStatus.Error;
                run.StatusDetail = detail;
                run.EndedAt = now;
                run.LeaseOwner = null;
                run.LeaseUntil = null;
            }
            if (stale.Count > 0)
                await _db.SaveChangesAsync(ct);
            return requeued + stale.Count;
        }

        /// <summary>Ends the pending runs a cancel was asked for, and claims up to <paramref name="capacity"/> due runs for this instance.</summary>
        public async Task<IReadOnlyList<Guid>> ClaimAsync(int capacity, CancellationToken ct)
        {
            var now = Now;
            var cancelled = await _db.AutomationRuns
                .Where(r => r.Status == AutomationStatus.Pending && r.CancelRequested)
                .ToListAsync(ct);
            foreach (var run in cancelled)
            {
                run.Status = AutomationStatus.Cancelled;
                run.EndedAt = now;
                run.StatusDetail ??= "Cancelled before it started.";
            }
            if (cancelled.Count > 0)
                await _db.SaveChangesAsync(ct);
            if (capacity <= 0)
                return Array.Empty<Guid>();

            var candidates = await _db.AutomationRuns
                .Where(r => r.Status == AutomationStatus.Pending && r.DueAt <= now && !r.CancelRequested)
                .OrderBy(r => r.DueAt)
                .Select(r => r.Id)
                .Take(capacity)
                .ToListAsync(ct);
            var leaseUntil = now.AddSeconds(Math.Max(5, _options.CurrentValue.LeaseSeconds));
            var claimed = new List<Guid>();
            foreach (var id in candidates)
            {
                var owner = _registry.InstanceId;
                var won = await _db.AutomationRuns
                    .Where(r => r.Id == id && r.Status == AutomationStatus.Pending)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(r => r.Status, AutomationStatus.Queued)
                        .SetProperty(r => r.LeaseOwner, owner)
                        .SetProperty(r => r.LeaseUntil, leaseUntil), ct);
                if (won == 1)
                    claimed.Add(id);
            }
            return claimed;
        }

        /// <summary>
        /// Keeps each task's newest test runs, as many as Automation:TestRunsKeptPerTask says, and as many tests of drafts
        /// per scenario event.
        /// </summary>
        public async Task<int> RetainAsync(CancellationToken ct)
        {
            var keep = Math.Max(1, _options.CurrentValue.TestRunsKeptPerTask);
            var tasks = await _db.AutomationRuns
                .Where(r => r.Kind == AutomationRunKind.Test && r.AutomationTaskId != null)
                .GroupBy(r => r.AutomationTaskId)
                .Where(g => g.Count() > keep)
                .Select(g => g.Key)
                .ToListAsync(ct);
            var removed = 0;
            foreach (var taskId in tasks)
            {
                var old = await _db.AutomationRuns
                    .Where(r => r.Kind == AutomationRunKind.Test && r.AutomationTaskId == taskId)
                    .OrderByDescending(r => r.DateCreated)
                    .Skip(keep)
                    .Where(r => r.Status != AutomationStatus.Running && r.Status != AutomationStatus.Queued)
                    .ToListAsync(ct);
                _db.AutomationRuns.RemoveRange(old);
                removed += old.Count;
            }
            // tests of a task that was not saved yet are kept per scenario event
            var events = await _db.AutomationRuns
                .Where(r => r.Kind == AutomationRunKind.Test && r.AutomationTaskId == null && r.ScenarioEventId != null)
                .GroupBy(r => r.ScenarioEventId)
                .Where(g => g.Count() > keep)
                .Select(g => g.Key)
                .ToListAsync(ct);
            foreach (var eventId in events)
            {
                var old = await _db.AutomationRuns
                    .Where(r => r.Kind == AutomationRunKind.Test && r.AutomationTaskId == null && r.ScenarioEventId == eventId)
                    .OrderByDescending(r => r.DateCreated)
                    .Skip(keep)
                    .Where(r => r.Status != AutomationStatus.Running && r.Status != AutomationStatus.Queued)
                    .ToListAsync(ct);
                _db.AutomationRuns.RemoveRange(old);
                removed += old.Count;
            }
            if (removed > 0)
                await _db.SaveChangesAsync(ct);
            return removed;
        }
    }

    /// <summary>
    /// Runs automation tasks: one tick every Automation:SchedulerIntervalSeconds, or as soon as a run is made, with up to
    /// Automation:MaxConcurrentRuns runs at once on this instance. The database is the source of truth: a run is claimed
    /// with a conditional update and a lease, so several instances can share the work, and a run whose instance stopped
    /// is taken over when its lease runs out.
    /// </summary>
    public class AutomationDispatcher : BackgroundService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly AutomationRunRegistry _registry;
        private readonly IOptionsMonitor<AutomationOptions> _options;
        private readonly TimeProvider _time;
        private readonly ILogger<AutomationDispatcher> _logger;
        private readonly Channel<Guid> _work = Channel.CreateUnbounded<Guid>();
        private int _inFlight;
        private DateTime _nextRetention;

        public AutomationDispatcher(IServiceScopeFactory scopeFactory, AutomationRunRegistry registry, IOptionsMonitor<AutomationOptions> options,
            TimeProvider time, ILogger<AutomationDispatcher> logger)
        {
            _scopeFactory = scopeFactory;
            _registry = registry;
            _options = options;
            _time = time;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (!_options.CurrentValue.WorkerEnabled)
            {
                _logger.LogInformation("Automation:WorkerEnabled is false, so this instance runs no automation tasks.");
                return;
            }
            var workers = Enumerable.Range(0, Math.Max(1, _options.CurrentValue.MaxConcurrentRuns))
                .Select(_ => Task.Run(() => WorkAsync(stoppingToken), stoppingToken))
                .ToList();
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await TickAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "An automation tick failed");
                }
                await _registry.WaitAsync(TimeSpan.FromSeconds(Math.Max(1, _options.CurrentValue.SchedulerIntervalSeconds)), _time, stoppingToken);
            }
            _work.Writer.TryComplete();
            await Task.WhenAll(workers.Select(w => w.ContinueWith(_ => { }, TaskScheduler.Default)));
        }

        private async Task TickAsync(CancellationToken ct)
        {
            using var scope = _scopeFactory.CreateScope();
            var scheduler = scope.ServiceProvider.GetRequiredService<AutomationScheduler>();
            await scheduler.ScheduleAsync(ct);
            await scheduler.RecoverAsync(ct);
            var capacity = Math.Max(1, _options.CurrentValue.MaxConcurrentRuns) - Volatile.Read(ref _inFlight) - _work.Reader.Count;
            foreach (var id in await scheduler.ClaimAsync(capacity, ct))
                await _work.Writer.WriteAsync(id, ct);
            if (_time.GetUtcNow().UtcDateTime >= _nextRetention)
            {
                _nextRetention = _time.GetUtcNow().UtcDateTime.AddMinutes(1);
                await scheduler.RetainAsync(ct);
            }
        }

        private async Task WorkAsync(CancellationToken ct)
        {
            await foreach (var runId in _work.Reader.ReadAllAsync(ct))
            {
                Interlocked.Increment(ref _inFlight);
                try
                {
                    using var scope = _scopeFactory.CreateScope();
                    await scope.ServiceProvider.GetRequiredService<IAutomationRunExecutor>().ExecuteIterationAsync(runId, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Automation run {RunId} failed to run", runId);
                }
                finally
                {
                    Interlocked.Decrement(ref _inFlight);
                    _registry.Wake();
                }
            }
        }
    }
}
