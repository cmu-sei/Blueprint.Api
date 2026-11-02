// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Blueprint.Api.Data;
using Blueprint.Api.Data.Models;
using Blueprint.Api.Infrastructure.Authorization;
using Blueprint.Api.Infrastructure.Exceptions;
using Blueprint.Api.Infrastructure.Extensions;
using Blueprint.Api.Infrastructure.Options;
using Blueprint.Api.ViewModels;
using Microsoft.EntityFrameworkCore;

namespace Blueprint.Api.Services
{
    public interface IMoveDraftService
    {
        /// <summary>Asks the model for a move's situation briefing, written from the MSEL and its injects. Nothing is saved.</summary>
        Task<MoveDraftResult> DraftAsync(Guid mselId, MoveDraftRequest request, bool hasSystemPermission, CancellationToken ct);
    }

    public class MoveDraftService : IMoveDraftService
    {
        private const string ToolName = "submit_situation";
        private const int ExcerptLength = 300;
        private const int InjectsShown = 60;
        private readonly BlueprintContext _context;
        private readonly IAiModelClient _aiModelClient;
        private readonly AiOptions _aiOptions;
        private readonly ClaimsPrincipal _user;
        private readonly IAiUsageContext _usage;

        private const string SystemPrompt =
@"You write the situation briefing of one move of a Master Scenario Event List (MSEL) for a Crucible cyber exercise in Blueprint:
what participants are told when the move opens, on its scenario clock. It shows in Blueprint, CITE and Player.
Rules:
- Write it as a short briefing in HTML: an h4 title, a paragraph on what has happened, a ul of what participants now know, and,
  where it helps, a two-column table of key facts (time, location, affected systems, who has been told). Use b, i, ul, ol, li, h4,
  p and table with border=""1"" cellpadding=""6"" cellspacing=""0"" only; no colors, images or style attributes.
- It follows from the moves before it and what their injects told participants, at the move's scenario time. It reveals nothing
  the injects of this move or later moves bring: those are listed so you keep clear of them, not to retell them.
- Stay consistent with the MSEL: the same organizations, people, places, times and numbers. Use fictional ones unless the MSEL
  names real ones.
- With a current briefing, change what the planner asks and keep the rest. Without one, write it from the move's title and place.
- Put any assumption you had to make, or conflict you found, in notes, not in the briefing.
Answer only by calling the tool.";

        public MoveDraftService(BlueprintContext context, IAiModelClient aiModelClient, AiOptions aiOptions, IPrincipal user, IAiUsageContext usage)
        {
            _context = context;
            _aiModelClient = aiModelClient;
            _aiOptions = aiOptions;
            _user = user as ClaimsPrincipal;
            _usage = usage;
        }

        public async Task<MoveDraftResult> DraftAsync(Guid mselId, MoveDraftRequest request, bool hasSystemPermission, CancellationToken ct)
        {
            if (!_aiOptions.Enabled)
                throw new InvalidOperationException("AI drafting is not enabled on this server.");
            // the roles that may save the move may have its briefing written
            var userId = _user.GetId();
            if (!hasSystemPermission && !await MselOwnerRequirement.IsMet(userId, mselId, _context) && !await MoveEditorRequirement.IsMet(userId, mselId, _context))
                throw new ForbiddenException();

            var msel = await _context.Msels
                .AsNoTracking()
                .Include(m => m.Moves)
                .Include(m => m.Teams)
                .Include(m => m.Organizations)
                .Include(m => m.DataFields)
                .Include(m => m.ScenarioEvents).ThenInclude(e => e.DataValues)
                .AsSplitQuery()
                .SingleOrDefaultAsync(m => m.Id == mselId, ct)
                ?? throw new EntityNotFoundException<MselEntity>("MSEL not found with ID=" + mselId);

            _usage.Use("move", mselId);
            var input = await _aiModelClient.InvokeToolAsync(SystemPrompt, BuildPrompt(msel, request), ToolName,
                "Submit the move's situation briefing for the planner to review.", Schema(), ct);
            var result = input.Deserialize<MoveDraftResult>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new MoveDraftResult();
            result.Notes = (result.Notes ?? new List<string>()).Where(n => !string.IsNullOrWhiteSpace(n)).ToList();
            result.Html = MselDraftRules.SituationHtml(result.Html);
            if (string.IsNullOrWhiteSpace(DataValueDraftService.StripHtml(result.Html)))
                throw new InvalidOperationException("The model returned an empty briefing. Try again or add an instruction.");
            return result;
        }

        private static JsonObject Schema() => new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["html"] = new JsonObject { ["type"] = "string", ["description"] = "The situation briefing, in HTML" },
                ["notes"] = new JsonObject
                {
                    ["type"] = "array",
                    ["description"] = "Assumptions the planner should check, and conflicts with the MSEL",
                    ["items"] = new JsonObject { ["type"] = "string" }
                }
            },
            ["required"] = new JsonArray("html")
        };

        /// <summary>What a briefing is written from: the MSEL, the moves, the injects before the move and those in and after it</summary>
        public static string BuildPrompt(MselEntity msel, MoveDraftRequest request)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Exercise: {msel.Name}");
            if (!string.IsNullOrWhiteSpace(msel.Description))
                sb.AppendLine($"Description: {Excerpt(msel.Description, 2000)}");
            var draft = MselBuildState.Deserialize(msel.AiDraft)?.Draft;
            if (draft?.Scenario != null)
            {
                sb.AppendLine($"Problem: {draft.Scenario.Problem}");
                sb.AppendLine($"Premise: {draft.Scenario.Premise}");
                if (!string.IsNullOrWhiteSpace(draft.Scenario.EscalationCeiling))
                    sb.AppendLine($"Escalation ceiling: {draft.Scenario.EscalationCeiling}");
            }
            if (msel.Teams.Any())
                sb.AppendLine($"Participant teams: {string.Join("; ", msel.Teams.OrderBy(t => t.ShortName).Select(t => $"{t.ShortName} ({t.Name})"))}");
            if (msel.Organizations.Any())
                sb.AppendLine($"Organizations: {string.Join("; ", msel.Organizations.OrderBy(o => o.ShortName).Select(o => $"{o.ShortName} ({o.Name})"))}");

            // this move as the dialog holds it, in place of the stored one
            var moves = msel.Moves.Where(m => m.Id != request.MoveId)
                .Select(m => (m.MoveNumber, m.DeltaSeconds, m.SituationTime, m.Description, m.SituationDescription, This: false))
                .ToList();
            var stored = msel.Moves.FirstOrDefault(m => m.Id == request.MoveId);
            var start = stored?.DeltaSeconds ?? moves.Where(m => m.MoveNumber < request.MoveNumber).Select(m => m.DeltaSeconds).DefaultIfEmpty(0).Max();
            moves.Add((request.MoveNumber, start, request.SituationTime, request.Description, request.SituationDescription, true));
            moves = moves.OrderBy(m => m.DeltaSeconds).ThenBy(m => m.MoveNumber).ToList();
            var next = moves.SkipWhile(m => !m.This).Skip(1).Select(m => (int?)m.DeltaSeconds).FirstOrDefault();

            sb.AppendLine().AppendLine("Moves (number, exercise clock, scenario time: title, situation):");
            foreach (var move in moves)
                sb.AppendLine($"- {(move.This ? "THIS MOVE " : "")}{move.MoveNumber}, {Clock(move.DeltaSeconds)}" +
                    (move.SituationTime.HasValue ? $", {move.SituationTime.Value.ToUniversalTime():yyyy-MM-dd HH:mm} UTC" : "") +
                    $": {move.Description}" + (move.This ? "" : $". {Excerpt(move.SituationDescription, ExcerptLength)}"));

            var titled = msel.DataFields.Where(f => f.GalleryArticleParameter == "Name" || new[] { "title", "name", "message" }.Contains(f.Name.Trim().ToLowerInvariant()))
                .Select(f => f.Id).ToHashSet();
            var summary = msel.DataFields.Where(f => f.GalleryArticleParameter == "Summary" || f.Name.Trim().Equals("Summary", StringComparison.OrdinalIgnoreCase))
                .Select(f => f.Id).ToHashSet();
            string Line(ScenarioEventEntity e)
            {
                var title = e.DataValues.FirstOrDefault(v => titled.Contains(v.DataFieldId) && !string.IsNullOrWhiteSpace(v.Value))?.Value;
                var gist = e.DataValues.FirstOrDefault(v => summary.Contains(v.DataFieldId) && !string.IsNullOrWhiteSpace(v.Value))?.Value;
                return $"- {Clock(e.DeltaSeconds)} {e.ScenarioEventType}: {Excerpt(title, 120)}" + (gist == null ? "" : $". {Excerpt(gist, 200)}");
            }
            var events = msel.ScenarioEvents.OrderBy(e => e.DeltaSeconds).ThenBy(e => e.GroupOrder).ToList();
            var before = events.Where(e => e.DeltaSeconds < start).TakeLast(InjectsShown).ToList();
            if (before.Any())
            {
                sb.AppendLine().AppendLine("Injects before this move, which participants have had:");
                foreach (var e in before)
                    sb.AppendLine(Line(e));
            }
            var during = events.Where(e => e.DeltaSeconds >= start && (next == null || e.DeltaSeconds < next)).Take(InjectsShown).ToList();
            if (during.Any())
            {
                sb.AppendLine().AppendLine("Injects of this move, which the briefing must not give away:");
                foreach (var e in during)
                    sb.AppendLine(Line(e));
            }

            sb.AppendLine().AppendLine($"This move: {request.MoveNumber}, {request.Description?.Trim()}");
            if (!string.IsNullOrWhiteSpace(DataValueDraftService.StripHtml(request.SituationDescription)))
                sb.AppendLine("Its current briefing:").AppendLine(request.SituationDescription.Trim());
            else
                sb.AppendLine("It has no briefing yet: write it.");
            if (!string.IsNullOrWhiteSpace(request.Instruction))
                sb.AppendLine().AppendLine("The planner's instruction:").AppendLine(request.Instruction.Trim());
            return sb.ToString();
        }

        private static string Clock(int seconds) => $"{seconds / 3600}:{seconds / 60 % 60:00}";

        private static string Excerpt(string html, int length)
        {
            var text = DataValueDraftService.StripHtml(html);
            return string.IsNullOrEmpty(text) || text.Length <= length ? text : text[..length] + "...";
        }
    }
}
