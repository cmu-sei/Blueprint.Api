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
    public interface IPageDraftService
    {
        /// <summary>Asks the model for a MSEL page, written from the MSEL and the planner's instruction. Nothing is saved.</summary>
        Task<PageDraftResult> DraftAsync(Guid mselId, PageDraftRequest request, bool hasSystemPermission, CancellationToken ct);
    }

    public class PageDraftService : IPageDraftService
    {
        private const string ToolName = "submit_page";
        // how much of the MSEL goes into the prompt
        private const int PageLength = 20000;
        private const int ExcerptLength = 400;
        private readonly BlueprintContext _context;
        private readonly IAiModelClient _aiModelClient;
        private readonly AiOptions _aiOptions;
        private readonly ClaimsPrincipal _user;
        private readonly IAiUsageContext _usage;

        private const string SystemPrompt =
@"You write one page of a Master Scenario Event List (MSEL) for a Crucible cyber exercise in Blueprint: a read-in, an exercise
overview, a facilitator guide, profiles, or any other page the planner keeps with the MSEL. Participants or controllers read it
in Blueprint, Player and the printed playbook.
Rules:
- Write from the MSEL below: its scenario, objectives, moves and their scenario times, teams and organizations. Stay consistent with
  them: the same names, places, times and numbers. A page participants see reveals nothing they should only learn from an inject.
- With a current page, change what the planner asks and keep the rest as it is: its headings, order, tables and wording. Without
  one, write the whole page for its name.
- Write HTML: section, h2, h3, h4, p, ul, ol, li, strong, em, table with border=""1"" cellpadding=""6"" cellspacing=""0"", tr, th
  and td only. No colors, images, scripts or style attributes; the page shows in light and dark themes.
- Use fictional organizations, people, domains and IP addresses unless the MSEL names real ones.
- Put any assumption you had to make, or conflict you found, in notes, not on the page.
Answer only by calling the tool.";

        public PageDraftService(BlueprintContext context, IAiModelClient aiModelClient, AiOptions aiOptions, IPrincipal user, IAiUsageContext usage)
        {
            _context = context;
            _aiModelClient = aiModelClient;
            _aiOptions = aiOptions;
            _user = user as ClaimsPrincipal;
            _usage = usage;
        }

        public async Task<PageDraftResult> DraftAsync(Guid mselId, PageDraftRequest request, bool hasSystemPermission, CancellationToken ct)
        {
            if (!_aiOptions.Enabled)
                throw new InvalidOperationException("AI drafting is not enabled on this server.");
            // the roles that may save the page may have it written
            if (!hasSystemPermission && !await MselOwnerRequirement.IsMet(_user.GetId(), mselId, _context))
                throw new ForbiddenException();
            if (string.IsNullOrWhiteSpace(request?.Name) && string.IsNullOrWhiteSpace(request?.Instruction))
                throw new BadRequestException("Name the page, or say what to write.");

            var msel = await _context.Msels
                .AsNoTracking()
                .Include(m => m.Moves)
                .Include(m => m.Teams)
                .Include(m => m.Organizations)
                .Include(m => m.Pages)
                .AsSplitQuery()
                .SingleOrDefaultAsync(m => m.Id == mselId, ct)
                ?? throw new EntityNotFoundException<MselEntity>("MSEL not found with ID=" + mselId);

            _usage.Use("page", mselId);
            var input = await _aiModelClient.InvokeToolAsync(SystemPrompt, BuildPrompt(msel, request), ToolName,
                "Submit the page for the planner to review.", Schema(), ct);
            var result = input.Deserialize<PageDraftResult>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new PageDraftResult();
            result.Notes = (result.Notes ?? new List<string>()).Where(n => !string.IsNullOrWhiteSpace(n)).ToList();
            result.Html = GalleryArticleHtml.Prepare(result.Html, result.Notes);
            if (string.IsNullOrWhiteSpace(DataValueDraftService.StripHtml(result.Html)))
                throw new InvalidOperationException("The model returned an empty page. Try again or add an instruction.");
            return result;
        }

        private static JsonObject Schema() => new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["html"] = new JsonObject { ["type"] = "string", ["description"] = "The whole page, in HTML" },
                ["notes"] = new JsonObject
                {
                    ["type"] = "array",
                    ["description"] = "Assumptions the planner should check, and conflicts with the MSEL",
                    ["items"] = new JsonObject { ["type"] = "string" }
                }
            },
            ["required"] = new JsonArray("html")
        };

        /// <summary>What a page is written from: the MSEL, its design where it was drafted with AI, the other pages, and the page itself</summary>
        public static string BuildPrompt(MselEntity msel, PageDraftRequest request)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Exercise: {msel.Name}");
            if (!string.IsNullOrWhiteSpace(msel.Description))
                sb.AppendLine($"Description: {Excerpt(msel.Description, 2000)}");
            // the design the MSEL was drafted from, where it was
            var draft = MselBuildState.Deserialize(msel.AiDraft)?.Draft;
            if (draft?.Scenario != null)
            {
                sb.AppendLine($"Problem: {draft.Scenario.Problem}");
                sb.AppendLine($"Premise: {draft.Scenario.Premise}");
                if (!string.IsNullOrWhiteSpace(draft.Scenario.Environment))
                    sb.AppendLine($"Environment: {draft.Scenario.Environment}");
                if (!string.IsNullOrWhiteSpace(draft.Scenario.EscalationCeiling))
                    sb.AppendLine($"Escalation ceiling: {draft.Scenario.EscalationCeiling}");
            }
            if (draft?.Objectives?.Count > 0)
            {
                sb.AppendLine("Objectives:");
                foreach (var objective in draft.Objectives)
                    sb.AppendLine($"- {objective.Id}: {objective.Task}. Condition: {objective.Condition}. Standard: {objective.Standard}");
            }

            var moves = msel.Moves.OrderBy(m => m.DeltaSeconds).ThenBy(m => m.MoveNumber).ToList();
            if (moves.Any())
            {
                sb.AppendLine().AppendLine("Moves (number, exercise clock, scenario time: title, situation):");
                foreach (var move in moves)
                    sb.AppendLine($"- {move.MoveNumber}, {move.DeltaSeconds / 3600}:{move.DeltaSeconds / 60 % 60:00}" +
                        (move.SituationTime.HasValue ? $", {move.SituationTime.Value.ToUniversalTime():yyyy-MM-dd HH:mm} UTC" : "") +
                        $": {move.Description}. {Excerpt(move.SituationDescription, ExcerptLength)}");
            }
            if (msel.Teams.Any())
            {
                sb.AppendLine().AppendLine("Teams (short name: name, who they are):");
                foreach (var team in msel.Teams.OrderBy(t => t.ShortName))
                    sb.AppendLine($"- {team.ShortName}: {team.Name}. {Excerpt(team.Description, ExcerptLength)}");
            }
            if (msel.Organizations.Any())
            {
                sb.AppendLine().AppendLine("Organizations (short name: name, who they are):");
                foreach (var organization in msel.Organizations.OrderBy(o => o.ShortName))
                    sb.AppendLine($"- {organization.ShortName}: {organization.Name}. {organization.Summary ?? Excerpt(organization.Description, ExcerptLength)}");
            }
            var others = msel.Pages.Where(p => p.Id != request.PageId).Select(p => p.Name).ToList();
            if (others.Any())
                sb.AppendLine().AppendLine($"The MSEL's other pages: {string.Join("; ", others)}.");

            sb.AppendLine().AppendLine($"The page: {request.Name?.Trim()}");
            if (!string.IsNullOrWhiteSpace(DataValueDraftService.StripHtml(request.Content)))
            {
                var content = request.Content.Trim();
                sb.AppendLine("Its current content:").AppendLine(content.Length <= PageLength ? content : content[..PageLength] + "...");
            }
            else
                sb.AppendLine("It is empty: write it from nothing.");
            if (!string.IsNullOrWhiteSpace(request.Instruction))
                sb.AppendLine().AppendLine("The planner's instruction:").AppendLine(request.Instruction.Trim());
            return sb.ToString();
        }

        private static string Excerpt(string html, int length)
        {
            var text = DataValueDraftService.StripHtml(html);
            return string.IsNullOrEmpty(text) || text.Length <= length ? text : text[..length] + "...";
        }
    }
}
