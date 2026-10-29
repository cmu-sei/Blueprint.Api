// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using Blueprint.Api.Data.Enumerations;

namespace Blueprint.Api.ViewModels
{
    /// <summary>
    /// The CITE scoring model a MSEL authors itself, with CITE's ScoringModel fields. Pushing the MSEL creates it
    /// in CITE for the MSEL's evaluation.
    /// </summary>
    public class MselScoringModel
    {
        public Guid MselId { get; set; }

        // CITE lists scoring models by their description, so it is the model's name
        [Required]
        public string Description { get; set; }
        public string CalculationEquation { get; set; }
        public bool HideScoresOnScoreSheet { get; set; }
        public bool DisplayCommentTextBoxes { get; set; }
        public bool DisplayScoringModelByMoveNumber { get; set; }
        public bool ShowPastSituationDescriptions { get; set; }
        public bool UseSubmit { get; set; }
        public bool UseUserScore { get; set; }
        public bool UseTeamScore { get; set; }
        public bool UseTeamAverageScore { get; set; }
        public bool UseTypeAverageScore { get; set; }
        public bool UseOfficialScore { get; set; }
        public CiteRightSideDisplay RightSideDisplay { get; set; }
        public string RightSideHtmlBlock { get; set; }
        public string RightSideEmbeddedUrl { get; set; }
        /// <summary>In display order. A replace numbers them in the order they are sent.</summary>
        public List<MselScoringCategory> Categories { get; set; } = new List<MselScoringCategory>();
    }

    public class MselScoringCategory
    {
        public Guid Id { get; set; }
        public int DisplayOrder { get; set; }
        public string Description { get; set; }
        public string CalculationEquation { get; set; }
        public bool IsModifierRequired { get; set; }
        public double ScoringWeight { get; set; }
        /// <summary>The first move whose score sheet shows the category, when the model displays by move number.</summary>
        public int MoveNumberFirstDisplay { get; set; }
        public int MoveNumberLastDisplay { get; set; }
        public CiteScoringOptionSelection ScoringOptionSelection { get; set; }
        /// <summary>In display order. A replace numbers them in the order they are sent.</summary>
        public List<MselScoringOption> Options { get; set; } = new List<MselScoringOption>();
    }

    public class MselScoringOption
    {
        public Guid Id { get; set; }
        public int DisplayOrder { get; set; }
        public string Description { get; set; }
        public bool IsModifier { get; set; }
        public double Value { get; set; }
    }

    /// <summary>The starting points a MSEL's own scoring model can be created from.</summary>
    public enum ScoringModelPreset
    {
        /// <summary>A discussion question category for each move, shown in that move, then an after-action review.</summary>
        QuestionsByMove,
        /// <summary>The DoD Cyber Table Top mission impact assessment.</summary>
        MissionImpact,
        /// <summary>The DoD Cyber Table Top likelihood assessment matrix.</summary>
        Likelihood,
        /// <summary>The CISA National Cyber Incident Scoring System.</summary>
        Nciss
    }
}
