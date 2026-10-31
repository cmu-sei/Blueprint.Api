// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using Blueprint.Api.Data.Enumerations;
using Blueprint.Api.Data.Models;
using Blueprint.Api.ViewModels;

namespace Blueprint.Api.Services
{
    /// <summary>
    /// The pages a drafted MSEL gets besides the read-in and profiles, as the author-ttx workflow builds them: an
    /// overview participants keep open during play, and a guide for exercise control only. Both are written from the
    /// reviewed draft, on white paper so they read the same in either theme.
    /// </summary>
    public static class MselDraftPages
    {
        private const string Th = "<td bgcolor=\"#F3F5F8\" valign=\"top\"><b>{0}</b></td>";

        /// <summary>The objectives, roles, schedule and assessment participants need through the exercise</summary>
        public static string Overview(MselDraft draft, MselEntity msel, DateTime startTime)
        {
            var sb = new StringBuilder();
            sb.Append($"<section><h2>{E(draft.Name)}: Exercise Overview</h2>");
            if (!string.IsNullOrWhiteSpace(draft.Description))
                sb.Append($"<p>{E(draft.Description)}</p>");
            var format = draft.Design?.Format switch
            {
                "Discussion" => "A discussion-based tabletop exercise: your team talks through the decisions; there are no hands-on tasks.",
                "Functional" => "A functional exercise: your team works hands-on in the range.",
                "Hybrid" => "A hybrid exercise: discussion moves with some hands-on work in the range.",
                _ => null
            };
            if (format != null)
                sb.Append($"<p><b>Format:</b> {E(format)}</p>");

            if (!string.IsNullOrWhiteSpace(draft.Scenario?.Problem))
                sb.Append($"<h3>The problem</h3><p>{E(draft.Scenario.Problem)}</p>");
            if (draft.Scenario?.Sectors?.Any() == true)
                sb.Append($"<p><b>Critical infrastructure sectors:</b> {E(string.Join(", ", draft.Scenario.Sectors))}</p>");

            if (draft.Objectives.Any())
            {
                sb.Append("<h3>Objectives</h3>");
                sb.Append(Table(["", "Task", "Condition", "Standard"],
                    draft.Objectives.Select(o => new[] { o.Id, o.Task, o.Condition, o.Standard })));
            }

            var teams = msel.Teams.Where(t => !string.IsNullOrWhiteSpace(t.ShortName)).ToList();
            if (teams.Any())
            {
                sb.Append("<h3>Teams and roles</h3><ul>");
                foreach (var team in teams)
                {
                    var role = OneLine(DataValueDraftService.StripHtml(team.Description));
                    sb.Append($"<li><b>{E(team.Name)} ({E(team.ShortName)})</b>{(string.IsNullOrEmpty(role) ? "" : ": " + E(role))}</li>");
                }
                sb.Append("</ul>");
            }

            sb.Append("<h3>Schedule</h3>");
            sb.Append(Table(["Move", "Starts", "Scenario time", ""],
                draft.Moves.Select(m => new[]
                {
                    m.MoveNumber.ToString(),
                    $"{startTime.AddMinutes(m.StartMinutes):HH:mm} UTC (+{Clock(m.StartMinutes)})",
                    ScenarioTime(m.SituationTime),
                    m.Description
                })));
            sb.Append($"<p>The exercise runs {Clock(draft.DurationMinutes)}. The scenario has its own clock, which can jump ahead between moves; " +
                "date what you write by the scenario clock.</p>");

            var assessment = draft.Design?.CiteAssessment switch
            {
                MselDraftRules.QuestionsByMove => "Each move, your team answers the move's discussion questions in CITE and checks off its tasks.",
                MselDraftRules.MissionImpact => "Each move, your team scores the mission impact of what has happened in CITE and checks off its tasks.",
                MselDraftRules.Likelihood => "Each move, your team scores how likely each attack is to succeed in CITE and checks off its tasks.",
                _ => null
            };
            if (assessment != null)
                sb.Append($"<h3>Assessment</h3><p>{E(assessment)}</p>");
            sb.Append("</section>");
            return GalleryArticleHtml.Prepare(sb.ToString(), null, GalleryArticleHtml.ArticleWidth);
        }

        /// <summary>
        /// What exercise control needs to run each move: its purpose, the decisions to draw out, notes, questions, and
        /// each inject's expected action and objective; then the AAR objectives. Null when the draft has none of it.
        /// </summary>
        public static string FacilitatorGuide(MselDraft draft, MselEntity msel, DateTime startTime)
        {
            var hasGuidance = draft.Objectives.Any() || draft.Moves.Any(m => m.Purpose != null || m.ExpectedDecisions.Any() || m.ControllerNotes != null)
                || draft.Injects.Any(i => i.ExpectedAction != null);
            if (!hasGuidance)
                return null;

            var sb = new StringBuilder();
            sb.Append($"<section><h2>{E(draft.Name)}: Facilitator Guide</h2>");
            sb.Append("<p><b>For exercise control.</b> Participants cannot see this page.</p>");
            var scenario = draft.Scenario ?? new MselDraftScenario();
            if (!string.IsNullOrWhiteSpace(scenario.Premise) || !string.IsNullOrWhiteSpace(scenario.Environment))
            {
                sb.Append("<h3>Scenario</h3>");
                if (!string.IsNullOrWhiteSpace(scenario.Premise))
                    sb.Append($"<p>{E(scenario.Premise)}</p>");
                if (!string.IsNullOrWhiteSpace(scenario.Environment))
                    sb.Append($"<p><b>Operational environment:</b> {E(scenario.Environment)}</p>");
                if (!string.IsNullOrWhiteSpace(scenario.EscalationCeiling))
                    sb.Append($"<p><b>Escalation ceiling:</b> {E(scenario.EscalationCeiling)}</p>");
                if (scenario.Sectors.Any())
                    sb.Append($"<p><b>Critical infrastructure sectors:</b> {E(string.Join(", ", scenario.Sectors))}</p>");
                if (scenario.Assumptions.Any())
                    sb.Append("<p><b>Assumptions</b></p>" + List("ul", scenario.Assumptions));
            }

            var objectives = draft.Objectives.ToDictionary(o => o.Id, StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < draft.Moves.Count; i++)
            {
                var move = draft.Moves[i];
                var ends = i + 1 < draft.Moves.Count ? draft.Moves[i + 1].StartMinutes : int.MaxValue;
                sb.Append("<br><hr><br>");
                sb.Append($"<h3>Move {move.MoveNumber}: {E(move.Description)}</h3>");
                sb.Append($"<p><b>Starts:</b> {startTime.AddMinutes(move.StartMinutes):HH:mm} UTC (+{Clock(move.StartMinutes)})" +
                    (move.SituationTime == null ? "" : $" &#183; <b>Scenario time:</b> {E(ScenarioTime(move.SituationTime))}") + "</p>");
                if (move.Purpose != null)
                    sb.Append($"<p><b>Purpose:</b> {E(move.Purpose)}</p>");
                if (!string.IsNullOrWhiteSpace(move.SituationDescription))
                    sb.Append($"<p><b>Situation:</b> {E(DataValueDraftService.StripHtml(move.SituationDescription))}</p>");
                if (move.ExpectedDecisions.Any())
                    sb.Append("<p><b>Expected decisions</b></p>" + List("ul", move.ExpectedDecisions));
                if (move.ControllerNotes != null)
                    sb.Append($"<p><b>Controller notes:</b> {E(move.ControllerNotes)}</p>");
                if (move.CiteQuestions.Any())
                    sb.Append("<p><b>Discussion questions (CITE)</b></p>" + List("ol", move.CiteQuestions));
                var injects = draft.Injects.Where(x => x.Minutes >= move.StartMinutes && x.Minutes < ends).ToList();
                if (injects.Any())
                    sb.Append(Table(["Time", "Inject", "Expected action", "Objective"],
                        injects.Select(x => new[]
                        {
                            "+" + Clock(x.Minutes),
                            Title(x, msel),
                            x.ExpectedAction ?? "",
                            x.Objective == null ? "" : objectives.TryGetValue(x.Objective, out var o) ? $"{o.Id} {o.Task}" : x.Objective
                        })));
            }

            if (draft.Objectives.Any())
            {
                sb.Append("<br><hr><br><h3>After-action review</h3>");
                sb.Append("<p>Close by reviewing each objective against its standard, and what the team would do differently.</p>");
                sb.Append(Table(["", "Objective", "Standard", "Competencies"],
                    draft.Objectives.Select(o => new[] { o.Id, o.Task, o.Standard, string.Join(", ", o.Competencies) })));
            }
            sb.Append("</section>");
            return GalleryArticleHtml.Prepare(sb.ToString(), null, GalleryArticleHtml.ArticleWidth);
        }

        // the column that best names a row, as the planner sees it in the draft review
        private static string Title(MselDraftInject inject, MselEntity msel)
        {
            var values = inject.Values ?? new Dictionary<string, string>();
            var named = msel.DataFields
                .Where(f => f.GalleryArticleParameter == nameof(GalleryArticleParameter.Name)
                    || new[] { "title", "message", "name" }.Contains(f.Name.Trim().ToLowerInvariant()))
                .Select(f => values.TryGetValue(f.Name, out var v) ? v : null)
                .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
            return OneLine(DataValueDraftService.StripHtml(named ?? values.Values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? inject.EventType));
        }

        private static string Table(string[] headings, IEnumerable<string[]> rows)
        {
            var sb = new StringBuilder("<table width=\"100%\" cellpadding=\"6\" cellspacing=\"0\" border=\"1\"><tbody><tr>");
            foreach (var heading in headings)
                sb.Append(string.Format(Th, E(heading)));
            sb.Append("</tr>");
            foreach (var row in rows)
                sb.Append("<tr>" + string.Concat(row.Select(cell => $"<td valign=\"top\">{E(cell)}</td>")) + "</tr>");
            return sb.Append("</tbody></table>").ToString();
        }

        private static string List(string tag, IEnumerable<string> items) =>
            $"<{tag}>{string.Concat(items.Select(i => $"<li>{E(i)}</li>"))}</{tag}>";

        private static string ScenarioTime(string time) =>
            MselDraftRules.ParseUtc(time) is DateTime t ? t.ToString("dd MMM yyyy HH:mm 'UTC'", System.Globalization.CultureInfo.InvariantCulture) : "";

        private static string Clock(int minutes) => $"{minutes / 60}:{minutes % 60:00}";

        private static string OneLine(string text) =>
            string.IsNullOrEmpty(text) || text.Length <= 160 ? text : text[..160] + "...";

        private static string E(string text) => WebUtility.HtmlEncode(text ?? "");
    }
}
