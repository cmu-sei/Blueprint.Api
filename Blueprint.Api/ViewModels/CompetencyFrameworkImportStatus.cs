// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using System;

namespace Blueprint.Api.ViewModels
{
    public enum CompetencyFrameworkImportState
    {
        Running,
        Succeeded,
        Failed
    }

    /// <summary>
    /// Progress of a single competency framework import. A large framework takes many
    /// seconds to import, so the client passes an importId on the import request and
    /// polls competencyframeworks/imports/{importId} to report progress instead of
    /// showing an indefinite spinner.
    /// </summary>
    public class CompetencyFrameworkImportStatus
    {
        public Guid Id { get; set; }
        public CompetencyFrameworkImportState State { get; set; }

        /// <summary>Human-readable name of the phase currently running, e.g. "Saving competencies".</summary>
        public string Phase { get; set; }
        public int PhaseNumber { get; set; }
        public int PhaseCount { get; set; }

        /// <summary>Items handled so far within the current phase. Total is 0 when the phase is not countable.</summary>
        public int Processed { get; set; }
        public int Total { get; set; }

        /// <summary>
        /// Progress through the import's phases, not a fraction of elapsed time — phases
        /// are weighted equally, so the bar advances unevenly. It only ever increases.
        /// </summary>
        public int PercentComplete { get; set; }

        public Guid? FrameworkId { get; set; }
        public string FrameworkName { get; set; }
        public string Error { get; set; }

        public DateTime StartedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
        public DateTime? CompletedAt { get; set; }
    }
}
