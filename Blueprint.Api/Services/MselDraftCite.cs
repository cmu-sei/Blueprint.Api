// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Blueprint.Api.Data.Models;
using Blueprint.Api.ViewModels;
using Cite.Api.Client;
using Exception = System.Exception;

namespace Blueprint.Api.Services
{
    /// <summary>
    /// The CITE side of a drafted MSEL, set up when the planner creates it, as the blueprint-ttx loader does: each team
    /// gets one of CITE's built-in team types, and the MSEL gets the scoring model the planner chose. A questions-by-move
    /// model is written from the draft; the DoD Cyber Table Top matrices are found in CITE by name, or created there.
    /// Nothing here stops the MSEL being created: what CITE refuses is reported for the planner to finish.
    /// </summary>
    public static class MselDraftCite
    {
        public const string ScoreContributorType = "Official Score Contributor";
        public static readonly string[] ControlTypes = ["Other", "Organization"];
        public const string QuestionsHeading = ScoringModelPresets.QuestionsHeading;
        public const string AarHeading = ScoringModelPresets.AarHeading;

        public static async Task ApplyAsync(MselEntity msel, MselDraft draft, ICiteApiClient cite, Action<string, string> addChange, CancellationToken ct)
        {
            if (!msel.UseCite)
                return;
            await SetTeamTypesAsync(msel, cite, addChange, ct);
            SetScoringModel(msel, draft, addChange);
        }

        // every participant team contributes to the official score; exercise control only watches
        private static async Task SetTeamTypesAsync(MselEntity msel, ICiteApiClient cite, Action<string, string> addChange, CancellationToken ct)
        {
            var teams = msel.Teams.Where(t => t.CiteTeamTypeId == null).ToList();
            if (!teams.Any())
                return;
            ICollection<TeamType> types;
            try
            {
                types = await cite.GetTeamTypesAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                addChange("citeTeamTypes", $"CITE's team types could not be read ({Reason(ex)}). Give each team a CITE team type before pushing.");
                return;
            }
            TeamType Named(params string[] names) => names
                .Select(n => types.FirstOrDefault(t => string.Equals(t.Name?.Trim(), n, StringComparison.OrdinalIgnoreCase)))
                .FirstOrDefault(t => t != null);
            var contributor = Named(ScoreContributorType);
            var control = Named(ControlTypes);
            var missing = new List<string>();
            foreach (var team in teams)
            {
                var type = IsControl(team) ? control : contributor;
                if (type == null)
                    missing.Add(team.ShortName);
                else
                {
                    team.CiteTeamTypeId = type.Id;
                    team.CiteTeamTypeName = type.Name;
                }
            }
            var set = teams.Where(t => t.CiteTeamTypeId != null).ToList();
            if (set.Any())
                addChange("citeTeamTypes", $"Gave {string.Join(", ", set.Select(t => t.ShortName))} CITE team types: {ScoreContributorType} for participant teams, {control?.Name ?? ControlTypes[0]} for exercise control.");
            if (missing.Any())
                addChange("citeTeamTypes", $"CITE has no matching team type for {string.Join(", ", missing)}. Give them one before pushing.");
        }

        /// <summary>
        /// The scoring model the planner chose, authored into the MSEL as its own, as the Config tab authors one: the planner
        /// reviews and edits it in Blueprint, the push creates it in CITE for the evaluation, and nothing reaches CITE for a
        /// draft that is discarded. Questions by move is written from the draft's questions, the matrices from the presets.
        /// </summary>
        private static void SetScoringModel(MselEntity msel, MselDraft draft, Action<string, string> addChange)
        {
            var assessment = draft.Design?.CiteAssessment;
            if (assessment is null or MselDraftRules.NoAssessment or MselDraftRules.TemplateModel)
                return;
            // a template that authors its own model hands the MSEL a copy; the draft does not write over it
            if (msel.ScoringModel != null)
            {
                addChange("citeScoringModel", $"Kept the template's own CITE scoring model \"{msel.ScoringModel.Description}\". Review it under the CITE settings on the Info tab.");
                return;
            }
            MselScoringModelEntity model;
            if (assessment == MselDraftRules.QuestionsByMove)
            {
                var moves = draft.Moves.Where(m => m.CiteQuestions.Any()).Select(m => (m.MoveNumber, (IEnumerable<string>)m.CiteQuestions)).ToList();
                if (!moves.Any())
                {
                    addChange("citeScoringModel", "The draft has no discussion questions, so no CITE scoring model was made. Author or link one under the CITE settings on the Info tab before pushing.");
                    return;
                }
                model = ScoringModelPresets.QuestionsByMove($"{msel.Name} Questions by Move", moves,
                    draft.Objectives.Any() ? draft.Objectives.Select(o => $"{o.Id}: {o.Task}. Standard: {o.Standard}") : null);
                // the after-action review belongs in the hot wash, after ENDEX, not the last move of play
                var hotWash = draft.Moves.Max(m => m.MoveNumber);
                foreach (var aar in model.Categories.Where(c => c.Description == ScoringModelPresets.AarHeading))
                    (aar.MoveNumberFirstDisplay, aar.MoveNumberLastDisplay) = (hotWash, hotWash);
            }
            else
                model = assessment switch
                {
                    MselDraftRules.MissionImpact => ScoringModelPresets.MissionImpact(),
                    MselDraftRules.Nciss => ScoringModelPresets.Nciss(),
                    _ => ScoringModelPresets.Likelihood()
                };

            model.MselId = msel.Id;
            model.CreatedBy = msel.CreatedBy;
            foreach (var category in model.Categories)
            {
                category.Id = Guid.NewGuid();
                category.MselScoringModelId = msel.Id;
                category.CreatedBy = msel.CreatedBy;
                foreach (var option in category.Options)
                {
                    option.Id = Guid.NewGuid();
                    option.MselScoringCategoryId = category.Id;
                    option.CreatedBy = msel.CreatedBy;
                }
            }
            msel.ScoringModel = model;
            // the MSEL's own model is pushed, so no CITE model is linked beside it
            msel.CiteScoringModelId = null;
            msel.CiteScoringModelName = null;
            addChange("citeScoringModel", $"Authored the CITE scoring model \"{model.Description}\" in Blueprint. Review it under the CITE settings on the Info tab; the push creates it in CITE.");
        }

        public static bool IsControl(TeamEntity team) =>
            string.Equals(team.ShortName?.Trim(), "EXCON", StringComparison.OrdinalIgnoreCase);

        private static string Reason(Exception ex) => ex is ApiException api
            ? api.StatusCode == 403 ? "you may not do that in CITE" : $"CITE answered {api.StatusCode}"
            : "CITE could not be reached";

    }
}
