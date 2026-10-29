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
    public interface IProfileDraftService
    {
        /// <summary>Writes a team's or organization's profile, for a MSEL or for a draft that is not one yet</summary>
        Task<ProfileDraftResult> DraftAsync(ProfileDraftRequest request, ProfileDraftPermissions permissions, CancellationToken ct);
    }

    /// <summary>The system permissions that let the planner change a team (EditMsels), an organization (EditMsels), or draft a MSEL (CreateMsels)</summary>
    public record ProfileDraftPermissions(bool EditMsels, bool CreateMsels);

    /// <summary>
    /// Writes the profile participants and facilitators read about a team or an organization: who they are, their part
    /// in the scenario, their people and systems. The HTML is a team's or organization's description, which Blueprint
    /// saves through its sanitizer and shows on its pages, so it carries no font faces and its logo becomes a PNG.
    /// </summary>
    public class ProfileDraftService : IProfileDraftService
    {
        private const string ToolName = "submit_profile";
        public const string Team = "team";
        public const string Organization = "organization";

        public const string SystemPrompt =
@"You write the profile of one team or organization for a Crucible cyber exercise: the background page participants and
facilitators read. It must look like a polished briefing page, not a block of text.

Lay it out as:
- A header: a table with the organization's logo in the left cell and, in the right cell, its name as a heading, its short name and
  type or sector in small grey text, and its one-line summary.
- Headed sections, each a short paragraph or a list:
  - Overview: who they are, where, their size and mission.
  - Role in the exercise: for a participant team, what it is responsible for, the decisions it makes and who it reports to; for an
    organization, what it does in the scenario and how it appears in injects. For a threat actor, its motives and the activity
    defenders would observe, never how to carry it out.
  - Key people: three to five fictional people with their role and a contact address on a fictional domain.
  - Systems and assets (organizations) or Tools and resources (teams): what matters to the scenario.
  - Relationships: the other teams and organizations listed, and how this one works with or against them.
- A last line in small grey text: Exercise use only. All names and details are fictional.

HTML rules, because Blueprint keeps only these:
- Answer with an HTML fragment only, no html, head or body tags.
- No style attributes, no style or script tags, no event handlers, no classes, and no face attribute on font: it is removed.
- Lay out with tables: <table width=""680"" cellpadding=""10"" cellspacing=""0"" border=""0"">, with bgcolor on table, tr and td for
  colour bands and boxes, and <font color=""#1A3A5C"" size=""2""> for coloured or smaller text. Use h2 and h3 for headings.
- Allowed tags: div, p, h1 to h6, b, strong, i, em, u, br, hr, ul, ol, li, blockquote, table, tbody, tr, td, th, font, a, img, svg.
- Draw the logo as one inline <svg width=""96"" height=""96"" viewBox=""0 0 96 96""> with an aria-label naming it: the short name or
  initials in a simple shape in the organization's colours. Use only &amp; &lt; &gt; &quot; and numeric entities inside it.
- No external images, fonts or scripts. No real company logos, national insignia, flags or identifiable people.
- The page is shown in light and dark themes, so the profile is placed on white paper with dark text for you. Give every coloured
  band, box and heading cell its own bgcolor, and colour text only with dark colours on light cells or white on dark cells.

Content rules:
- Stay consistent with the scenario and the other teams and organizations. Use fictional people, domains and phone numbers.
- Keep what the planner already wrote unless asked to change it, and follow the planner's instruction.
- Put anything you had to assume in notes.
Answer only by calling the tool.";

        private readonly BlueprintContext _context;
        private readonly IAiModelClient _aiModelClient;
        private readonly AiOptions _aiOptions;
        private readonly ClaimsPrincipal _user;

        private readonly IAiUsageContext _usage;

        public ProfileDraftService(BlueprintContext context, IAiModelClient aiModelClient, AiOptions aiOptions, IPrincipal user, IAiUsageContext usage)
        {
            _usage = usage;
            _context = context;
            _aiModelClient = aiModelClient;
            _aiOptions = aiOptions;
            _user = user as ClaimsPrincipal;
        }

        public async Task<ProfileDraftResult> DraftAsync(ProfileDraftRequest request, ProfileDraftPermissions permissions, CancellationToken ct)
        {
            if (!_aiOptions.Enabled)
                throw new InvalidOperationException("AI drafting is not enabled on this server.");
            if (request == null || (request.Kind != Team && request.Kind != Organization))
                throw new BadRequestException("Say whether the profile is a team's or an organization's.");
            if (string.IsNullOrWhiteSpace(request.ShortName) && string.IsNullOrWhiteSpace(request.Name))
                throw new BadRequestException("A profile needs the team's or organization's name.");

            var context = request.MselId.HasValue
                ? await LoadMselContextAsync(request, permissions, ct)
                : DraftContext(request, permissions);
            _usage.Use("profile", request.MselId, request.Draft?.DraftId is Guid draftId && draftId != Guid.Empty ? draftId : null);
            var input = await _aiModelClient.InvokeToolAsync(SystemPrompt, BuildPrompt(request, context), ToolName,
                "Submit the profile's HTML.", Schema(), ct);

            var result = new ProfileDraftResult();
            result.Html = GalleryArticleHtml.Prepare(GetString(input, "html"), result.Notes, GalleryArticleHtml.ProfileWidth);
            if (string.IsNullOrWhiteSpace(result.Html))
                throw new InvalidOperationException("The model wrote no profile. Try again.");
            if (input.TryGetProperty("notes", out var notes) && notes.ValueKind == JsonValueKind.Array)
                result.Notes.AddRange(notes.EnumerateArray().Where(n => n.ValueKind == JsonValueKind.String).Select(n => n.GetString()));
            return result;
        }

        /// <summary>The scenario a profile is written in: the exercise, and the other teams and organizations</summary>
        public record ProfileContext(string Name, string Description, List<(string ShortName, string Name, string About)> Teams, List<(string ShortName, string Name, string About)> Organizations);

        // the same roles that may save the team or organization may have its profile written
        private async Task<ProfileContext> LoadMselContextAsync(ProfileDraftRequest request, ProfileDraftPermissions permissions, CancellationToken ct)
        {
            var msel = await _context.Msels.AsNoTracking()
                .Include(m => m.Teams)
                .Include(m => m.Organizations)
                .AsSplitQuery()
                .SingleOrDefaultAsync(m => m.Id == request.MselId, ct)
                ?? throw new EntityNotFoundException<MselEntity>("MSEL not found with ID=" + request.MselId);
            var userId = _user.GetId();
            var owner = await MselOwnerRequirement.IsMet(userId, msel.Id, _context);
            var allowed = permissions.EditMsels || owner ||
                (request.Kind == Organization && await MselEditorRequirement.IsMet(userId, msel.Id, _context));
            if (!allowed)
                throw new ForbiddenException();
            return new ProfileContext(
                msel.Name,
                DataValueDraftService.StripHtml(msel.Description),
                msel.Teams.OrderBy(t => t.ShortName).Select(t => (t.ShortName, t.Name, Shorten(DataValueDraftService.StripHtml(t.Description)))).ToList(),
                msel.Organizations.OrderBy(o => o.ShortName).Select(o => (o.ShortName, o.Name, o.Summary ?? Shorten(DataValueDraftService.StripHtml(o.Description)))).ToList());
        }

        private static ProfileContext DraftContext(ProfileDraftRequest request, ProfileDraftPermissions permissions)
        {
            if (!permissions.CreateMsels)
                throw new ForbiddenException();
            var draft = request.Draft ?? throw new BadRequestException("Give the MSEL or the draft the profile belongs to.");
            return new ProfileContext(
                draft.Name,
                draft.Description,
                (draft.Teams ?? new List<MselDraftTeam>()).Select(t => (t.ShortName, t.Name, t.Description)).ToList(),
                (draft.Organizations ?? new List<MselDraftOrganization>()).Select(o => (o.ShortName, o.Name, o.Description)).ToList());
        }

        public static string BuildPrompt(ProfileDraftRequest request, ProfileContext context)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Exercise: {context.Name}");
            if (!string.IsNullOrWhiteSpace(context.Description))
                sb.AppendLine($"Scenario: {context.Description}");
            sb.AppendLine();
            sb.AppendLine("Participant teams (short name: name, role):");
            foreach (var team in context.Teams)
                sb.AppendLine($"- {team.ShortName}: {team.Name}, {team.About}");
            sb.AppendLine("Organizations (short name: name, who they are):");
            foreach (var organization in context.Organizations)
                sb.AppendLine($"- {organization.ShortName}: {organization.Name}, {organization.About}");
            sb.AppendLine();
            sb.AppendLine($"Write the profile of this {(request.Kind == Team ? "participant team" : "organization")}:");
            sb.AppendLine($"Short name: {request.ShortName}");
            sb.AppendLine($"Name: {request.Name}");
            if (!string.IsNullOrWhiteSpace(request.Summary))
                sb.AppendLine($"Summary: {request.Summary}");
            var current = DataValueDraftService.StripHtml(request.Description);
            if (!string.IsNullOrWhiteSpace(current))
                sb.AppendLine($"What the planner has written so far: {current}");
            if (!string.IsNullOrWhiteSpace(request.Instruction))
                sb.AppendLine($"The planner's instruction: {request.Instruction.Trim()}");
            return sb.ToString();
        }

        private static string Shorten(string value) =>
            string.IsNullOrEmpty(value) || value.Length <= 200 ? value : value[..200] + "...";

        private static JsonObject Schema() => new()
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["html"] = new JsonObject { ["type"] = "string", ["description"] = "The profile as an HTML fragment that follows the HTML rules." },
                ["notes"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" }, ["description"] = "Assumptions the planner should check." }
            },
            ["required"] = new JsonArray("html")
        };

        private static string GetString(JsonElement element, string property) =>
            element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }
}
