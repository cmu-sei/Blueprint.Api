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
        public const string MissionImpactName = "DoD CTT Mission Impact Assessment";
        public const string LikelihoodName = "DoD CTT Likelihood Assessment Matrix";
        public const string QuestionsHeading = "Please respond to the following discussion questions:";
        public const string AarHeading = "After-action review: for each objective, what went well and what would your team do differently?";

        public static async Task ApplyAsync(MselEntity msel, MselDraft draft, ICiteApiClient cite, Action<string, string> addChange, CancellationToken ct)
        {
            if (!msel.UseCite)
                return;
            await SetTeamTypesAsync(msel, cite, addChange, ct);
            await SetScoringModelAsync(msel, draft, cite, addChange, ct);
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

        private static async Task SetScoringModelAsync(MselEntity msel, MselDraft draft, ICiteApiClient cite, Action<string, string> addChange, CancellationToken ct)
        {
            var assessment = draft.Design?.CiteAssessment;
            if (assessment is null or MselDraftRules.NoAssessment or MselDraftRules.TemplateModel)
                return;
            ScoringModel model;
            if (assessment == MselDraftRules.QuestionsByMove)
            {
                model = QuestionsByMove(draft, $"{msel.Name} Questions by Move");
                if (model == null)
                {
                    addChange("citeScoringModel", "The draft has no discussion questions, so no CITE scoring model was made. Choose one before pushing.");
                    return;
                }
            }
            else
                model = assessment == MselDraftRules.MissionImpact ? MissionImpact() : Likelihood();

            try
            {
                // the matrices are shared: one copy in CITE serves every exercise that uses it
                var existing = assessment == MselDraftRules.QuestionsByMove ? null
                    : (await cite.GetScoringModelsAsync(null, null, null, ct))
                        .FirstOrDefault(m => string.Equals(m.Description?.Trim(), model.Description, StringComparison.OrdinalIgnoreCase));
                if (existing != null)
                {
                    model = existing;
                    addChange("citeScoringModel", $"Linked to the CITE scoring model \"{model.Description}\".");
                }
                else
                {
                    model = await cite.CreateScoringModelAsync(model, ct);
                    addChange("citeScoringModel", $"Created the CITE scoring model \"{model.Description}\".");
                }
                msel.CiteScoringModelId = model.Id;
                msel.CiteScoringModelName = model.Description;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                addChange("citeScoringModel", $"The CITE scoring model \"{model.Description}\" could not be made ({Reason(ex)}). Create or choose one in CITE before pushing.");
            }
        }

        /// <summary>
        /// The proven questions-by-move shape: one category a move, shown only in that move, its questions as options with
        /// comment boxes, scores hidden and nothing to select; then the objectives for the after-action review in the last move.
        /// Null when the draft has no questions.
        /// </summary>
        public static ScoringModel QuestionsByMove(MselDraft draft, string description)
        {
            var moves = draft.Moves.Where(m => m.CiteQuestions.Any()).ToList();
            if (!moves.Any())
                return null;
            var categories = moves.Select((m, i) => Category(i + 1, QuestionsHeading, "{max}", 1, m.MoveNumber, m.MoveNumber,
                ScoringOptionSelection.None, m.CiteQuestions.Select(q => (q, 1.0)))).ToList();
            if (draft.Objectives.Any())
            {
                var last = draft.Moves.Max(m => m.MoveNumber);
                categories.Add(Category(categories.Count + 1, AarHeading, "{max}", 1, last, last, ScoringOptionSelection.None,
                    draft.Objectives.Select(o => ($"{o.Id}: {o.Task}. Standard: {o.Standard}", 1.0))));
            }
            return new ScoringModel
            {
                Description = description,
                CalculationEquation = "{average}",
                Status = ItemStatus.Active,
                HideScoresOnScoreSheet = true,
                DisplayCommentTextBoxes = true,
                DisplayScoringModelByMoveNumber = true,
                ShowPastSituationDescriptions = false,
                UseSubmit = true,
                UseUserScore = true,
                UseTeamScore = true,
                UseOfficialScore = false,
                RightSideDisplay = RightSideDisplay.Scoresheet,
                ScoringCategories = categories
            };
        }

        /// <summary>The DoD Cyber Table Top mission impact assessment, weighted toward mission capability, scored each move</summary>
        public static ScoringModel MissionImpact() => new()
        {
            Description = MissionImpactName,
            CalculationEquation = "100 > {sum} > 0",
            Status = ItemStatus.Active,
            DisplayScoringModelByMoveNumber = true,
            ShowPastSituationDescriptions = true,
            UseSubmit = true,
            UseUserScore = true,
            UseTeamScore = true,
            UseOfficialScore = true,
            RightSideDisplay = RightSideDisplay.ScoreSummary,
            ScoringCategories =
            [
                Category(1, "Mission Capability Impact", "{max}", 8, 1, 999, ScoringOptionSelection.Single,
                [
                    ("Non-Mission Capable - Mission cannot be completed", 5),
                    ("Severely Degraded - Mission completion questionable", 4),
                    ("Partial Mission Capable - Primary objective achievable with delays", 3),
                    ("Minor Impact - 1-2 secondary objectives degraded", 2),
                    ("Fully Mission Capable - All objectives achievable on time", 1)
                ]),
                Category(2, "Time to Recover/Restore", "{max}", 6, 1, 999, ScoringOptionSelection.Single,
                [
                    ("Permanent/Mission failure", 5),
                    ("Days (< 1 week)", 4),
                    ("Hours (< 24 hrs)", 3),
                    ("Minutes (< 30 min)", 2),
                    ("Immediate/No recovery needed", 1)
                ]),
                Category(3, "System Availability Impact", "{max}", 3, 1, 999, ScoringOptionSelection.Single,
                [
                    ("0% - Complete system failure", 5),
                    ("25% - Critical systems compromised", 4),
                    ("50% - Some critical functions degraded", 3),
                    ("75% - Non-critical systems affected", 2),
                    ("100% - All systems operational", 1)
                ]),
                Category(4, "Data Integrity Impact", "{max}", 3, 1, 999, ScoringOptionSelection.Single,
                [
                    ("Complete data destruction/exfiltration", 5),
                    ("Critical data loss or manipulation", 4),
                    ("Significant data corruption", 3),
                    ("Minor data inconsistencies", 2),
                    ("No data corruption or loss", 1)
                ])
            ]
        };

        /// <summary>The DoD Cyber Table Top likelihood matrix: attack cost plus attack success, with the matrix beside the score sheet</summary>
        public static ScoringModel Likelihood() => new()
        {
            Description = LikelihoodName,
            CalculationEquation = "5 > {sum} > 1",
            Status = ItemStatus.Active,
            ShowPastSituationDescriptions = true,
            UseSubmit = true,
            UseUserScore = true,
            UseTeamScore = true,
            RightSideDisplay = RightSideDisplay.HtmlBlock,
            RightSideHtmlBlock = LikelihoodMatrixHtml,
            ScoringCategories =
            [
                Category(1, "Attack Cost / Level of Effort", "{sum}", 1, 1, 999, ScoringOptionSelection.Single,
                [
                    ("Nascent - Limited threat: nearly anyone can build; low cost or easy to develop", 2),
                    ("Moderate - Moderate threat: criminal-level organization can build; moderate cost or many can develop", 1),
                    ("Advanced - Advanced threat: nation-state organization can build; high cost or hard to develop", 0)
                ]),
                Category(2, "Attack Success Likelihood", "{sum}", 1, 1, 999, ScoringOptionSelection.Single,
                [
                    ("Low - Rarely works", 1),
                    ("Medium - Sometimes works", 2),
                    ("High - Always works", 3)
                ])
            ]
        };

        public static bool IsControl(TeamEntity team) =>
            string.Equals(team.ShortName?.Trim(), "EXCON", StringComparison.OrdinalIgnoreCase);

        private static ScoringCategory Category(int order, string description, string equation, double weight, int firstMove, int lastMove,
            ScoringOptionSelection selection, IEnumerable<(string Description, double Value)> options) => new()
            {
                DisplayOrder = order,
                Description = description,
                CalculationEquation = equation,
                ScoringWeight = weight,
                MoveNumberFirstDisplay = firstMove,
                MoveNumberLastDisplay = lastMove,
                ScoringOptionSelection = selection,
                ScoringOptions = options.Select((o, i) => new ScoringOption { DisplayOrder = i + 1, Description = o.Description, Value = o.Value }).ToList()
            };

        private static string Reason(Exception ex) => ex is ApiException api
            ? api.StatusCode == 403 ? "you may not do that in CITE" : $"CITE answered {api.StatusCode}"
            : "CITE could not be reached";

        // the DoD Cyber Table Top likelihood matrix, as reproduced in the SEACAT Project CYBERCLAW white paper, Fig. 1
        public const string LikelihoodMatrixHtml = @"<h3>DoD CTT Likelihood Assessment</h3><table border=""1"" cellpadding=""6"" cellspacing=""0"" width=""100%""><tr><td colspan=""2""></td><td colspan=""3"" align=""center"" bgcolor=""#DDEBF7""><font color=""#000000""><b>Attack Success Likelihood</b></font></td></tr><tr><td colspan=""2"" bgcolor=""#BFBFBF""><font color=""#000000""><b>Attack Cost / Level of Effort</b></font></td><td align=""center"" bgcolor=""#E2EFDA""><font color=""#000000""><b>Low</b><br>Rarely works</font></td><td align=""center"" bgcolor=""#E2EFDA""><font color=""#000000""><b>Medium</b><br>Sometimes works</font></td><td align=""center"" bgcolor=""#E2EFDA""><font color=""#000000""><b>High</b><br>Always works</font></td></tr><tr><td bgcolor=""#F4B6B6""><font color=""#000000"">Nearly anyone can build: Nascent - Limited threat</font></td><td bgcolor=""#F4B6B6""><font color=""#000000"">Low cost or easy to develop</font></td><td align=""center"" bgcolor=""#FFC000""><font color=""#000000""><b><font size=""5"">3</font></b><br><small>Example: Network DoS</small></font></td><td align=""center"" bgcolor=""#FF8C00""><font color=""#000000""><b><font size=""5"">4</font></b></font></td><td align=""center"" bgcolor=""#FF0000""><font color=""#000000""><b><font size=""5"">5</font></b><br><small>Example: Flash implant delivered via website/email</small></font></td></tr><tr><td bgcolor=""#FCE9C8""><font color=""#000000"">Criminal level organization can build: Moderate threat</font></td><td bgcolor=""#FCE9C8""><font color=""#000000"">Moderate cost or many can develop</font></td><td align=""center"" bgcolor=""#FFFF00""><font color=""#000000""><b><font size=""5"">2</font></b></font></td><td align=""center"" bgcolor=""#FFC000""><font color=""#000000""><b><font size=""5"">3</font></b></font></td><td align=""center"" bgcolor=""#FF8C00""><font color=""#000000""><b><font size=""5"">4</font></b></font></td></tr><tr><td bgcolor=""#D8E7C8""><font color=""#000000"">Nation state organization can build: Advanced threat</font></td><td bgcolor=""#D8E7C8""><font color=""#000000"">High cost or hard to develop</font></td><td align=""center"" bgcolor=""#00B050""><font color=""#000000""><b><font size=""5"">1</font></b><br><small>Example: RF inject of malware into sensor or radio</small></font></td><td align=""center"" bgcolor=""#FFFF00""><font color=""#000000""><b><font size=""5"">2</font></b></font></td><td align=""center"" bgcolor=""#FFC000""><font color=""#000000""><b><font size=""5"">3</font></b><br><small>Example: Supply chain implant in HW or firmware</small></font></td></tr></table><p><small>Source: DoD Cyber Table Top Guide likelihood assessment, as reproduced in SEACAT Project CYBERCLAW White Paper, Fig. 1. Score = Attack Cost value + Attack Success value (1-5).</small></p>";
    }
}
