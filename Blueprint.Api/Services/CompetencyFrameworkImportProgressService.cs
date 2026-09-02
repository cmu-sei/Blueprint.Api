// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using System;
using System.Collections.Concurrent;
using System.Linq;
using Blueprint.Api.ViewModels;

namespace Blueprint.Api.Services
{
    public interface ICompetencyFrameworkImportProgressService
    {
        /// <summary>Registers a new import and returns its initial status.</summary>
        CompetencyFrameworkImportStatus Begin(Guid importId);

        /// <summary>
        /// Records the phase an import has reached. Pass total = 0 for a phase whose work
        /// cannot be counted; pass processed/total to drive a determinate bar within it.
        /// </summary>
        void ReportPhase(Guid importId, string phase, int phaseNumber, int phaseCount, int processed = 0, int total = 0);

        void Succeed(Guid importId, Guid frameworkId, string frameworkName);

        void Fail(Guid importId, string error);

        /// <summary>Returns the status of an import, or null if it is unknown or has expired.</summary>
        CompetencyFrameworkImportStatus Get(Guid importId);
    }

    /// <summary>
    /// In-memory import progress, held as a singleton so the polling GET can see the
    /// progress of an import running on a different request. Progress is deliberately
    /// not persisted: it is only useful while the import is in flight, and an import
    /// cannot survive a restart of the process running it either.
    /// </summary>
    public class CompetencyFrameworkImportProgressService : ICompetencyFrameworkImportProgressService
    {
        /// <summary>
        /// How long a finished import stays readable, so a client that polls slowly still
        /// sees the terminal state rather than a 404.
        /// </summary>
        private static readonly TimeSpan Retention = TimeSpan.FromMinutes(30);

        private readonly ConcurrentDictionary<Guid, CompetencyFrameworkImportStatus> _statuses = new();

        public CompetencyFrameworkImportStatus Begin(Guid importId)
        {
            Prune();
            var now = DateTime.UtcNow;
            var status = new CompetencyFrameworkImportStatus
            {
                Id = importId,
                State = CompetencyFrameworkImportState.Running,
                Phase = "Starting",
                PhaseNumber = 0,
                PhaseCount = 0,
                PercentComplete = 0,
                StartedAt = now,
                UpdatedAt = now
            };
            _statuses[importId] = status;
            return status;
        }

        public void ReportPhase(Guid importId, string phase, int phaseNumber, int phaseCount, int processed = 0, int total = 0)
        {
            if (!_statuses.TryGetValue(importId, out var status))
                return;

            lock (status)
            {
                // A terminal status is final — a late report must not resurrect it.
                if (status.State != CompetencyFrameworkImportState.Running)
                    return;

                status.Phase = phase;
                status.PhaseNumber = phaseNumber;
                status.PhaseCount = phaseCount;
                status.Processed = processed;
                status.Total = total;
                status.PercentComplete = Math.Max(status.PercentComplete, ComputePercent(phaseNumber, phaseCount, processed, total));
                status.UpdatedAt = DateTime.UtcNow;
            }
        }

        public void Succeed(Guid importId, Guid frameworkId, string frameworkName)
        {
            if (!_statuses.TryGetValue(importId, out var status))
                return;

            lock (status)
            {
                status.State = CompetencyFrameworkImportState.Succeeded;
                status.Phase = "Complete";
                status.PhaseNumber = status.PhaseCount;
                status.PercentComplete = 100;
                status.FrameworkId = frameworkId;
                status.FrameworkName = frameworkName;
                status.CompletedAt = DateTime.UtcNow;
                status.UpdatedAt = status.CompletedAt.Value;
            }
        }

        public void Fail(Guid importId, string error)
        {
            if (!_statuses.TryGetValue(importId, out var status))
                return;

            lock (status)
            {
                status.State = CompetencyFrameworkImportState.Failed;
                status.Error = error;
                status.CompletedAt = DateTime.UtcNow;
                status.UpdatedAt = status.CompletedAt.Value;
            }
        }

        public CompetencyFrameworkImportStatus Get(Guid importId)
        {
            if (!_statuses.TryGetValue(importId, out var status))
                return null;

            // Hand back a copy: the import keeps mutating the stored instance while the
            // caller is serializing it.
            lock (status)
            {
                return new CompetencyFrameworkImportStatus
                {
                    Id = status.Id,
                    State = status.State,
                    Phase = status.Phase,
                    PhaseNumber = status.PhaseNumber,
                    PhaseCount = status.PhaseCount,
                    Processed = status.Processed,
                    Total = status.Total,
                    PercentComplete = status.PercentComplete,
                    FrameworkId = status.FrameworkId,
                    FrameworkName = status.FrameworkName,
                    Error = status.Error,
                    StartedAt = status.StartedAt,
                    UpdatedAt = status.UpdatedAt,
                    CompletedAt = status.CompletedAt
                };
            }
        }

        /// <summary>
        /// Phases are weighted equally: the caller does not know in advance how long each
        /// one takes, and a wrong weighting is worse than an even one because it makes the
        /// bar stall. Within a phase the processed/total fraction fills that phase's share.
        /// </summary>
        private static int ComputePercent(int phaseNumber, int phaseCount, int processed, int total)
        {
            if (phaseCount <= 0 || phaseNumber <= 0)
                return 0;

            var fraction = total > 0 ? Math.Clamp((double)processed / total, 0, 1) : 0;
            var percent = 100.0 * (phaseNumber - 1 + fraction) / phaseCount;
            return (int)Math.Clamp(Math.Round(percent), 0, 99);
        }

        private void Prune()
        {
            var cutoff = DateTime.UtcNow - Retention;
            foreach (var key in _statuses
                .Where(kvp => kvp.Value.CompletedAt.HasValue
                    ? kvp.Value.CompletedAt.Value < cutoff
                    : kvp.Value.UpdatedAt < cutoff)
                .Select(kvp => kvp.Key)
                .ToList())
            {
                _statuses.TryRemove(key, out _);
            }
        }
    }
}
