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
    public interface IMselAssistantService
    {
        Task<MselAssistResult> AskAsync(Guid mselId, MselAssistRequest request, bool hasSystemPermission, CancellationToken ct);
        Task<MselAssistApplyResult> ApplyAsync(Guid mselId, MselAssistApply apply, MselAssistPermissions permissions, CancellationToken ct);
    }

    /// <summary>The system permissions the change endpoints check, so applying a change is allowed exactly when making it by hand is</summary>
    public record MselAssistPermissions(bool EditMsels, bool ManageMsels, bool ManageOrganizations, bool ManageGalleryCards);

    public class MselAssistantService : IMselAssistantService
    {
        private const string ToolName = "propose_msel_changes";
        // the conversation is resent each turn; older turns are dropped first
        private const int MaxHistoryMessages = 20;
        private const int MaxInjectsShown = 200;

        private const string SystemPrompt =
@"You help an exercise planner change an existing Master Scenario Event List (MSEL) in Blueprint.
A MSEL is organized as Move > Group > Inject. Teams are the participant groups (and EXCON, exercise control). Organizations are the
scenario's actors: the agencies, companies, media outlets and threat actors that send or appear in injects. Gallery cards group articles
on the participants' Gallery wall; a card's move and inject numbers say when it first appears. Competencies are framework work roles
and tasks (DCWF or NICE) linked to the MSEL as a whole or to a team.
You can propose these changes: add or edit a team, add or edit an organization, add or edit a Gallery card, and add or remove a
competency on the MSEL or on a team.
Rules:
- Answer only by calling the tool. Put a short reply to the planner in reply: what you propose and why, in plain words.
- Change only what the planner asked for, plus what that needs (a new team named in the request needs to exist before a competency is put on it).
- If the request is ambiguous in a way that changes the result, ask one short question and propose no changes.
- If the planner asks for something you cannot change yet (injects, moves, data fields, integrations, users, pages), say so in reply.
- Refer to existing teams and organizations by their short names and to cards by their names, exactly as listed.
- Short names are short, upper case and unique. Team and card descriptions are plain text; organization descriptions are HTML paragraphs.
- Use competency ID numbers exactly as listed. Prefer a work role for a team's role in the exercise and tasks for specific actions.
- Use fictional organizations and people unless the planner names real ones.";

        private readonly BlueprintContext _context;
        private readonly IAiModelClient _aiModelClient;
        private readonly AiOptions _aiOptions;
        private readonly ITeamService _teamService;
        private readonly IOrganizationService _organizationService;
        private readonly ICardService _cardService;
        private readonly IMselCompetencyService _mselCompetencyService;
        private readonly ITeamCompetencyService _teamCompetencyService;
        private readonly ClaimsPrincipal _user;

        public MselAssistantService(
            BlueprintContext context,
            IAiModelClient aiModelClient,
            AiOptions aiOptions,
            ITeamService teamService,
            IOrganizationService organizationService,
            ICardService cardService,
            IMselCompetencyService mselCompetencyService,
            ITeamCompetencyService teamCompetencyService,
            IPrincipal user)
        {
            _context = context;
            _aiModelClient = aiModelClient;
            _aiOptions = aiOptions;
            _teamService = teamService;
            _organizationService = organizationService;
            _cardService = cardService;
            _mselCompetencyService = mselCompetencyService;
            _teamCompetencyService = teamCompetencyService;
            _user = user as ClaimsPrincipal;
        }

        public async Task<MselAssistResult> AskAsync(Guid mselId, MselAssistRequest request, bool hasSystemPermission, CancellationToken ct)
        {
            if (!_aiOptions.Enabled)
                throw new InvalidOperationException("AI drafting is not enabled on this server.");
            if (string.IsNullOrWhiteSpace(request?.Prompt))
                throw new ArgumentException("Say what to change.");
            await AuthorizeAsync(mselId, hasSystemPermission, ct);

            var state = await LoadStateAsync(mselId, ct);
            var workRoles = await LoadWorkRolesAsync(ct);
            var input = await _aiModelClient.InvokeToolAsync(SystemPrompt, BuildPrompt(request, state, workRoles), ToolName,
                "Propose changes to the MSEL for the planner to review, or ask one question.", BuildSchema(), ct);

            var proposed = input.TryGetProperty("changes", out var changes) && changes.ValueKind == JsonValueKind.Array
                ? changes.Deserialize<List<MselChange>>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new List<MselChange>()
                : new List<MselChange>();
            var result = new MselAssistResult
            {
                Reply = GetString(input, "reply"),
                Question = GetString(input, "question")
            };
            await AddCompetenciesAsync(state, proposed, ct);
            result.Changes = MselAssistRules.Review(state, proposed, result.Warnings);
            if (!string.IsNullOrWhiteSpace(result.Question))
                result.Changes.Clear();
            return result;
        }

        public async Task<MselAssistApplyResult> ApplyAsync(Guid mselId, MselAssistApply apply, MselAssistPermissions permissions, CancellationToken ct)
        {
            await AuthorizeAsync(mselId, permissions.EditMsels, ct);
            var result = new MselAssistApplyResult();
            var changes = apply?.Changes ?? new List<MselChange>();

            // checked again against the MSEL as it is now, as it may have changed since the proposal
            var state = await LoadStateAsync(mselId, ct);
            await AddCompetenciesAsync(state, changes, ct);
            var dropped = new List<string>();
            var valid = MselAssistRules.Review(state, changes, dropped);
            result.Results.AddRange(dropped.Select(d => new MselChangeResult { Label = d, Applied = false, Error = "No longer possible" }));

            foreach (var change in valid)
            {
                var outcome = new MselChangeResult { Label = change.Label };
                try
                {
                    await ApplyChangeAsync(mselId, change, state, permissions, ct);
                    outcome.Applied = true;
                }
                catch (Exception ex) when (ex is ForbiddenException or EntityNotFoundException<TeamEntity> or ArgumentException or InvalidOperationException or DbUpdateException)
                {
                    outcome.Error = ex is ForbiddenException ? "You do not have permission to make this change." : ex.Message;
                }
                result.Results.Add(outcome);
            }
            return result;
        }

        private async Task ApplyChangeAsync(Guid mselId, MselChange change, MselAssistState state, MselAssistPermissions permissions, CancellationToken ct)
        {
            switch (change.Op)
            {
                case MselAssistRules.TeamAdd:
                    await _teamService.CreateAsync(new TeamCreate
                    {
                        Id = Guid.NewGuid(),
                        MselId = mselId,
                        ShortName = change.ShortName,
                        Name = change.Name,
                        Description = change.Description
                    }, permissions.EditMsels, ct);
                    break;
                case MselAssistRules.TeamUpdate:
                {
                    var team = await _context.Teams.AsNoTracking().SingleAsync(t => t.MselId == mselId && t.ShortName == change.Target, ct);
                    await _teamService.UpdateAsync(team.Id, new TeamEdit
                    {
                        ShortName = change.ShortName ?? team.ShortName,
                        Name = change.Name ?? team.Name,
                        Description = change.Description ?? team.Description,
                        CiteTeamTypeId = team.CiteTeamTypeId,
                        Email = team.Email,
                        canTeamLeaderInvite = team.canTeamLeaderInvite,
                        canTeamMemberInvite = team.canTeamMemberInvite,
                        CasterTeamVariable = team.CasterTeamVariable
                    }, permissions.EditMsels, ct);
                    break;
                }
                case MselAssistRules.OrganizationAdd:
                    await _organizationService.CreateAsync(new OrganizationCreate
                    {
                        Id = Guid.NewGuid(),
                        MselId = mselId,
                        ShortName = change.ShortName,
                        Name = change.Name,
                        Summary = change.Summary,
                        Description = change.Description
                    }, permissions.EditMsels, permissions.ManageOrganizations, ct);
                    break;
                case MselAssistRules.OrganizationUpdate:
                {
                    var organization = await _context.Organizations.AsNoTracking().SingleAsync(o => o.MselId == mselId && o.ShortName == change.Target, ct);
                    await _organizationService.UpdateAsync(organization.Id, new OrganizationEdit
                    {
                        ShortName = change.ShortName ?? organization.ShortName,
                        Name = change.Name ?? organization.Name,
                        Summary = change.Summary ?? organization.Summary,
                        Description = change.Description ?? organization.Description,
                        Email = organization.Email
                    }, permissions.EditMsels, permissions.ManageOrganizations, ct);
                    break;
                }
                case MselAssistRules.CardAdd:
                    await _cardService.CreateAsync(new CardCreate
                    {
                        Id = Guid.NewGuid(),
                        MselId = mselId,
                        Name = change.Name,
                        Description = change.Description,
                        Move = change.Move ?? 0,
                        Inject = change.Inject ?? 0
                    }, permissions.EditMsels, permissions.ManageGalleryCards, ct);
                    break;
                case MselAssistRules.CardUpdate:
                {
                    var card = await _context.Cards.AsNoTracking().SingleAsync(c => c.MselId == mselId && c.Name == change.Target, ct);
                    await _cardService.UpdateAsync(card.Id, new CardEdit
                    {
                        Name = change.Name ?? card.Name,
                        Description = change.Description ?? card.Description,
                        Move = change.Move ?? card.Move,
                        Inject = change.Inject ?? card.Inject,
                        GalleryId = card.GalleryId
                    }, permissions.EditMsels, permissions.ManageGalleryCards, ct);
                    break;
                }
                case MselAssistRules.CompetencyAdd:
                case MselAssistRules.CompetencyRemove:
                {
                    var competencyId = state.Competencies[change.IdNumber.ToLowerInvariant()].Id;
                    var add = change.Op == MselAssistRules.CompetencyAdd;
                    if (string.IsNullOrEmpty(change.Target))
                    {
                        if (add)
                            await _mselCompetencyService.CreateAsync(new ViewModels.MselCompetency { MselId = mselId, CompetencyId = competencyId }, permissions.ManageMsels, ct);
                        else
                            await _mselCompetencyService.DeleteByIdsAsync(mselId, competencyId, permissions.ManageMsels, ct);
                        break;
                    }
                    // the team may have been added earlier in this same apply
                    var teamId = await _context.Teams.Where(t => t.MselId == mselId && t.ShortName == change.Target).Select(t => t.Id).SingleAsync(ct);
                    if (add)
                        await _teamCompetencyService.CreateAsync(new ViewModels.TeamCompetency { TeamId = teamId, CompetencyId = competencyId }, permissions.ManageMsels, ct);
                    else
                        await _teamCompetencyService.DeleteByIdsAsync(teamId, competencyId, permissions.ManageMsels, ct);
                    break;
                }
            }
        }

        // the same roles that may edit the MSEL may ask for changes to it
        private async Task AuthorizeAsync(Guid mselId, bool hasSystemPermission, CancellationToken ct)
        {
            if (!await _context.Msels.AnyAsync(m => m.Id == mselId, ct))
                throw new EntityNotFoundException<MselEntity>("MSEL not found with ID=" + mselId);
            var userId = _user.GetId();
            if (!hasSystemPermission &&
                !await MselOwnerRequirement.IsMet(userId, mselId, _context) &&
                !await MselEditorRequirement.IsMet(userId, mselId, _context))
                throw new ForbiddenException();
        }

        private async Task<MselAssistState> LoadStateAsync(Guid mselId, CancellationToken ct)
        {
            var msel = await _context.Msels
                .AsNoTracking()
                .Include(m => m.Teams).ThenInclude(t => t.TeamCompetencies).ThenInclude(tc => tc.Competency)
                .Include(m => m.Organizations)
                .Include(m => m.Cards)
                .Include(m => m.Moves)
                .Include(m => m.MselCompetencies).ThenInclude(mc => mc.Competency)
                .Include(m => m.DataFields)
                .Include(m => m.ScenarioEvents).ThenInclude(e => e.DataValues)
                .AsSplitQuery()
                .SingleAsync(m => m.Id == mselId, ct);
            var state = new MselAssistState(msel);
            foreach (var competency in msel.MselCompetencies.Select(mc => mc.Competency)
                .Concat(msel.Teams.SelectMany(t => t.TeamCompetencies).Select(tc => tc.Competency)))
                state.Competencies.TryAdd(competency.IdNumber.ToLowerInvariant(), competency);
            return state;
        }

        // a work role sits one level under a framework's top-level categories
        private async Task<List<CompetencyEntity>> LoadWorkRolesAsync(CancellationToken ct) =>
            await _context.Competencies
                .AsNoTracking()
                .Include(c => c.CompetencyFramework)
                .Where(c => c.Parent != null && c.Parent.ParentId == null)
                .OrderBy(c => c.CompetencyFramework.Name).ThenBy(c => c.SortOrder).ThenBy(c => c.IdNumber)
                .ToListAsync(ct);

        // looks up the competencies the changes name, preferring the frameworks the MSEL already uses
        private async Task AddCompetenciesAsync(MselAssistState state, List<MselChange> changes, CancellationToken ct)
        {
            var wanted = changes.Where(c => !string.IsNullOrWhiteSpace(c.IdNumber)).Select(c => c.IdNumber.Trim().ToLower()).Distinct().ToList();
            if (wanted.Count == 0)
                return;
            var frameworks = state.Competencies.Values.Select(c => c.CompetencyFrameworkId).ToHashSet();
            var found = await _context.Competencies.AsNoTracking()
                .Where(c => wanted.Contains(c.IdNumber.ToLower()))
                .ToListAsync(ct);
            foreach (var match in found.GroupBy(c => c.IdNumber.ToLowerInvariant()))
                state.Competencies.TryAdd(match.Key, match.OrderByDescending(c => frameworks.Contains(c.CompetencyFrameworkId)).First());
        }

        private static string BuildPrompt(MselAssistRequest request, MselAssistState state, List<CompetencyEntity> workRoles)
        {
            var msel = state.Msel;
            var sb = new StringBuilder();
            sb.AppendLine($"MSEL: {msel.Name}");
            if (!string.IsNullOrWhiteSpace(msel.Description))
                sb.AppendLine($"Description: {DataValueDraftService.StripHtml(msel.Description)}");
            sb.AppendLine($"Integrations: {string.Join(", ", new[] { msel.UseGallery ? "Gallery" : null, msel.UseCite ? "CITE" : null, msel.UsePlayer ? "Player" : null, msel.UseSteamfitter ? "Steamfitter" : null }.Where(x => x != null))}");

            sb.AppendLine().AppendLine("Moves:");
            foreach (var move in msel.Moves.OrderBy(m => m.MoveNumber))
                sb.AppendLine($"- Move {move.MoveNumber} at minute {move.DeltaSeconds / 60}: {move.Description}");

            sb.AppendLine().AppendLine("Teams:");
            foreach (var team in msel.Teams.OrderBy(t => t.ShortName))
            {
                var competencies = team.TeamCompetencies.Select(tc => tc.Competency.IdNumber).OrderBy(x => x);
                sb.AppendLine($"- {team.ShortName} ({team.Name}): {DataValueDraftService.StripHtml(team.Description)} Competencies: {string.Join(", ", competencies)}");
            }

            sb.AppendLine().AppendLine("Organizations:");
            foreach (var organization in msel.Organizations.OrderBy(o => o.ShortName))
                sb.AppendLine($"- {organization.ShortName} ({organization.Name}): {organization.Summary ?? DataValueDraftService.StripHtml(organization.Description)}");

            sb.AppendLine().AppendLine("Gallery cards:");
            foreach (var card in msel.Cards.OrderBy(c => c.Move).ThenBy(c => c.Inject))
                sb.AppendLine($"- {card.Name} (move {card.Move}, inject {card.Inject}): {card.Description}");

            sb.AppendLine().AppendLine("MSEL competencies: " + string.Join(", ", msel.MselCompetencies.Select(mc => $"{mc.Competency.IdNumber} {mc.Competency.ShortName}")));

            // the injects give the model the story; only their first text field is sent
            var titleField = msel.DataFields
                .Where(f => f.DataType == Data.Enumerations.DataFieldType.String && !f.IsChosenFromList)
                .OrderBy(f => f.Name.Equals("Title", StringComparison.OrdinalIgnoreCase) ? 0 : 1).ThenBy(f => f.DisplayOrder)
                .FirstOrDefault();
            sb.AppendLine().AppendLine("Injects:");
            foreach (var inject in msel.ScenarioEvents.OrderBy(e => e.DeltaSeconds).Take(MaxInjectsShown))
            {
                var title = titleField == null ? null : inject.DataValues.FirstOrDefault(v => v.DataFieldId == titleField.Id)?.Value;
                sb.AppendLine($"- minute {inject.DeltaSeconds / 60}: {DataValueDraftService.StripHtml(title)}");
            }

            sb.AppendLine().AppendLine("Work roles that can be added (ID number, name, framework):");
            foreach (var role in workRoles)
                sb.AppendLine($"- {role.IdNumber} {role.ShortName} ({role.CompetencyFramework.Name})");

            var history = (request.History ?? new List<MselAssistMessage>()).TakeLast(MaxHistoryMessages).ToList();
            if (history.Count > 0)
            {
                sb.AppendLine().AppendLine("Conversation so far:");
                foreach (var message in history)
                    sb.AppendLine($"{(message.Role == "assistant" ? "Assistant" : "Planner")}: {message.Text}");
            }
            sb.AppendLine().AppendLine("Planner's request:").AppendLine(request.Prompt.Trim());
            return sb.ToString();
        }

        private static JsonObject BuildSchema() => new()
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["reply"] = new JsonObject { ["type"] = "string", ["description"] = "A short reply to the planner." },
                ["question"] = new JsonObject { ["type"] = "string", ["description"] = "One question to ask instead of proposing changes; leave empty when proposing changes." },
                ["changes"] = new JsonObject
                {
                    ["type"] = "array",
                    ["items"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["op"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray(MselAssistRules.Ops.Select(o => (JsonNode)o).ToArray()) },
                            ["target"] = new JsonObject { ["type"] = "string", ["description"] = "For an update, the short name of the team or organization, or the name of the card. For a competency, the team's short name, or empty for the MSEL." },
                            ["shortName"] = new JsonObject { ["type"] = "string", ["description"] = "A team's or organization's short name, new or changed." },
                            ["name"] = new JsonObject { ["type"] = "string" },
                            ["description"] = new JsonObject { ["type"] = "string" },
                            ["summary"] = new JsonObject { ["type"] = "string", ["description"] = "An organization's one-line summary." },
                            ["move"] = new JsonObject { ["type"] = "integer", ["description"] = "A card's move." },
                            ["inject"] = new JsonObject { ["type"] = "integer", ["description"] = "A card's inject number within its move." },
                            ["idNumber"] = new JsonObject { ["type"] = "string", ["description"] = "A competency's ID number." }
                        },
                        ["required"] = new JsonArray("op")
                    }
                }
            },
            ["required"] = new JsonArray("reply")
        };

        private static string GetString(JsonElement element, string property) =>
            element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
                ? value.GetString().Trim()
                : null;
    }

    /// <summary>What the review checks changes against: the MSEL as loaded, and the competencies the changes may name</summary>
    public class MselAssistState
    {
        public MselAssistState(MselEntity msel) => Msel = msel;
        public MselEntity Msel { get; }
        public Dictionary<string, CompetencyEntity> Competencies { get; } = new Dictionary<string, CompetencyEntity>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Checks proposed changes against the MSEL, in order, as if each earlier one had been applied. Drops what cannot be
    /// applied, with a warning, and labels the rest for the planner. Shared by the proposal and by apply.
    /// </summary>
    public static class MselAssistRules
    {
        public const string TeamAdd = "team.add";
        public const string TeamUpdate = "team.update";
        public const string OrganizationAdd = "organization.add";
        public const string OrganizationUpdate = "organization.update";
        public const string CardAdd = "card.add";
        public const string CardUpdate = "card.update";
        public const string CompetencyAdd = "competency.add";
        public const string CompetencyRemove = "competency.remove";
        public static readonly string[] Ops = [TeamAdd, TeamUpdate, OrganizationAdd, OrganizationUpdate, CardAdd, CardUpdate, CompetencyAdd, CompetencyRemove];

        public static List<MselChange> Review(MselAssistState state, IEnumerable<MselChange> proposed, List<string> warnings)
        {
            var msel = state.Msel;
            var teams = msel.Teams.Select(t => t.ShortName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var organizations = msel.Organizations.Select(o => o.ShortName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var cards = msel.Cards.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            // competency links as "<team short name or empty>|<id number>"
            var links = msel.MselCompetencies.Select(mc => "|" + mc.Competency.IdNumber)
                .Concat(msel.Teams.SelectMany(t => t.TeamCompetencies.Select(tc => t.ShortName + "|" + tc.Competency.IdNumber)))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var kept = new List<MselChange>();

            foreach (var raw in proposed ?? Enumerable.Empty<MselChange>())
            {
                var change = Clean(raw);
                string problem = null;
                switch (change.Op)
                {
                    case TeamAdd:
                        change.Name ??= change.ShortName;
                        problem = change.ShortName == null ? "a new team needs a short name"
                            : !teams.Add(change.ShortName) ? $"there is already a team {change.ShortName}" : null;
                        change.Label = $"Add team {change.ShortName} ({change.Name})";
                        break;
                    case TeamUpdate:
                        problem = Rename(teams, change, "team");
                        change.Label = $"Edit team {change.Target}" + Describe(change);
                        break;
                    case OrganizationAdd:
                        change.Name ??= change.ShortName;
                        problem = change.ShortName == null ? "a new organization needs a short name"
                            : !organizations.Add(change.ShortName) ? $"there is already an organization {change.ShortName}" : null;
                        change.Label = $"Add organization {change.ShortName} ({change.Name})";
                        break;
                    case OrganizationUpdate:
                        problem = Rename(organizations, change, "organization");
                        change.Label = $"Edit organization {change.Target}" + Describe(change);
                        break;
                    case CardAdd:
                        change.Move = Math.Max(0, change.Move ?? 0);
                        change.Inject = Math.Max(0, change.Inject ?? 0);
                        problem = change.Name == null ? "a new card needs a name"
                            : !cards.Add(change.Name) ? $"there is already a card {change.Name}" : null;
                        change.Label = $"Add Gallery card {change.Name} (move {change.Move}, inject {change.Inject})";
                        break;
                    case CardUpdate:
                        if (change.Target == null || !cards.Contains(change.Target))
                            problem = $"there is no card {change.Target}";
                        else if (change.Name != null && !change.Name.Equals(change.Target, StringComparison.OrdinalIgnoreCase) && !cards.Add(change.Name))
                            problem = $"there is already a card {change.Name}";
                        else if (change.Name != null)
                            cards.Remove(change.Target);
                        change.Label = $"Edit Gallery card {change.Target}" + Describe(change);
                        break;
                    case CompetencyAdd:
                    case CompetencyRemove:
                    {
                        var add = change.Op == CompetencyAdd;
                        var owner = change.Target == null ? "the MSEL" : $"team {change.Target}";
                        change.Label = $"{(add ? "Add" : "Remove")} {change.IdNumber} {(add ? "to" : "from")} {owner}";
                        if (change.IdNumber == null || !state.Competencies.TryGetValue(change.IdNumber, out var competency))
                            problem = $"there is no competency {change.IdNumber}";
                        else if (change.Target != null && !teams.Contains(change.Target))
                            problem = $"there is no team {change.Target}";
                        else
                        {
                            change.IdNumber = competency.IdNumber;
                            change.Label = $"{(add ? "Add" : "Remove")} {competency.IdNumber} {competency.ShortName} {(add ? "to" : "from")} {owner}";
                            var link = (change.Target ?? "") + "|" + competency.IdNumber;
                            problem = add ? (links.Add(link) ? null : $"{owner} already has {competency.IdNumber}")
                                : (links.Remove(link) ? null : $"{owner} does not have {competency.IdNumber}");
                        }
                        break;
                    }
                    default:
                        problem = $"{change.Op ?? "an empty change"} is not a change the assistant can make";
                        change.Label = change.Op ?? "Unknown change";
                        break;
                }

                if (problem != null)
                    warnings.Add($"{change.Label}: left out, {problem}.");
                else
                    kept.Add(change);
            }
            return kept;
        }

        // the target must exist, and a new short name must not belong to another
        private static string Rename(HashSet<string> names, MselChange change, string kind)
        {
            if (change.Target == null || !names.Contains(change.Target))
                return $"there is no {kind} {change.Target}";
            if (change.ShortName == null || change.ShortName.Equals(change.Target, StringComparison.OrdinalIgnoreCase))
            {
                change.ShortName = null;
                return null;
            }
            if (!names.Add(change.ShortName))
                return $"there is already a {kind} {change.ShortName}";
            names.Remove(change.Target);
            return null;
        }

        private static string Describe(MselChange change)
        {
            var parts = new List<string>();
            if (change.ShortName != null) parts.Add($"short name {change.ShortName}");
            if (change.Name != null) parts.Add($"name \"{change.Name}\"");
            if (change.Summary != null) parts.Add("summary");
            if (change.Description != null) parts.Add("description");
            if (change.Move != null) parts.Add($"move {change.Move}");
            if (change.Inject != null) parts.Add($"inject {change.Inject}");
            return parts.Count == 0 ? "" : ": " + string.Join(", ", parts);
        }

        private static MselChange Clean(MselChange change) => new()
        {
            Op = change?.Op?.Trim().ToLowerInvariant(),
            Target = Text(change?.Target),
            ShortName = Text(change?.ShortName)?.ToUpperInvariant(),
            Name = Text(change?.Name),
            Description = Text(change?.Description),
            Summary = Text(change?.Summary),
            Move = change?.Move,
            Inject = change?.Inject,
            IdNumber = Text(change?.IdNumber)
        };

        private static string Text(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
