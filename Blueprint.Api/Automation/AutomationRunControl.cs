// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Blueprint.Api.Data;
using Blueprint.Api.Data.Enumerations;
using Microsoft.EntityFrameworkCore;

namespace Blueprint.Api.Automation
{
    /// <summary>Stops runs: one a facilitator cancels, or every exercise run of a MSEL whose deployment ends.</summary>
    public interface IAutomationRunControl
    {
        /// <summary>
        /// Asks a run to stop. One that has not started ends cancelled now; one that is running stops as soon as the
        /// instance working it hears, at once on this instance. False when the run has already ended.
        /// </summary>
        Task<bool> CancelAsync(Guid runId, CancellationToken ct);
        /// <summary>Cancels every exercise run of the MSEL that has not ended.</summary>
        Task<int> CancelAllForMselAsync(Guid mselId, string reason, CancellationToken ct);
    }

    public class AutomationRunControl : IAutomationRunControl
    {
        private readonly BlueprintContext _db;
        private readonly AutomationRunRegistry _registry;
        private readonly TimeProvider _time;

        public AutomationRunControl(BlueprintContext db, AutomationRunRegistry registry, TimeProvider time)
        {
            _db = db;
            _registry = registry;
            _time = time;
        }

        public async Task<bool> CancelAsync(Guid runId, CancellationToken ct)
        {
            var run = await _db.AutomationRuns.SingleOrDefaultAsync(r => r.Id == runId, ct);
            if (run == null || AutomationRuns.IsFinal(run.Status))
                return false;
            Cancel(run, "Cancelled.");
            await _db.SaveChangesAsync(ct);
            _registry.TryCancel(runId);
            return true;
        }

        public Task<int> CancelAllForMselAsync(Guid mselId, string reason, CancellationToken ct) =>
            CancelAllAsync(_db, _registry, mselId, reason, _time.GetUtcNow().UtcDateTime, ct);

        /// <summary>
        /// Cancels every exercise run of the MSEL that has not ended, over any context: the integration paths call it as
        /// they end a deployment. <paramref name="registry"/> stops this instance's own runs at once, when there is one.
        /// </summary>
        public static async Task<int> CancelAllAsync(BlueprintContext db, AutomationRunRegistry registry, Guid mselId, string reason, DateTime now, CancellationToken ct)
        {
            var open = new[] { AutomationStatus.Pending, AutomationStatus.Queued, AutomationStatus.Running };
            var runs = await db.AutomationRuns
                .Where(r => r.MselId == mselId && r.Kind == AutomationRunKind.Exercise && open.Contains(r.Status))
                .ToListAsync(ct);
            foreach (var run in runs)
                Cancel(run, reason, now);
            if (runs.Count > 0)
                await db.SaveChangesAsync(ct);
            foreach (var run in runs)
                registry?.TryCancel(run.Id);
            return runs.Count;
        }

        private void Cancel(Data.Models.AutomationRunEntity run, string reason) => Cancel(run, reason, _time.GetUtcNow().UtcDateTime);

        /// <summary>A run not yet working ends here; a working one is flagged, and its worker ends it.</summary>
        private static void Cancel(Data.Models.AutomationRunEntity run, string reason, DateTime now)
        {
            run.CancelRequested = true;
            if (run.Status == AutomationStatus.Pending)
            {
                run.Status = AutomationStatus.Cancelled;
                run.EndedAt = now;
                run.StatusDetail = reason;
            }
        }
    }
}
