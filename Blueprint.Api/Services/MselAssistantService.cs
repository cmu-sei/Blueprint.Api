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
using Blueprint.Api.Data.Enumerations;
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
        Task<MselAssistResult> AskAsync(Guid mselId, MselAssistRequest request, MselAssistPermissions permissions, CancellationToken ct);
        Task<MselAssistApplyResult> ApplyAsync(Guid mselId, MselAssistApply apply, MselAssistPermissions permissions, CancellationToken ct);
    }

    /// <summary>The system permissions the change endpoints check, so applying a change is allowed exactly when making it by hand is</summary>
    public record MselAssistPermissions(bool EditMsels, bool ManageMsels, bool ManageOrganizations, bool ManageGalleryCards);

    /// <summary>
    /// Which kinds of change the planner may make on this MSEL, by the rules of the services that make them. A change
    /// the planner may not make is left out of a proposal with the reason, rather than failing when applied.
    /// </summary>
    public record MselAssistAccess(bool Teams, bool OrganizationsAndCards, bool Competencies, bool Moves, bool AddInjects, bool EditInjects)
    {
        public bool Any => Teams || OrganizationsAndCards || Competencies || Moves || AddInjects || EditInjects;

        public static readonly MselAssistAccess All = new(true, true, true, true, true, true);

        /// <summary>Why the planner may not make a change of this kind, or null when they may</summary>
        public string Refusal(string op) => op switch
        {
            MselAssistRules.TeamAdd or MselAssistRules.TeamUpdate when !Teams => "only the MSEL's owner can change its teams",
            MselAssistRules.OrganizationAdd or MselAssistRules.OrganizationUpdate or MselAssistRules.CardAdd or MselAssistRules.CardUpdate
                when !OrganizationsAndCards => "only the MSEL's owner or an editor can change its organizations and Gallery cards",
            MselAssistRules.CompetencyAdd or MselAssistRules.CompetencyRemove when !Competencies => "only the MSEL's owner can change its competencies",
            MselAssistRules.MoveAdd or MselAssistRules.MoveUpdate when !Moves => "only the MSEL's owner or a move editor can change its moves",
            MselAssistRules.InjectAdd when !AddInjects => "only the MSEL's owner can add injects",
            MselAssistRules.InjectUpdate when !EditInjects => "only the MSEL's owner, an editor or an approver can edit injects",
            _ => null
        };

        /// <summary>What the planner may change, in words the model reads</summary>
        public string Describe()
        {
            var parts = new List<string>();
            if (Teams) parts.Add("teams");
            if (OrganizationsAndCards) parts.Add("organizations and Gallery cards");
            if (Competencies) parts.Add("competencies");
            if (Moves) parts.Add("moves");
            if (AddInjects) parts.Add("new injects");
            if (EditInjects) parts.Add("existing injects");
            return string.Join(", ", parts);
        }
    }

    public class MselAssistantService : IMselAssistantService
    {
        private const string ToolName = "propose_msel_changes";
        // the conversation is resent each turn; older turns are dropped first
        private const int MaxHistoryMessages = 20;
        private const int MaxInjectsShown = 300;
        // each inject value shown to the model is cut to this, so a long article does not crowd out the rest
        private const int MaxValueLength = 300;

        private const string SystemPrompt =
@"You help an exercise planner change an existing Master Scenario Event List (MSEL) in Blueprint.
A MSEL is organized as Move > Group > Inject. Moves are time-bounded phases; an inject belongs to the move whose start it follows.
Moves run on two clocks: the exercise clock (minutes from the start, real time) and the scenario clock (the story's date and time
when the move opens, which can jump ahead between moves). Date anything in the story on the scenario clock within its move.
Injects that share a delivery time form a group. Teams are the participant groups (and EXCON, exercise control). Organizations are the
scenario's actors: the agencies, companies, media outlets and threat actors that send or appear in injects. Gallery cards group articles
on the participants' Gallery wall; a card's move and inject numbers say when it first appears. Competencies are framework work roles
and tasks (DCWF or NICE) linked to the MSEL as a whole or to a team.
You can propose these changes: add or edit a team, an organization or a Gallery card; add or remove a competency on the MSEL or a team;
add or edit a move; add an inject or edit an existing one.
Rules:
- Answer only by calling the tool. Put a short reply to the planner in reply: what you propose and why, in plain words.
- Change only what the planner asked for, plus what that needs (a new team named in the request must exist before a competency is put on it).
- If the request is ambiguous in a way that changes the result, ask one short question and propose no changes.
- If the planner asks for something you cannot change (deleting injects or moves, data fields, integrations, users, pages), say so in reply.
- Only propose kinds of change the planner is allowed to make, listed with the MSEL.
- Refer to existing teams and organizations by their short names, cards by their names, moves by their numbers and injects by their
  references (E1, E2, ...), exactly as listed.
- Short names are short, upper case and unique. Team and card descriptions are plain text; organization descriptions are HTML paragraphs.
- Use competency ID numbers exactly as listed. Prefer a work role for a team's role in the exercise and tasks for specific actions.
- An inject's values are keyed by column name and follow the column's description. For an edit, give only the columns that change.
- Keep injects consistent with the scenario, the moves and the injects around them: the same organizations, people, places and times.
- Every inject needs a clear purpose: what participants receive, who sends it, and what they are expected to do.
- Use fictional organizations, people, domains and IP addresses (RFC 5737 / RFC 1918 ranges) unless the planner names real ones.";

        private readonly BlueprintContext _context;
        private readonly IAiModelClient _aiModelClient;
        private readonly AiOptions _aiOptions;
        private readonly ITeamService _teamService;
        private readonly IOrganizationService _organizationService;
        private readonly ICardService _cardService;
        private readonly IMselCompetencyService _mselCompetencyService;
        private readonly ITeamCompetencyService _teamCompetencyService;
        private readonly IMoveService _moveService;
        private readonly IScenarioEventService _scenarioEventService;
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
            IMoveService moveService,
            IScenarioEventService scenarioEventService,
            IPrincipal user,
            IAiUsageContext usage)
        {
            _usage = usage;
            _context = context;
            _aiModelClient = aiModelClient;
            _aiOptions = aiOptions;
            _teamService = teamService;
            _organizationService = organizationService;
            _cardService = cardService;
            _mselCompetencyService = mselCompetencyService;
            _teamCompetencyService = teamCompetencyService;
            _moveService = moveService;
            _scenarioEventService = scenarioEventService;
            _user = user as ClaimsPrincipal;
        }

        private readonly IAiUsageContext _usage;

        public async Task<MselAssistResult> AskAsync(Guid mselId, MselAssistRequest request, MselAssistPermissions permissions, CancellationToken ct)
        {
            _usage.Use("assist", mselId);
            if (!_aiOptions.Enabled)
                throw new InvalidOperationException("AI drafting is not enabled on this server.");
            if (string.IsNullOrWhiteSpace(request?.Prompt))
                throw new BadRequestException("Say what to change.");
            var access = await AuthorizeAsync(mselId, permissions, ct);

            var state = await LoadStateAsync(mselId, ct);
            var workRoles = access.Competencies ? await LoadWorkRolesAsync(ct) : new List<CompetencyEntity>();
            // the body of a Gallery article is a whole document, laid out for its source type
            var articleBody = ArticleFields.Body(state.Msel);
            var system = articleBody == null ? SystemPrompt
                : SystemPrompt + $"\n\nThe {articleBody.Name} column of a Gallery article (an inject with a source type) holds the whole document. Write it this way:\n\n" + ArticleDraftService.SystemPrompt;
            var input = await _aiModelClient.InvokeToolAsync(system, BuildPrompt(request, state, workRoles, access), ToolName,
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
            result.Changes = MselAssistRules.Review(state, proposed, access, result.Warnings);
            if (!string.IsNullOrWhiteSpace(result.Question))
                result.Changes.Clear();
            return result;
        }

        public async Task<MselAssistApplyResult> ApplyAsync(Guid mselId, MselAssistApply apply, MselAssistPermissions permissions, CancellationToken ct)
        {
            var access = await AuthorizeAsync(mselId, permissions, ct);
            var result = new MselAssistApplyResult();
            var changes = apply?.Changes ?? new List<MselChange>();

            // checked again against the MSEL as it is now, as it may have changed since the proposal
            var state = await LoadStateAsync(mselId, ct);
            await AddCompetenciesAsync(state, changes, ct);
            foreach (var (change, problem) in MselAssistRules.ReviewEach(state, changes, access, new List<string>()))
            {
                var outcome = new MselChangeResult { Label = change.Label };
                if (problem != null)
                {
                    outcome.Error = $"No longer possible: {problem}.";
                    result.Results.Add(outcome);
                    continue;
                }
                try
                {
                    await ApplyChangeAsync(mselId, change, state, permissions, ct);
                    outcome.Applied = true;
                }
                catch (Exception ex) when (ex is ForbiddenException or ArgumentException or InvalidOperationException or DbUpdateException
                    || ex.GetType().Name.StartsWith("EntityNotFoundException"))
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
                case MselAssistRules.MoveAdd:
                    await _moveService.CreateAsync(new MoveCreate
                    {
                        Id = Guid.NewGuid(),
                        MselId = mselId,
                        MoveNumber = change.Move.Value,
                        Description = change.Description,
                        SituationDescription = change.Situation,
                        SituationTime = MselDraftRules.ParseUtc(change.SituationTime),
                        DeltaSeconds = (change.Minutes ?? 0) * 60
                    }, permissions.EditMsels, ct);
                    break;
                case MselAssistRules.MoveUpdate:
                {
                    var move = await _context.Moves.AsNoTracking().SingleAsync(m => m.MselId == mselId && m.MoveNumber == change.Move, ct);
                    await _moveService.UpdateAsync(move.Id, new MoveEdit
                    {
                        MoveNumber = move.MoveNumber,
                        Description = change.Description ?? move.Description,
                        SituationDescription = change.Situation ?? move.SituationDescription,
                        DeltaSeconds = change.Minutes.HasValue ? change.Minutes.Value * 60 : move.DeltaSeconds,
                        MoveStartTime = move.MoveStartTime,
                        SituationTime = MselDraftRules.ParseUtc(change.SituationTime) ?? move.SituationTime
                    }, permissions.EditMsels, ct);
                    break;
                }
                case MselAssistRules.InjectAdd:
                {
                    var deltaSeconds = change.Minutes.Value * 60;
                    var cells = new List<ScenarioEventCell>();
                    foreach (var field in state.Msel.DataFields)
                    {
                        var value = change.Values.GetValueOrDefault(field.Name) ?? MselAssistRules.ServerValue(field, state.Msel, deltaSeconds);
                        if (value != null)
                            cells.Add(new ScenarioEventCell { DataFieldId = field.Id, Value = value });
                    }
                    await _scenarioEventService.CreateAsync(new ScenarioEventCreate
                    {
                        Id = Guid.NewGuid(),
                        MselId = mselId,
                        DeltaSeconds = deltaSeconds,
                        ScenarioEventType = Enum.Parse<EventType>(change.EventType),
                        // the push sends a row to Gallery only when its integration target says so
                        IntegrationTarget = state.Msel.UseGallery && ArticleFields.IsArticle(state.Msel, change.Values) ? "Gallery" : null,
                        DataValues = cells
                    }, permissions.EditMsels, ct);
                    break;
                }
                case MselAssistRules.InjectUpdate:
                {
                    var inject = await _context.ScenarioEvents.AsNoTracking()
                        .Include(e => e.DataValues)
                        .SingleAsync(e => e.MselId == mselId && e.Id == change.ScenarioEventId, ct);
                    var deltaSeconds = change.Minutes.HasValue ? change.Minutes.Value * 60 : inject.DeltaSeconds;
                    // the edit writes every cell it carries, so it carries the current ones with the changed ones in place
                    var cells = new List<ScenarioEventCell>();
                    foreach (var field in state.Msel.DataFields)
                    {
                        var value = inject.DataValues.FirstOrDefault(v => v.DataFieldId == field.Id)?.Value;
                        if (change.Values != null && change.Values.TryGetValue(field.Name, out var changed))
                            value = changed;
                        else if (change.Minutes.HasValue && field.DataType == DataFieldType.DateTime && MselDraftRules.IsServerFilled(field))
                            value = MselAssistRules.ServerValue(field, state.Msel, deltaSeconds);
                        cells.Add(new ScenarioEventCell { DataFieldId = field.Id, ScenarioEventId = inject.Id, Value = value });
                    }
                    await _scenarioEventService.UpdateAsync(inject.Id, new ScenarioEventEdit
                    {
                        DeltaSeconds = deltaSeconds,
                        GroupOrder = inject.GroupOrder,
                        IsHidden = inject.IsHidden,
                        RowMetadata = inject.RowMetadata,
                        ScenarioEventType = change.EventType != null ? Enum.Parse<EventType>(change.EventType) : inject.ScenarioEventType,
                        IntegrationTarget = GalleryTarget(state.Msel, inject.IntegrationTarget, cells),
                        Information = inject.Information,
                        DataValues = cells
                    }, permissions.EditMsels, ct);
                    break;
                }
            }
        }

        // anyone who may make some kind of change may ask; the review leaves out what they may not make
        private async Task<MselAssistAccess> AuthorizeAsync(Guid mselId, MselAssistPermissions permissions, CancellationToken ct)
        {
            if (!await _context.Msels.AnyAsync(m => m.Id == mselId, ct))
                throw new EntityNotFoundException<MselEntity>("MSEL not found with ID=" + mselId);
            var userId = _user.GetId();
            var owner = await MselOwnerRequirement.IsMet(userId, mselId, _context);
            var editor = await MselEditorRequirement.IsMet(userId, mselId, _context);
            var approver = await MselApproverRequirement.IsMet(userId, mselId, _context);
            var moveEditor = await MoveEditorRequirement.IsMet(userId, mselId, _context);
            var access = new MselAssistAccess(
                Teams: permissions.EditMsels || owner,
                OrganizationsAndCards: permissions.EditMsels || owner || editor,
                Competencies: permissions.ManageMsels || owner,
                Moves: permissions.EditMsels || owner || moveEditor,
                AddInjects: permissions.EditMsels || owner,
                EditInjects: permissions.EditMsels || owner || editor || approver);
            if (!access.Any)
                throw new ForbiddenException();
            return access;
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
                .Include(m => m.DataFields).ThenInclude(f => f.DataOptions)
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

        // an edit that makes a row a Gallery article also sends it to Gallery; the row's other targets stay
        private static string GalleryTarget(MselEntity msel, string target, List<ScenarioEventCell> cells)
        {
            var values = cells.Where(c => c.Value != null)
                .Join(msel.DataFields, c => c.DataFieldId, f => f.Id, (c, f) => (f.Name, c.Value))
                .ToDictionary(v => v.Name, v => v.Value);
            if (!msel.UseGallery || !ArticleFields.IsArticle(msel, values) || (target?.Contains("Gallery") ?? false))
                return target;
            return string.IsNullOrEmpty(target) ? "Gallery" : target + "|Gallery";
        }

        private static string BuildPrompt(MselAssistRequest request, MselAssistState state, List<CompetencyEntity> workRoles, MselAssistAccess access)
        {
            var msel = state.Msel;
            var sb = new StringBuilder();
            sb.AppendLine($"MSEL: {msel.Name}");
            if (!string.IsNullOrWhiteSpace(msel.Description))
                sb.AppendLine($"Description: {DataValueDraftService.StripHtml(msel.Description)}");
            sb.AppendLine($"Integrations: {string.Join(", ", new[] { msel.UseGallery ? "Gallery" : null, msel.UseCite ? "CITE" : null, msel.UsePlayer ? "Player" : null, msel.UseSteamfitter ? "Steamfitter" : null }.Where(x => x != null))}");
            sb.AppendLine($"The planner may change: {access.Describe()}.");

            sb.AppendLine().AppendLine("Moves:");
            foreach (var move in msel.Moves.OrderBy(m => m.MoveNumber))
                sb.AppendLine($"- Move {move.MoveNumber} at minute {move.DeltaSeconds / 60}" +
                    (move.SituationTime.HasValue ? $", scenario time {move.SituationTime.Value.ToUniversalTime():yyyy-MM-ddTHH:mm:ssZ}" : "") +
                    $": {move.Description} Situation: {DataValueDraftService.StripHtml(move.SituationDescription)}");

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

            var fields = MselDraftRules.DraftedFields(msel).ToList();
            sb.AppendLine().AppendLine("Inject columns (name: what to write):");
            foreach (var field in fields)
            {
                var options = MselDraftRules.AllowedOptions(field);
                sb.AppendLine($"- {field.Name}: {MselDraftRules.Describe(field)}" + (options == null ? "" : $". One of: {string.Join(", ", options)}"));
            }
            sb.AppendLine("Event types: Inject, Information, Facilitation.");

            sb.AppendLine().AppendLine("Injects (reference, minute, event type, values):");
            foreach (var (reference, inject) in state.Injects.Take(MaxInjectsShown))
            {
                var values = fields
                    .Select(f => (f.Name, Value: Shorten(DataValueDraftService.StripHtml(inject.DataValues.FirstOrDefault(v => v.DataFieldId == f.Id)?.Value))))
                    .Where(v => !string.IsNullOrEmpty(v.Value))
                    .Select(v => $"{v.Name}: {v.Value}");
                sb.AppendLine($"- {reference} minute {inject.DeltaSeconds / 60} {inject.ScenarioEventType}: {string.Join(" | ", values)}");
            }
            if (state.Injects.Count > MaxInjectsShown)
                sb.AppendLine($"({state.Injects.Count - MaxInjectsShown} later injects are not shown.)");

            if (workRoles.Count > 0)
            {
                sb.AppendLine().AppendLine("Work roles that can be added (ID number, name, framework):");
                foreach (var role in workRoles)
                    sb.AppendLine($"- {role.IdNumber} {role.ShortName} ({role.CompetencyFramework.Name})");
            }

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

        private static string Shorten(string value) =>
            string.IsNullOrEmpty(value) || value.Length <= MaxValueLength ? value : value[..MaxValueLength] + "...";

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
                            ["target"] = new JsonObject { ["type"] = "string", ["description"] = "For a team or organization edit, its short name; for a card edit, its name; for an inject edit, its reference (E1, E2, ...). For a competency, the team's short name, or empty for the MSEL." },
                            ["shortName"] = new JsonObject { ["type"] = "string", ["description"] = "A team's or organization's short name, new or changed." },
                            ["name"] = new JsonObject { ["type"] = "string" },
                            ["description"] = new JsonObject { ["type"] = "string", ["description"] = "A team's, organization's, card's or move's description." },
                            ["summary"] = new JsonObject { ["type"] = "string", ["description"] = "An organization's one-line summary." },
                            ["move"] = new JsonObject { ["type"] = "integer", ["description"] = "A card's move, or the number of the move to add or edit." },
                            ["inject"] = new JsonObject { ["type"] = "integer", ["description"] = "A card's inject number within its move." },
                            ["idNumber"] = new JsonObject { ["type"] = "string", ["description"] = "A competency's ID number." },
                            ["minutes"] = new JsonObject { ["type"] = "integer", ["description"] = "A move's start, or an inject's time, in minutes from the start of the exercise." },
                            ["situation"] = new JsonObject { ["type"] = "string", ["description"] = "A move's situation description." },
                            ["situationTime"] = new JsonObject { ["type"] = "string", ["description"] = "The scenario date and time when a move opens, in ISO 8601 UTC; the scenario clock can jump ahead between moves." },
                            ["eventType"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("Inject", "Information", "Facilitation") },
                            ["values"] = new JsonObject
                            {
                                ["type"] = "object",
                                ["description"] = "An inject's values by column name. For an edit, only the columns that change.",
                                ["additionalProperties"] = new JsonObject { ["type"] = "string" }
                            }
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
        public MselAssistState(MselEntity msel)
        {
            Msel = msel;
            Injects = msel.ScenarioEvents
                .OrderBy(e => e.DeltaSeconds).ThenBy(e => e.GroupOrder).ThenBy(e => e.Id)
                .Select((e, i) => ($"E{i + 1}", e))
                .ToList();
        }

        public MselEntity Msel { get; }

        /// <summary>The injects in time order, each with the reference (E1, E2, ...) the model uses for it</summary>
        public List<(string Reference, ScenarioEventEntity Inject)> Injects { get; }

        public Dictionary<string, CompetencyEntity> Competencies { get; } = new Dictionary<string, CompetencyEntity>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Checks proposed changes against the MSEL, in order, as if each earlier one had been applied. Drops what cannot be
    /// applied, or what the planner may not make, with a warning, and labels the rest for the planner. Shared by the
    /// proposal and by apply.
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
        public const string MoveAdd = "move.add";
        public const string MoveUpdate = "move.update";
        public const string InjectAdd = "inject.add";
        public const string InjectUpdate = "inject.update";
        public static readonly string[] Ops = [TeamAdd, TeamUpdate, OrganizationAdd, OrganizationUpdate, CardAdd, CardUpdate, CompetencyAdd, CompetencyRemove, MoveAdd, MoveUpdate, InjectAdd, InjectUpdate];

        public static List<MselChange> Review(MselAssistState state, IEnumerable<MselChange> proposed, List<string> warnings) =>
            Review(state, proposed, MselAssistAccess.All, warnings);

        public static List<MselChange> Review(MselAssistState state, IEnumerable<MselChange> proposed, MselAssistAccess access, List<string> warnings)
        {
            var kept = new List<MselChange>();
            foreach (var (change, problem) in ReviewEach(state, proposed, access, warnings))
            {
                if (problem != null)
                    warnings.Add($"{change.Label}: left out, {problem}.");
                else
                    kept.Add(change);
            }
            return kept;
        }

        /// <summary>
        /// Each change, in order, labelled and with why it cannot be made (null when it can). Notes about single values,
        /// such as an option that does not exist, go to notes; the change is kept without them.
        /// </summary>
        public static List<(MselChange Change, string Problem)> ReviewEach(MselAssistState state, IEnumerable<MselChange> proposed, MselAssistAccess access, List<string> notes)
        {
            var warnings = notes;
            var msel = state.Msel;
            var teams = msel.Teams.Select(t => t.ShortName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var organizations = msel.Organizations.Select(o => o.ShortName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var cards = msel.Cards.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var moves = msel.Moves.Select(m => m.MoveNumber).ToHashSet();
            var fields = MselDraftRules.DraftedFields(msel).ToDictionary(f => f.Name, StringComparer.OrdinalIgnoreCase);
            // competency links as "<team short name or empty>|<id number>"
            var links = msel.MselCompetencies.Select(mc => "|" + mc.Competency.IdNumber)
                .Concat(msel.Teams.SelectMany(t => t.TeamCompetencies.Select(tc => t.ShortName + "|" + tc.Competency.IdNumber)))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var reviewed = new List<(MselChange, string)>();

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
                    case MoveAdd:
                        change.Minutes = Math.Max(0, change.Minutes ?? 0);
                        change.Label = $"Add move {change.Move} at {Time(change.Minutes.Value)}: {change.Description}";
                        problem = ScenarioTimeProblem(change) ?? (change.Move == null || change.Move < 0 ? "a new move needs a number"
                            : change.Description == null ? "a new move needs a description"
                            : !moves.Add(change.Move.Value) ? $"there is already a move {change.Move}" : null);
                        if (change.SituationTime != null)
                            change.Label += $" (scenario time {change.SituationTime})";
                        break;
                    case MoveUpdate:
                        change.Move ??= int.TryParse(change.Target, out var number) ? number : null;
                        change.Minutes = change.Minutes.HasValue ? Math.Max(0, change.Minutes.Value) : null;
                        problem = ScenarioTimeProblem(change);
                        change.Label = $"Edit move {change.Move}" + Describe(change);
                        problem ??= change.Move == null || !moves.Contains(change.Move.Value) ? $"there is no move {change.Move?.ToString() ?? change.Target}"
                            : change.Description == null && change.Situation == null && change.Minutes == null && change.SituationTime == null ? "it changes nothing" : null;
                        break;
                    case InjectAdd:
                    {
                        change.EventType = EventTypeOrDefault(change.EventType) ?? nameof(Data.Enumerations.EventType.Inject);
                        change.Values = CleanValues(change.Values, fields, teams, organizations, warnings);
                        change.Label = $"Add {Kind(change.EventType)} at {Time(change.Minutes ?? 0)}: {Title(change.Values)}";
                        problem = change.Minutes == null || change.Minutes < 0 ? "a new inject needs a time"
                            : change.Values.Count == 0 ? "a new inject needs values" : null;
                        break;
                    }
                    case InjectUpdate:
                    {
                        var target = state.Injects.FirstOrDefault(i =>
                            change.ScenarioEventId.HasValue ? i.Inject.Id == change.ScenarioEventId : string.Equals(i.Reference, change.Target, StringComparison.OrdinalIgnoreCase));
                        change.Values = CleanValues(change.Values, fields, teams, organizations, warnings);
                        change.Minutes = change.Minutes.HasValue ? Math.Max(0, change.Minutes.Value) : null;
                        change.EventType = change.EventType == null ? null : EventTypeOrDefault(change.EventType);
                        if (target.Inject == null)
                        {
                            change.Label = $"Edit inject {change.Target}";
                            problem = $"there is no inject {change.Target}";
                            break;
                        }
                        change.Target = target.Reference;
                        change.ScenarioEventId = target.Inject.Id;
                        var current = fields.Values.ToDictionary(f => f.Name, f => target.Inject.DataValues.FirstOrDefault(v => v.DataFieldId == f.Id)?.Value, StringComparer.OrdinalIgnoreCase);
                        // a value the inject already has is no change
                        foreach (var same in change.Values.Where(v => current.GetValueOrDefault(v.Key) == v.Value).Select(v => v.Key).ToList())
                            change.Values.Remove(same);
                        if (change.Minutes == target.Inject.DeltaSeconds / 60)
                            change.Minutes = null;
                        if (change.EventType == target.Inject.ScenarioEventType.ToString())
                            change.EventType = null;
                        var parts = change.Values.Keys.ToList();
                        if (change.Minutes.HasValue) parts.Add($"time {Time(change.Minutes.Value)}");
                        if (change.EventType != null) parts.Add($"type {change.EventType}");
                        change.Label = $"Edit {target.Reference} ({Title(current)})" + (parts.Count > 0 ? ": " + string.Join(", ", parts) : "");
                        problem = parts.Count == 0 ? "it changes nothing" : null;
                        break;
                    }
                    default:
                        problem = $"{change.Op ?? "an empty change"} is not a change the assistant can make";
                        change.Label = change.Op ?? "Unknown change";
                        break;
                }

                problem ??= access.Refusal(change.Op);
                reviewed.Add((change, problem));
            }
            return reviewed;
        }

        /// <summary>The value the server gives a column the model is not asked for: Pending status, and the delivery time from the MSEL's start</summary>
        public static string ServerValue(DataFieldEntity field, MselEntity msel, int deltaSeconds) => field.DataType switch
        {
            DataFieldType.Status => MselItemStatus.Pending.ToString(),
            DataFieldType.DateTime when MselDraftRules.IsServerFilled(field) => msel.StartTime.AddSeconds(deltaSeconds).ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
            _ => null
        };

        // the model's values, by the columns' exact names, checked as a draft's are; unknown columns are reported
        private static Dictionary<string, string> CleanValues(Dictionary<string, string> values, Dictionary<string, DataFieldEntity> fields,
            HashSet<string> teams, HashSet<string> organizations, List<string> warnings)
        {
            var cleaned = new Dictionary<string, string>();
            var actors = new HashSet<string>(organizations.Concat(teams), StringComparer.OrdinalIgnoreCase);
            foreach (var (name, raw) in values ?? new Dictionary<string, string>())
            {
                if (!fields.TryGetValue(name, out var field))
                {
                    warnings.Add($"Left out a value for {name}, which is not a column the assistant can write.");
                    continue;
                }
                var value = MselDraftRules.CleanValue(field, raw?.Trim(), teams, actors, warnings);
                // an article's body keeps its layout; other HTML is kept to simple formatting
                if (field.DataType == DataFieldType.Html && value != null)
                    value = field.GalleryArticleParameter == nameof(GalleryArticleParameter.Description)
                        ? GalleryArticleHtml.Prepare(value, warnings, GalleryArticleHtml.ArticleWidth)
                        : DataValueDraftService.CleanHtml(value);
                if (!string.IsNullOrEmpty(value))
                    cleaned[field.Name] = value;
            }
            return cleaned;
        }

        private static string EventTypeOrDefault(string value) =>
            Enum.TryParse<EventType>(value, true, out var type) && Enum.IsDefined(type) ? type.ToString() : null;

        private static string Kind(string eventType) => eventType == nameof(Data.Enumerations.EventType.Inject) ? "inject" : eventType.ToLowerInvariant();

        private static string Time(int minutes) => $"{minutes / 60}:{minutes % 60:00}";

        // the inject's title for a label: its Title or Name column, else its first value
        private static string Title(Dictionary<string, string> values)
        {
            var title = values.FirstOrDefault(v => v.Key.Equals("Title", StringComparison.OrdinalIgnoreCase) || v.Key.Equals("Name", StringComparison.OrdinalIgnoreCase)).Value
                ?? values.Values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
            title = DataValueDraftService.StripHtml(title);
            return string.IsNullOrEmpty(title) ? "untitled" : title.Length <= 80 ? title : title[..80] + "...";
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

        // a scenario time the model wrote, in the form Blueprint keeps; one that is not a time is the problem
        private static string ScenarioTimeProblem(MselChange change)
        {
            if (change.SituationTime == null)
                return null;
            var time = MselDraftRules.ParseUtc(change.SituationTime);
            if (time == null)
                return $"\"{change.SituationTime}\" is not a date and time";
            change.SituationTime = time.Value.ToString("yyyy-MM-ddTHH:mm:ssZ");
            return null;
        }

        private static string Describe(MselChange change)
        {
            var parts = new List<string>();
            if (change.ShortName != null) parts.Add($"short name {change.ShortName}");
            if (change.Name != null) parts.Add($"name \"{change.Name}\"");
            if (change.Summary != null) parts.Add("summary");
            if (change.Description != null) parts.Add("description");
            if (change.Situation != null) parts.Add("situation");
            if (change.SituationTime != null) parts.Add($"scenario time {change.SituationTime}");
            if (change.Op != MoveUpdate && change.Move != null) parts.Add($"move {change.Move}");
            if (change.Inject != null) parts.Add($"inject {change.Inject}");
            if (change.Op == MoveUpdate && change.Minutes != null) parts.Add($"start {Time(change.Minutes.Value)}");
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
            IdNumber = Text(change?.IdNumber),
            Minutes = change?.Minutes,
            Situation = Text(change?.Situation),
            SituationTime = Text(change?.SituationTime),
            EventType = Text(change?.EventType),
            Values = change?.Values == null ? null : new Dictionary<string, string>(change.Values, StringComparer.OrdinalIgnoreCase),
            ScenarioEventId = change?.ScenarioEventId
        };

        private static string Text(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
