// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

namespace Blueprint.Api.Data.Models
{
    /// <summary>
    /// The fields of a CITE scoring model that a MSEL's own model, its API model and the CITE client's models share,
    /// so a conversion copies them in one place and a new field is added once. The right-side display is not among
    /// them: Blueprint keeps CITE's stored numbers and the client numbers its own, so each conversion maps it by name.
    /// </summary>
    public interface IScoringModelFields
    {
        string Description { get; set; }
        string CalculationEquation { get; set; }
        bool HideScoresOnScoreSheet { get; set; }
        bool DisplayCommentTextBoxes { get; set; }
        bool DisplayScoringModelByMoveNumber { get; set; }
        bool ShowPastSituationDescriptions { get; set; }
        bool UseSubmit { get; set; }
        bool UseUserScore { get; set; }
        bool UseTeamScore { get; set; }
        bool UseTeamAverageScore { get; set; }
        bool UseTypeAverageScore { get; set; }
        bool UseOfficialScore { get; set; }
        string RightSideHtmlBlock { get; set; }
        string RightSideEmbeddedUrl { get; set; }
    }

    /// <summary>A scoring category's shared fields; the option selection is mapped by name, as the right-side display is.</summary>
    public interface IScoringCategoryFields
    {
        int DisplayOrder { get; set; }
        string Description { get; set; }
        string CalculationEquation { get; set; }
        bool IsModifierRequired { get; set; }
        double ScoringWeight { get; set; }
        int MoveNumberFirstDisplay { get; set; }
        int MoveNumberLastDisplay { get; set; }
    }

    public interface IScoringOptionFields
    {
        int DisplayOrder { get; set; }
        string Description { get; set; }
        bool IsModifier { get; set; }
        double Value { get; set; }
    }

    public static class ScoringModelFieldsExtensions
    {
        public static T CopyFieldsTo<T>(this IScoringModelFields from, T to) where T : IScoringModelFields
        {
            to.Description = from.Description;
            to.CalculationEquation = from.CalculationEquation;
            to.HideScoresOnScoreSheet = from.HideScoresOnScoreSheet;
            to.DisplayCommentTextBoxes = from.DisplayCommentTextBoxes;
            to.DisplayScoringModelByMoveNumber = from.DisplayScoringModelByMoveNumber;
            to.ShowPastSituationDescriptions = from.ShowPastSituationDescriptions;
            to.UseSubmit = from.UseSubmit;
            to.UseUserScore = from.UseUserScore;
            to.UseTeamScore = from.UseTeamScore;
            to.UseTeamAverageScore = from.UseTeamAverageScore;
            to.UseTypeAverageScore = from.UseTypeAverageScore;
            to.UseOfficialScore = from.UseOfficialScore;
            to.RightSideHtmlBlock = from.RightSideHtmlBlock;
            to.RightSideEmbeddedUrl = from.RightSideEmbeddedUrl;
            return to;
        }

        public static T CopyFieldsTo<T>(this IScoringCategoryFields from, T to) where T : IScoringCategoryFields
        {
            to.DisplayOrder = from.DisplayOrder;
            to.Description = from.Description;
            to.CalculationEquation = from.CalculationEquation;
            to.IsModifierRequired = from.IsModifierRequired;
            to.ScoringWeight = from.ScoringWeight;
            to.MoveNumberFirstDisplay = from.MoveNumberFirstDisplay;
            to.MoveNumberLastDisplay = from.MoveNumberLastDisplay;
            return to;
        }

        public static T CopyFieldsTo<T>(this IScoringOptionFields from, T to) where T : IScoringOptionFields
        {
            to.DisplayOrder = from.DisplayOrder;
            to.Description = from.Description;
            to.IsModifier = from.IsModifier;
            to.Value = from.Value;
            return to;
        }
    }
}
