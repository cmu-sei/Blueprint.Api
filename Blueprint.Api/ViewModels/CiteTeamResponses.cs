// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;

namespace Blueprint.Api.ViewModels
{
    /// <summary>
    /// What a team answered on its CITE scoring model for one move, for the Assessor View.
    /// </summary>
    /// <remarks>
    /// Blueprint's own shape rather than CITE's Submission: everything here is resolved to text (category and
    /// option descriptions, comments), so it can be filled from live CITE or from a stored record of a run.
    /// It holds team-level answers only, never an individual participant's, and no user names.
    /// </remarks>
    public class CiteTeamResponses
    {
        public Guid MselId { get; set; }
        public Guid TeamId { get; set; }
        public int MoveNumber { get; set; }
        public CiteTeamResponsesStatus Status { get; set; }
        /// <summary>Why there are no answers to show, when <see cref="Status"/> is not Answered or NotAnswered.</summary>
        public string Message { get; set; }
        /// <summary>The move the CITE evaluation is on, when CITE was reached.</summary>
        public int? CurrentMoveNumber { get; set; }
        public string ScoringModelDescription { get; set; }
        /// <summary>The team's own submission for the move.</summary>
        public CiteSubmissionResponse TeamSubmission { get; set; }
        /// <summary>The evaluation's official submission for the move, when the scoring model uses one.</summary>
        public CiteSubmissionResponse OfficialSubmission { get; set; }
        /// <summary>The average of the team members' own submissions, when the scoring model uses one.</summary>
        public CiteSubmissionResponse TeamAverageSubmission { get; set; }
    }

    public enum CiteTeamResponsesStatus
    {
        /// <summary>The team has selected at least one option or written a comment.</summary>
        Answered,
        /// <summary>The team has a submission for the move but has not selected anything in it, or has none.</summary>
        NotAnswered,
        /// <summary>The MSEL does not use CITE, or has not been pushed to it.</summary>
        NotPushed,
        /// <summary>The team has no CITE team type, so the push did not create it in CITE.</summary>
        TeamNotInCite,
        /// <summary>The CITE evaluation has no move with this number - it was added after the push.</summary>
        MoveNotInCite,
        /// <summary>The CITE evaluation has not reached this move yet.</summary>
        MoveNotStarted,
        /// <summary>CITE could not be reached, or refused Blueprint's service account.</summary>
        Unavailable
    }

    public class CiteSubmissionResponse
    {
        public double Score { get; set; }
        public string Status { get; set; }
        public DateTimeOffset? DateModified { get; set; }
        /// <summary>True for an average of several submissions, whose options carry how many chose them.</summary>
        public bool IsAverage { get; set; }
        public List<CiteCategoryResponse> Categories { get; set; } = new();
    }

    public class CiteCategoryResponse
    {
        public Guid ScoringCategoryId { get; set; }
        public string Description { get; set; }
        public int DisplayOrder { get; set; }
        public double Score { get; set; }
        public List<CiteOptionResponse> SelectedOptions { get; set; } = new();
        public List<CiteCommentResponse> Comments { get; set; } = new();
    }

    public class CiteOptionResponse
    {
        public string Description { get; set; }
        public double Value { get; set; }
        public bool IsModifier { get; set; }
        /// <summary>On an average submission, how many of the averaged submissions selected the option.</summary>
        public int SelectedCount { get; set; }
    }

    public class CiteCommentResponse
    {
        /// <summary>The option the comment was written on.</summary>
        public string OptionDescription { get; set; }
        public string Text { get; set; }
    }
}
