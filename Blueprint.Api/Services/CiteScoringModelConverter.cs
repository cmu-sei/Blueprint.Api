// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Linq;
using Blueprint.Api.Data.Enumerations;
using Blueprint.Api.Data.Models;
using Cite.Api.Client;

namespace Blueprint.Api.Services
{
    /// <summary>
    /// Converts between a MSEL's own scoring model and the CITE client's. The enums go by name: Blueprint keeps CITE's
    /// stored values (None = 20), the client numbers its copies from 0, and the wire carries the names.
    /// </summary>
    public static class CiteScoringModelConverter
    {
        /// <summary>The model to create in CITE, categories and options nested, in display order and with no ids.</summary>
        public static ScoringModel ToCite(MselScoringModelEntity model) => new()
        {
            Description = model.Description,
            CalculationEquation = model.CalculationEquation,
            Status = ItemStatus.Active,
            HideScoresOnScoreSheet = model.HideScoresOnScoreSheet,
            DisplayCommentTextBoxes = model.DisplayCommentTextBoxes,
            DisplayScoringModelByMoveNumber = model.DisplayScoringModelByMoveNumber,
            ShowPastSituationDescriptions = model.ShowPastSituationDescriptions,
            UseSubmit = model.UseSubmit,
            UseUserScore = model.UseUserScore,
            UseTeamScore = model.UseTeamScore,
            UseTeamAverageScore = model.UseTeamAverageScore,
            UseTypeAverageScore = model.UseTypeAverageScore,
            UseOfficialScore = model.UseOfficialScore,
            RightSideDisplay = Enum.Parse<RightSideDisplay>(model.RightSideDisplay.ToString()),
            RightSideHtmlBlock = model.RightSideHtmlBlock,
            RightSideEmbeddedUrl = model.RightSideEmbeddedUrl,
            ScoringCategories = model.Categories.OrderBy(c => c.DisplayOrder).Select(c => new ScoringCategory
            {
                DisplayOrder = c.DisplayOrder,
                Description = c.Description,
                CalculationEquation = c.CalculationEquation,
                IsModifierRequired = c.IsModifierRequired,
                ScoringWeight = c.ScoringWeight,
                MoveNumberFirstDisplay = c.MoveNumberFirstDisplay,
                MoveNumberLastDisplay = c.MoveNumberLastDisplay,
                ScoringOptionSelection = Enum.Parse<ScoringOptionSelection>(c.ScoringOptionSelection.ToString()),
                ScoringOptions = c.Options.OrderBy(o => o.DisplayOrder).Select(o => new ScoringOption
                {
                    DisplayOrder = o.DisplayOrder,
                    Description = o.Description,
                    IsModifier = o.IsModifier,
                    Value = o.Value
                }).ToList()
            }).ToList()
        };

        /// <summary>A MSEL's own copy of a CITE model, unsaved and with no ids.</summary>
        public static MselScoringModelEntity FromCite(ScoringModel model) => new()
        {
            Description = model.Description,
            CalculationEquation = model.CalculationEquation,
            HideScoresOnScoreSheet = model.HideScoresOnScoreSheet,
            DisplayCommentTextBoxes = model.DisplayCommentTextBoxes,
            DisplayScoringModelByMoveNumber = model.DisplayScoringModelByMoveNumber,
            ShowPastSituationDescriptions = model.ShowPastSituationDescriptions,
            UseSubmit = model.UseSubmit,
            UseUserScore = model.UseUserScore,
            UseTeamScore = model.UseTeamScore,
            UseTeamAverageScore = model.UseTeamAverageScore,
            UseTypeAverageScore = model.UseTypeAverageScore,
            UseOfficialScore = model.UseOfficialScore,
            RightSideDisplay = Enum.Parse<CiteRightSideDisplay>(model.RightSideDisplay.ToString()),
            RightSideHtmlBlock = model.RightSideHtmlBlock,
            RightSideEmbeddedUrl = model.RightSideEmbeddedUrl,
            Categories = (model.ScoringCategories ?? []).OrderBy(c => c.DisplayOrder).Select(c => new MselScoringCategoryEntity
            {
                DisplayOrder = c.DisplayOrder,
                Description = c.Description,
                CalculationEquation = c.CalculationEquation,
                IsModifierRequired = c.IsModifierRequired,
                ScoringWeight = c.ScoringWeight,
                MoveNumberFirstDisplay = c.MoveNumberFirstDisplay,
                MoveNumberLastDisplay = c.MoveNumberLastDisplay,
                ScoringOptionSelection = Enum.Parse<CiteScoringOptionSelection>(c.ScoringOptionSelection.ToString()),
                Options = (c.ScoringOptions ?? []).OrderBy(o => o.DisplayOrder).Select(o => new MselScoringOptionEntity
                {
                    DisplayOrder = o.DisplayOrder,
                    Description = o.Description,
                    IsModifier = o.IsModifier,
                    Value = o.Value
                }).ToList()
            }).ToList()
        };
    }
}
