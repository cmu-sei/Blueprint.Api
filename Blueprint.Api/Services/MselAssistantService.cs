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
    public record MselAssistAccess(bool Teams, bool OrganizationsAndCards, bool Competencies, bool Moves, bool AddInjects, bool EditInjects, bool Columns, bool Integrations, bool Pages, bool Techniques = false)
    {
        public bool Any => Teams || OrganizationsAndCards || Competencies || Moves || AddInjects || EditInjects || Columns || Integrations || Pages || Techniques;

        public static readonly MselAssistAccess All = new(true, true, true, true, true, true, true, true, true, true);

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
            MselAssistRules.ColumnAdd or MselAssistRules.ColumnUpdate when !Columns => "only the MSEL's owner or an editor can change its columns",
            MselAssistRules.IntegrationAdd when !Integrations => "only the MSEL's owner can change its integrations",
            MselAssistRules.PageAdd or MselAssistRules.PageUpdate when !Pages => "only the MSEL's owner can change its pages",
            MselAssistRules.TechniqueAdd when !Techniques => "only the MSEL's owner can change its techniques",
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
            if (Columns) parts.Add("inject columns");
            if (Integrations) parts.Add("integrations (turning one on)");
            if (Pages) parts.Add("pages");
            if (Techniques) parts.Add("techniques (adding them)");
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
add or edit a move; add an inject or edit an existing one; add an inject column or edit one; turn on an integration; add a page or edit one;
add a technique to the MSEL's techniques.
Rules:
- Answer only by calling the tool. Put a short reply to the planner in reply: what you propose and why, in plain words.
- Change only what the planner asked for, plus what that needs (a new team named in the request must exist before a competency is put on it).
- If the request is ambiguous in a way that changes the result, ask one short question and propose no changes.
- If the planner asks for something you cannot change (deleting injects, moves, columns, pages or techniques, turning an integration off, users), say so in reply.
- Techniques come from the technique releases listed with the MSEL; the MSEL takes each domain's techniques from one release, the one
  marked, or the newest when none is. Add a technique (technique.add, its id exactly as its release lists it, and the release's
  domain) before an inject names it. An AttackTechnique or DefensiveTechnique column holds ids of the MSEL's techniques of that kind,
  separated by "", "": ids only, no names. To turn a text column that names techniques into one, change its type to AttackTechnique
  or DefensiveTechnique: Blueprint reads the id at the start of each item, adds those techniques and keeps only the ids.
- Pages are the MSEL's documents, such as the read-in, the exercise overview and the facilitator guide. For an edit, write the
  page's whole new content in HTML, changing what the planner asked and keeping the rest as it is: its headings, order, tables and
  wording. Use section, h2, h3, h4, p, ul, ol, li, strong, em and table with border=""1"" cellpadding=""6"" cellspacing=""0"" only;
  no colors, images or style attributes. A page participants see reveals nothing they should only learn from an inject. A page
  shown cut short cannot be edited here; say so, and that Write with AI on the page can.
- Turning on an integration also needs what it works with, which you propose in the same changes, after it. Gallery: Blueprint adds the
  article columns (listed with the change), and you add the Gallery cards and give the injects that are articles a source type,
  sender, recipients, title, summary and card. CITE: choose a scoring model preset that fits (MissionImpact, Likelihood or Nciss),
  or none to leave it to the planner; Blueprint gives the teams CITE team types. Player, Steamfitter and Caster need setting up on
  their tabs afterwards, which say so.
- Add a column when the planner wants injects to carry something no column holds. Give it a short name, what it is for in
  description, and a type; a column whose values come from a fixed set is a String with options, in order. Then fill it on the
  injects that need it in the same proposal, keyed by the new name: column changes are applied before the injects.
- A column that fills part of a Gallery article names the article parameter it fills; each parameter belongs to one column.
- Only propose kinds of change the planner is allowed to make, listed with the MSEL.
- Refer to existing teams and organizations by their short names, cards by their names, moves by their numbers and injects by their
  references (E1, E2, ...), exactly as listed.
- Short names are short, upper case and unique. Team and card descriptions are plain text; organization descriptions are HTML paragraphs.
- Use competency ID numbers exactly as listed. Prefer a work role for a team's role in the exercise and tasks for specific actions.
- An inject's values are keyed by column name and follow the column's description. For an edit, give only the columns that change;
  to clear a column, give it an empty value.
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
            IAiUsageContext usage,
            IDataFieldService dataFieldService,
            Cite.Api.Client.ICiteApiClient citeApiClient,
            IMselPageService mselPageService,
            IMselTechniqueService mselTechniqueService)
        {
            _mselPageService = mselPageService;
            _mselTechniqueService = mselTechniqueService;
            _usage = usage;
            _dataFieldService = dataFieldService;
            _citeApiClient = citeApiClient;
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
        private readonly IDataFieldService _dataFieldService;
        private readonly Cite.Api.Client.ICiteApiClient _citeApiClient;
        private readonly IMselPageService _mselPageService;
        private readonly IMselTechniqueService _mselTechniqueService;

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
            await AddTechniquesAsync(state, proposed, ct);
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
            await AddTechniquesAsync(state, changes, ct);
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
                catch (Exception ex) when (ex is ForbiddenException or ArgumentException or InvalidOperationException or DbUpdateException or ConflictException
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
                case MselAssistRules.TechniqueAdd:
                    await _mselTechniqueService.CreateAsync(new ViewModels.MselTechnique { MselId = mselId, TechniqueId = change.TechniqueId.Value }, permissions.ManageMsels, ct);
                    break;
                case MselAssistRules.ColumnAdd:
                {
                    var order = state.Msel.DataFields.Select(f => f.DisplayOrder).DefaultIfEmpty(0).Max() + 1;
                    var techniques = MselAssistRules.IsTechniqueType(change.DataType);
                    await _dataFieldService.CreateAsync(new DataFieldCreate
                    {
                        Id = Guid.NewGuid(),
                        MselId = mselId,
                        Name = change.Name,
                        Description = change.Description,
                        DataType = Enum.Parse<DataFieldType>(change.DataType),
                        DisplayOrder = order,
                        OnScenarioEventList = change.OnEventList ?? true,
                        OnExerciseView = change.OnExerciseView ?? false,
                        IsShownOnDefaultTab = true,
                        IsChosenFromList = change.Options?.Count > 0,
                        // a technique column offers the MSEL's techniques, several at a time, as the Data Fields tab sets one up
                        IsMultiSelect = techniques,
                        IsFacilitationField = techniques,
                        DataOptions = Options(change.Options),
                        GalleryArticleParameter = change.GalleryParameter
                    }, permissions.EditMsels, false, ct);
                    await ReloadColumnsAsync(state, mselId, ct);
                    break;
                }
                case MselAssistRules.ColumnUpdate:
                {
                    var field = await _context.DataFields.AsNoTracking().Include(f => f.DataOptions)
                        .SingleAsync(f => f.MselId == mselId && f.Name == change.Target, ct);
                    // the options that stay keep their ids, so the values chosen from them stay chosen
                    var converting = change.Conversion != null;
                    var options = converting ? new List<DataFieldOption>()
                        : change.Options == null
                        ? field.DataOptions.Select(o => new DataFieldOption { Id = o.Id, OptionName = o.OptionName, OptionValue = o.OptionValue, OptionDescription = o.OptionDescription, DisplayOrder = o.DisplayOrder }).ToList()
                        : Options(change.Options, field.DataOptions);
                    await _dataFieldService.UpdateAsync(field.Id, new DataFieldEdit
                    {
                        Name = change.Name ?? field.Name,
                        Description = change.Description ?? field.Description,
                        DataType = change.DataType != null ? Enum.Parse<DataFieldType>(change.DataType) : field.DataType,
                        DisplayOrder = field.DisplayOrder,
                        OnScenarioEventList = change.OnEventList ?? field.OnScenarioEventList,
                        OnExerciseView = change.OnExerciseView ?? field.OnExerciseView,
                        IsChosenFromList = options.Count > 0,
                        IsMultiSelect = converting || field.IsMultiSelect,
                        DataOptions = options,
                        CellMetadata = field.CellMetadata,
                        ColumnMetadata = field.ColumnMetadata,
                        IsShownOnDefaultTab = field.IsShownOnDefaultTab,
                        IsOnlyShownToOwners = field.IsOnlyShownToOwners,
                        GalleryArticleParameter = change.GalleryParameter ?? field.GalleryArticleParameter,
                        IsInformationField = field.IsInformationField,
                        IsFacilitationField = converting || field.IsFacilitationField,
                        IsAssessorVisible = field.IsAssessorVisible
                    }, permissions.EditMsels, false, ct);
                    if (converting)
                        await ConvertToTechniquesAsync(mselId, field.Id, change.Conversion, permissions, ct);
                    await ReloadColumnsAsync(state, mselId, ct);
                    break;
                }
                case MselAssistRules.PageAdd:
                    await _mselPageService.CreateAsync(new MselPageCreate
                    {
                        Id = Guid.NewGuid(),
                        MselId = mselId,
                        Name = change.Name,
                        Content = change.Content,
                        AllCanView = change.AllCanView ?? false,
                        IncludeInPlaybook = true
                    }, permissions.EditMsels, ct);
                    break;
                case MselAssistRules.PageUpdate:
                {
                    var page = await _context.MselPages.AsNoTracking().SingleAsync(p => p.MselId == mselId && p.Name == change.Target, ct);
                    await _mselPageService.UpdateAsync(page.Id, new MselPageEdit
                    {
                        Name = change.Name ?? page.Name,
                        Content = change.Content ?? page.Content,
                        AllCanView = change.AllCanView ?? page.AllCanView,
                        IncludeInPlaybook = page.IncludeInPlaybook
                    }, permissions.EditMsels, ct);
                    break;
                }
                case MselAssistRules.IntegrationAdd:
                    await TurnOnAsync(mselId, change, ct);
                    await ReloadColumnsAsync(state, mselId, ct);
                    break;
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
                        IntegrationTarget = GalleryTarget(state.Msel, Enum.Parse<EventType>(change.EventType), null, cells),
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
                        IntegrationTarget = GalleryTarget(state.Msel, change.EventType != null ? Enum.Parse<EventType>(change.EventType) : inject.ScenarioEventType, inject.IntegrationTarget, cells),
                        Information = inject.Information,
                        DataValues = cells
                    }, permissions.EditMsels, ct);
                    break;
                }
            }
        }

        // a text column turned into a technique column: its techniques join the MSEL's, then each inject's text becomes their ids
        private async Task ConvertToTechniquesAsync(Guid mselId, Guid fieldId, TechniqueColumnConversion conversion, MselAssistPermissions permissions, CancellationToken ct)
        {
            var pooled = await _context.MselTechniques.Where(m => m.MselId == mselId).Select(m => m.TechniqueId).ToListAsync(ct);
            foreach (var techniqueId in conversion.TechniquesToAdd.Except(pooled))
                await _mselTechniqueService.CreateAsync(new ViewModels.MselTechnique { MselId = mselId, TechniqueId = techniqueId }, permissions.ManageMsels, ct);
            var values = await _context.DataValues
                .Where(v => v.DataFieldId == fieldId && v.ScenarioEventId != null && conversion.Values.Keys.Contains(v.ScenarioEventId.Value))
                .ToListAsync(ct);
            foreach (var value in values)
                value.Value = conversion.Values[value.ScenarioEventId.Value];
            await _context.SaveChangesAsync(ct);
        }

        // looks up the techniques the changes name, in every release Blueprint has, so the review can check them
        private async Task AddTechniquesAsync(MselAssistState state, List<MselChange> changes, CancellationToken ct)
        {
            var wanted = MselAssistRules.TechniqueIdsNamed(state, changes).Select(id => id.ToUpper()).Distinct().ToList();
            if (wanted.Count == 0)
                return;
            state.Techniques.AddRange(await _context.Techniques.AsNoTracking()
                .Include(t => t.TechniqueRelease)
                .Where(t => wanted.Contains(t.ExternalId.ToUpper()))
                .ToListAsync(ct));
        }

        // an integration turned on, with what it works with: Gallery's article columns, CITE's team types and scoring model
        private async Task TurnOnAsync(Guid mselId, MselChange change, CancellationToken ct)
        {
            var msel = await _context.Msels
                .Include(m => m.DataFields).ThenInclude(f => f.DataOptions)
                .Include(m => m.Teams)
                .Include(m => m.ScoringModel)
                .AsSplitQuery()
                .SingleAsync(m => m.Id == mselId, ct);
            if (MselService.HasIntegrations(msel))
                throw new InvalidOperationException("The MSEL has been pushed; its integrations change only after a pull.");
            var userId = _user.GetId();
            switch (change.Integration)
            {
                case "Player": msel.UsePlayer = true; break;
                case "Steamfitter": msel.UseSteamfitter = true; break;
                case "Caster": msel.UseCaster = true; break;
                case "Gallery":
                {
                    msel.UseGallery = true;
                    var known = msel.DataFields.Select(f => f.Id).ToHashSet();
                    MselDraftRules.AddGalleryFields(msel);
                    // a column the rules add has its id already, which change tracking would take for a stored one
                    foreach (var field in msel.DataFields.Where(f => !known.Contains(f.Id)))
                    {
                        field.CreatedBy = userId;
                        _context.Entry(field).State = EntityState.Added;
                    }
                    break;
                }
                case "Cite":
                    msel.UseCite = true;
                    // a team type CITE cannot give now is left for the planner, as the push needs one; the change still lands
                    await MselDraftCite.SetTeamTypesAsync(msel, _citeApiClient, (_, _) => { }, ct);
                    if (change.Preset != null && MselDraftCite.AuthorPreset(msel, change.Preset, userId))
                    {
                        // the preset's rows carry their ids, which change tracking would take for stored ones
                        _context.Entry(msel.ScoringModel).State = EntityState.Added;
                        foreach (var category in msel.ScoringModel.Categories)
                        {
                            _context.Entry(category).State = EntityState.Added;
                            foreach (var option in category.Options)
                                _context.Entry(option).State = EntityState.Added;
                        }
                    }
                    break;
            }
            msel.ModifiedBy = userId;
            msel.DateModified = DateTime.UtcNow;
            await _context.SaveChangesAsync(ct);
        }

        // a column added or changed in this apply is one the injects after it write to
        private async Task ReloadColumnsAsync(MselAssistState state, Guid mselId, CancellationToken ct) =>
            state.Msel.DataFields = await _context.DataFields.AsNoTracking().Include(f => f.DataOptions)
                .Where(f => f.MselId == mselId).ToListAsync(ct);

        // a list column's options, in order; an option that stays keeps its id
        private static List<DataFieldOption> Options(List<string> values, IEnumerable<DataOptionEntity> existing = null) =>
            (values ?? new List<string>()).Select((value, i) =>
            {
                var kept = existing?.FirstOrDefault(o => string.Equals(o.OptionValue, value, StringComparison.OrdinalIgnoreCase));
                return new DataFieldOption
                {
                    Id = kept?.Id ?? Guid.Empty,
                    OptionName = value,
                    OptionValue = value,
                    OptionDescription = kept?.OptionDescription,
                    DisplayOrder = i + 1
                };
            }).ToList();

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
                EditInjects: permissions.EditMsels || owner || editor || approver,
                Columns: permissions.EditMsels || owner || editor,
                Integrations: permissions.EditMsels || owner,
                Pages: permissions.EditMsels || owner,
                Techniques: permissions.ManageMsels || owner);
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
                .Include(m => m.Pages)
                .Include(m => m.MselTechniques).ThenInclude(mt => mt.Technique).ThenInclude(t => t.TechniqueRelease)
                .AsSplitQuery()
                .SingleAsync(m => m.Id == mselId, ct);
            var state = new MselAssistState(msel)
            {
                Releases = await _context.TechniqueReleases.AsNoTracking().ToListAsync(ct)
            };
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

        // a row is a Gallery article when it is an inject with a source type: making it one also sends it to Gallery, and an
        // edit that leaves it no longer one, such as clearing the source type of a controller's step, takes Gallery off it;
        // a MSEL with no article columns cannot tell, so its rows keep the targets they have; the row's other targets stay
        public static string GalleryTarget(MselEntity msel, EventType eventType, string target, List<ScenarioEventCell> cells)
        {
            if (!msel.UseGallery || ArticleFields.Body(msel) == null
                || !msel.DataFields.Any(f => f.GalleryArticleParameter == nameof(Data.Enumerations.GalleryArticleParameter.SourceType)))
                return target;
            var values = cells.Where(c => c.Value != null)
                .Join(msel.DataFields, c => c.DataFieldId, f => f.Id, (c, f) => (f.Name, c.Value))
                .ToDictionary(v => v.Name, v => v.Value);
            var targets = (target ?? "").Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            var article = eventType == EventType.Inject && ArticleFields.IsArticle(msel, values);
            if (article && !targets.Contains("Gallery"))
                targets.Add("Gallery");
            else if (!article)
                targets.RemoveAll(t => t == "Gallery");
            return targets.Any() ? string.Join("|", targets) : null;
        }

        private static string BuildPrompt(MselAssistRequest request, MselAssistState state, List<CompetencyEntity> workRoles, MselAssistAccess access)
        {
            var msel = state.Msel;
            var sb = new StringBuilder();
            sb.AppendLine($"MSEL: {msel.Name}");
            if (!string.IsNullOrWhiteSpace(msel.Description))
                sb.AppendLine($"Description: {DataValueDraftService.StripHtml(msel.Description)}");
            sb.AppendLine($"Integrations on: {string.Join(", ", new[] { msel.UsePlayer ? "Player" : null, msel.UseGallery ? "Gallery" : null, msel.UseCite ? "Cite" : null, msel.UseSteamfitter ? "Steamfitter" : null, msel.UseCaster ? "Caster" : null }.Where(x => x != null).DefaultIfEmpty("none"))}" +
                (MselService.HasIntegrations(msel) ? " (pushed: integrations cannot change until it is pulled)" : ""));
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

            if (state.Releases.Count > 0)
            {
                var pins = MselAssistRules.PinnedReleases(msel);
                sb.AppendLine().AppendLine("Technique releases Blueprint has (name and version, domain, attack or defensive):");
                foreach (var release in state.Releases.OrderBy(r => r.IsDefensive).ThenBy(r => r.Domain).ThenByDescending(r => r.ReleaseDate))
                    sb.AppendLine($"- {release.Name} {release.Version}, domain {release.Domain}, {(release.IsDefensive ? "defensive" : "attack")}" +
                        (pins.TryGetValue(release.Domain, out var pinned) && pinned == release.Id ? $" (this MSEL's {release.Domain} techniques come from it)" : ""));
                var pool = msel.MselTechniques.Select(m => m.Technique).OrderBy(t => t.TechniqueRelease.IsDefensive).ThenBy(t => t.ExternalId).ToList();
                sb.AppendLine("MSEL techniques (id, name, domain):" + (pool.Count == 0 ? " none" : ""));
                foreach (var technique in pool)
                    sb.AppendLine($"- {technique.ExternalId} {technique.Name} ({technique.TechniqueRelease.Domain}, {(technique.TechniqueRelease.IsDefensive ? "defensive" : "attack")})");
            }

            var fields = MselDraftRules.DraftedFields(msel).ToList();
            sb.AppendLine().AppendLine("Inject columns (name, type: what to write):");
            foreach (var field in fields)
            {
                var options = MselDraftRules.AllowedOptions(field);
                sb.AppendLine($"- {field.Name}, {field.DataType}: {MselDraftRules.Describe(field)}" + (options == null ? "" : $". One of: {string.Join(", ", options)}"));
            }
            var filled = msel.DataFields.Except(fields).OrderBy(f => f.DisplayOrder).Select(f => f.Name).ToList();
            if (filled.Count > 0)
                sb.AppendLine($"Columns Blueprint fills itself, which injects do not write: {string.Join(", ", filled)}.");
            sb.AppendLine($"Column types: {string.Join(", ", MselAssistRules.ColumnTypes)}. Gallery article parameters: {string.Join(", ", Enum.GetNames<GalleryArticleParameter>())}.");
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

            if (msel.Pages.Any())
            {
                sb.AppendLine().AppendLine("Pages (name, who sees it, content):");
                foreach (var page in msel.Pages.OrderBy(p => p.Name))
                {
                    var content = page.Content ?? "";
                    var shown = content.Length <= MselAssistRules.MaxPageShown ? content : content[..MselAssistRules.MaxPageShown] + " [cut short]";
                    sb.AppendLine($"- {page.Name} ({(page.AllCanView ? "participants and controllers" : "controllers only")}):").AppendLine(shown);
                }
            }

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
                            ["target"] = new JsonObject { ["type"] = "string", ["description"] = "For a team or organization edit, its short name; for a card, column or page edit, its name; for an inject edit, its reference (E1, E2, ...). For a competency, the team's short name, or empty for the MSEL." },
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
                            ["dataType"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray(MselAssistRules.ColumnTypes.Select(t => (JsonNode)t).ToArray()), ["description"] = "A column's type." },
                            ["options"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" }, ["description"] = "A list column's choices, in order; for an edit, the whole new list." },
                            ["galleryParameter"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray(Enum.GetNames<GalleryArticleParameter>().Select(t => (JsonNode)t).ToArray()), ["description"] = "The Gallery article parameter a column fills, if any." },
                            ["onExerciseView"] = new JsonObject { ["type"] = "boolean", ["description"] = "Whether participants see the column on the Exercise View." },
                            ["onEventList"] = new JsonObject { ["type"] = "boolean", ["description"] = "Whether the column shows on the scenario event list." },
                            ["integration"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray(MselAssistRules.Integrations.Select(t => (JsonNode)t).ToArray()), ["description"] = "The integration to turn on." },
                            ["preset"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray(MselDraftRules.MissionImpact, MselDraftRules.Likelihood, MselDraftRules.Nciss), ["description"] = "For CITE, the scoring model to author; leave out to let the planner choose." },
                            ["content"] = new JsonObject { ["type"] = "string", ["description"] = "A page's whole content, in HTML." },
                            ["technique"] = new JsonObject { ["type"] = "string", ["description"] = "For a technique add, the technique's id exactly as its release lists it." },
                            ["domain"] = new JsonObject { ["type"] = "string", ["description"] = "For a technique add, the domain of the release it is from, as listed." },
                            ["allCanView"] = new JsonObject { ["type"] = "boolean", ["description"] = "Whether participants may see a page, as they do the read-in; controllers only when false." },
                            ["values"] = new JsonObject
                            {
                                ["type"] = "object",
                                ["description"] = "An inject's values by column name. For an edit, only the columns that change; an empty value clears the column.",
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

        /// <summary>The technique releases Blueprint has</summary>
        public List<TechniqueReleaseEntity> Releases { get; set; } = new List<TechniqueReleaseEntity>();

        /// <summary>The techniques the changes name, found in those releases, each with its release</summary>
        public List<TechniqueEntity> Techniques { get; } = new List<TechniqueEntity>();
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
        public const string ColumnAdd = "column.add";
        public const string ColumnUpdate = "column.update";
        public const string IntegrationAdd = "integration.add";
        public const string PageAdd = "page.add";
        public const string PageUpdate = "page.update";
        public const string TechniqueAdd = "technique.add";
        public static readonly string[] Ops = [TeamAdd, TeamUpdate, OrganizationAdd, OrganizationUpdate, CardAdd, CardUpdate, CompetencyAdd, CompetencyRemove, MoveAdd, MoveUpdate, InjectAdd, InjectUpdate, ColumnAdd, ColumnUpdate, IntegrationAdd, PageAdd, PageUpdate, TechniqueAdd];
        /// <summary>How much of a page the model is shown; a longer page is shown cut short and cannot be edited here</summary>
        public const int MaxPageShown = 12000;
        public static readonly string[] Integrations = ["Player", "Gallery", "Cite", "Steamfitter", "Caster"];

        /// <summary>Whether the MSEL has an integration on</summary>
        public static bool Uses(MselEntity msel, string integration) => integration switch
        {
            "Player" => msel.UsePlayer,
            "Gallery" => msel.UseGallery,
            "Cite" => msel.UseCite,
            "Steamfitter" => msel.UseSteamfitter,
            "Caster" => msel.UseCaster,
            _ => false
        };

        /// <summary>The article columns turning Gallery on adds, by the rules a draft's MSEL gets them by</summary>
        public static List<DataFieldEntity> GalleryColumns(MselEntity msel)
        {
            var copy = new MselEntity
            {
                Id = msel.Id,
                UseGallery = true,
                DataFields = msel.DataFields.Select(f => new DataFieldEntity
                {
                    Id = f.Id, Name = f.Name, DataType = f.DataType, DisplayOrder = f.DisplayOrder, GalleryArticleParameter = f.GalleryArticleParameter,
                    IsChosenFromList = f.IsChosenFromList, DataOptions = f.DataOptions
                }).ToList()
            };
            var known = msel.DataFields.Select(f => f.Id).ToHashSet();
            MselDraftRules.AddGalleryFields(copy);
            return copy.DataFields.Where(f => !known.Contains(f.Id)).ToList();
        }

        /// <summary>The column types the assistant may give a column; the rest are filled by Blueprint or chosen from its own lists</summary>
        public static readonly string[] ColumnTypes = [nameof(DataFieldType.String), nameof(DataFieldType.Html), nameof(DataFieldType.Integer),
            nameof(DataFieldType.Double), nameof(DataFieldType.Boolean), nameof(DataFieldType.DateTime), nameof(DataFieldType.Url),
            nameof(DataFieldType.Organization), nameof(DataFieldType.Team), nameof(DataFieldType.TeamsMultiple), nameof(DataFieldType.SourceType),
            nameof(DataFieldType.IpAddress), nameof(DataFieldType.Network), nameof(DataFieldType.AttackTechnique), nameof(DataFieldType.DefensiveTechnique)];

        public static bool IsTechniqueType(DataFieldType type) => type is DataFieldType.AttackTechnique or DataFieldType.DefensiveTechnique;

        public static bool IsTechniqueType(string type) => Enum.TryParse<DataFieldType>(type, out var parsed) && IsTechniqueType(parsed);

        /// <summary>The release each domain of the MSEL's techniques comes from</summary>
        public static Dictionary<string, Guid> PinnedReleases(MselEntity msel)
        {
            var pins = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
            foreach (var technique in msel.MselTechniques.Select(m => m.Technique).Where(t => t?.TechniqueRelease != null))
                pins.TryAdd(technique.TechniqueRelease.Domain, technique.TechniqueReleaseId);
            return pins;
        }

        /// <summary>
        /// The technique ids a value names: the first word of each item, the items separated by semicolons or new lines
        /// when the value has them, else by commas, so "T1, T2" and "T1 Name: Detail; T2 Name" both read as two ids.
        /// </summary>
        public static List<string> TechniqueIds(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return new List<string>();
            var separators = value.IndexOfAny([';', '\n']) >= 0 ? new[] { ';', '\n' } : new[] { ',' };
            return value.Split(separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(item => item.Split((char[])null, 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim('(', ')', ':', ',', '.'))
                .Where(id => !string.IsNullOrEmpty(id))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>Every technique id the changes name, for looking them up before the review</summary>
        public static IEnumerable<string> TechniqueIdsNamed(MselAssistState state, IEnumerable<MselChange> changes)
        {
            var msel = state.Msel;
            var techniqueColumns = msel.DataFields.Where(f => IsTechniqueType(f.DataType)).Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var change in changes ?? Enumerable.Empty<MselChange>())
            {
                var op = change?.Op?.Trim().ToLowerInvariant();
                if (op == TechniqueAdd && !string.IsNullOrWhiteSpace(change.Technique))
                    yield return change.Technique.Trim();
                if ((op == ColumnAdd || op == ColumnUpdate) && IsTechniqueType(ColumnType(change.DataType)))
                {
                    var name = Text(change.Name) ?? Text(change.Target);
                    if (name != null)
                        techniqueColumns.Add(name);
                    var field = op == ColumnUpdate ? msel.DataFields.FirstOrDefault(f => f.Name.Equals(Text(change.Target), StringComparison.OrdinalIgnoreCase)) : null;
                    if (field != null && !IsTechniqueType(field.DataType))
                        foreach (var cell in msel.ScenarioEvents.SelectMany(e => e.DataValues).Where(v => v.DataFieldId == field.Id))
                            foreach (var id in TechniqueIds(DataValueDraftService.StripHtml(cell.Value)))
                                yield return id;
                }
                if (change?.Values != null)
                    foreach (var (name, value) in change.Values)
                        if (techniqueColumns.Contains(name))
                            foreach (var id in TechniqueIds(value))
                                yield return id;
            }
        }

        // the MSEL's techniques as the changes before this one leave them, and where an id is found
        private sealed class TechniquePool
        {
            private readonly MselAssistState _state;
            private readonly Dictionary<string, Guid> _pins;
            private readonly List<TechniqueEntity> _pooled;

            public TechniquePool(MselAssistState state)
            {
                _state = state;
                _pins = PinnedReleases(state.Msel);
                _pooled = state.Msel.MselTechniques.Select(m => m.Technique).Where(t => t?.TechniqueRelease != null).ToList();
            }

            public bool Holds(TechniqueEntity technique) => _pooled.Any(p => p.Id == technique.Id);

            public void Add(TechniqueEntity technique)
            {
                if (Holds(technique))
                    return;
                _pooled.Add(technique);
                _pins.TryAdd(technique.TechniqueRelease.Domain, technique.TechniqueReleaseId);
            }

            /// <summary>The MSEL's technique of a kind with an id, or null</summary>
            public TechniqueEntity Pooled(string id, bool defensive) =>
                _pooled.FirstOrDefault(p => p.TechniqueRelease.IsDefensive == defensive && p.ExternalId.Equals(id, StringComparison.OrdinalIgnoreCase));

            /// <summary>
            /// The technique an id names: in the release the MSEL takes that domain from, else the newest release that has it,
            /// preferring a domain the MSEL already uses; or why there is none.
            /// </summary>
            public (TechniqueEntity Technique, string Problem) Find(string id, string domain, bool? defensive)
            {
                if (id == null)
                    return (null, "it names no technique");
                if (domain != null && !_state.Releases.Any(r => r.Domain.Equals(domain, StringComparison.OrdinalIgnoreCase))
                    && !_pooled.Any(p => p.TechniqueRelease.Domain.Equals(domain, StringComparison.OrdinalIgnoreCase)))
                    return (null, $"Blueprint has no {domain} technique release");
                var candidates = _state.Techniques.Concat(_pooled)
                    .Where(t => t.TechniqueRelease != null && t.ExternalId.Equals(id, StringComparison.OrdinalIgnoreCase)
                        && (domain == null || t.TechniqueRelease.Domain.Equals(domain, StringComparison.OrdinalIgnoreCase))
                        && (defensive == null || t.TechniqueRelease.IsDefensive == defensive))
                    .DistinctBy(t => t.Id)
                    .ToList();
                if (candidates.Count == 0)
                    return (null, $"Blueprint has no technique {id}" + (domain == null ? "" : $" in {domain}"));
                string problem = null;
                foreach (var inDomain in candidates.GroupBy(t => t.TechniqueRelease.Domain, StringComparer.OrdinalIgnoreCase).OrderByDescending(g => _pins.ContainsKey(g.Key)))
                {
                    if (!_pins.TryGetValue(inDomain.Key, out var releaseId))
                        return (inDomain.OrderByDescending(t => t.TechniqueRelease.ReleaseDate ?? DateTime.MinValue).ThenByDescending(t => t.TechniqueRelease.DateCreated).First(), null);
                    var pinned = inDomain.FirstOrDefault(t => t.TechniqueReleaseId == releaseId);
                    if (pinned != null)
                        return (pinned, null);
                    var release = _state.Releases.FirstOrDefault(r => r.Id == releaseId) ?? _pooled.First(p => p.TechniqueReleaseId == releaseId).TechniqueRelease;
                    problem ??= $"{id} is not in {release.Name} {release.Version}, the release this MSEL's {inDomain.Key} techniques come from";
                }
                return (null, problem);
            }
        }

        // a technique column's value as the ids of the MSEL's techniques of its kind; an id it does not have is left out
        private static void TechniqueValues(MselChange change, Dictionary<string, DataFieldEntity> fields, TechniquePool pool, string where, List<string> warnings)
        {
            foreach (var name in change.Values.Keys.ToList())
            {
                var field = fields.GetValueOrDefault(name);
                var value = change.Values[name];
                if (field == null || value == null || !IsTechniqueType(field.DataType))
                    continue;
                var defensive = field.DataType == DataFieldType.DefensiveTechnique;
                var ids = TechniqueIds(value);
                var kept = ids.Select(id => pool.Pooled(id, defensive)?.ExternalId).Where(id => id != null).Distinct().ToList();
                var missing = ids.Where(id => pool.Pooled(id, defensive) == null).ToList();
                if (missing.Count > 0)
                    warnings.Add($"Left out {string.Join(", ", missing)} from {name}{where}: not in the MSEL's {(defensive ? "defensive" : "attack")} techniques.");
                if (kept.Count > 0)
                    change.Values[name] = string.Join(", ", kept);
                else
                    change.Values.Remove(name);
            }
        }

        // what turning a text column into a technique column does to each inject's value, worked out on the change
        private static string ConvertToTechniques(MselChange change, DataFieldEntity field, MselAssistState state, TechniquePool pool)
        {
            var defensive = change.DataType == nameof(DataFieldType.DefensiveTechnique);
            var conversion = new TechniqueColumnConversion();
            var found = new HashSet<Guid>();
            var missing = new List<string>();
            var injects = 0;
            foreach (var (_, inject) in state.Injects)
            {
                var text = inject.DataValues.FirstOrDefault(v => v.DataFieldId == field.Id)?.Value;
                if (string.IsNullOrWhiteSpace(text))
                    continue;
                var kept = new List<string>();
                foreach (var id in TechniqueIds(DataValueDraftService.StripHtml(text)))
                {
                    var (technique, _) = pool.Find(id, null, defensive);
                    if (technique == null)
                    {
                        missing.Add(id);
                        continue;
                    }
                    if (!pool.Holds(technique))
                        conversion.TechniquesToAdd.Add(technique.Id);
                    pool.Add(technique);
                    found.Add(technique.Id);
                    if (!kept.Contains(technique.ExternalId))
                        kept.Add(technique.ExternalId);
                }
                conversion.Values[inject.Id] = kept.Count > 0 ? string.Join(", ", kept) : null;
                if (kept.Count > 0)
                    injects++;
            }
            change.Conversion = conversion;
            var label = $", reading the technique ids on {injects} injects: {found.Count} techniques, {conversion.TechniquesToAdd.Count} of them added to the MSEL's techniques";
            var unknown = missing.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (unknown.Count > 0)
                label += $"; not found, so dropped: {string.Join(", ", unknown.Take(10))}" + (unknown.Count > 10 ? $" and {unknown.Count - 10} more" : "");
            return label;
        }

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
            // every column by name, and the Gallery parameters in use, for the columns a proposal adds or renames
            var columns = msel.DataFields.Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var parameters = msel.DataFields.Where(f => !string.IsNullOrEmpty(f.GalleryArticleParameter))
                .ToDictionary(f => f.GalleryArticleParameter, f => f.Name, StringComparer.OrdinalIgnoreCase);
            var integrationsOn = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var pages = msel.Pages.Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            // competency links as "<team short name or empty>|<id number>"
            var links = msel.MselCompetencies.Select(mc => "|" + mc.Competency.IdNumber)
                .Concat(msel.Teams.SelectMany(t => t.TeamCompetencies.Select(tc => t.ShortName + "|" + tc.Competency.IdNumber)))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var pool = new TechniquePool(state);
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
                        TechniqueValues(change, fields, pool, "", warnings);
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
                        TechniqueValues(change, fields, pool, $" on {target.Reference ?? change.Target}", warnings);
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
                    case ColumnAdd:
                    {
                        change.DataType = ColumnType(change.DataType) ?? nameof(DataFieldType.String);
                        change.Options = CleanOptions(change.Options);
                        change.Label = $"Add column {change.Name} ({ColumnKind(change)})";
                        problem = change.Name == null ? "a new column needs a name"
                            : change.Options?.Count > 0 && change.DataType != nameof(DataFieldType.String) ? "only a text column can be chosen from a list"
                            : !columns.Add(change.Name) ? $"there is already a column {change.Name}"
                            : ParameterProblem(change, parameters, null);
                        // the injects after it in this proposal may write to it
                        if (problem == null)
                            fields[change.Name] = ColumnEntity(change, null);
                        break;
                    }
                    case ColumnUpdate:
                    {
                        var field = change.Target == null ? null : msel.DataFields.FirstOrDefault(f => f.Name.Equals(change.Target, StringComparison.OrdinalIgnoreCase));
                        if (field == null)
                        {
                            change.Label = $"Edit column {change.Target}";
                            problem = $"there is no column {change.Target}";
                            break;
                        }
                        change.Target = field.Name;
                        if (change.DataType != null)
                            change.DataType = ColumnType(change.DataType) == field.DataType.ToString() ? null : ColumnType(change.DataType);
                        change.Options = change.Options == null ? null : CleanOptions(change.Options);
                        if (change.Name != null && change.Name.Equals(field.Name, StringComparison.Ordinal))
                            change.Name = null;
                        if (change.OnExerciseView == field.OnExerciseView)
                            change.OnExerciseView = null;
                        if (change.OnEventList == field.OnScenarioEventList)
                            change.OnEventList = null;
                        if (string.Equals(change.GalleryParameter, field.GalleryArticleParameter, StringComparison.OrdinalIgnoreCase))
                            change.GalleryParameter = null;
                        var parts = new List<string>();
                        if (change.Name != null) parts.Add($"name \"{change.Name}\"");
                        if (change.DataType != null) parts.Add($"type {change.DataType}");
                        if (change.Options != null) parts.Add(change.Options.Count == 0 ? "no list" : $"choices {string.Join(", ", change.Options)}");
                        if (change.Description != null) parts.Add("description");
                        if (change.GalleryParameter != null) parts.Add($"Gallery {change.GalleryParameter}");
                        if (change.OnExerciseView != null) parts.Add(change.OnExerciseView.Value ? "on the Exercise View" : "off the Exercise View");
                        if (change.OnEventList != null) parts.Add(change.OnEventList.Value ? "on the event list" : "off the event list");
                        change.Label = $"Edit column {field.Name}" + (parts.Count > 0 ? ": " + string.Join(", ", parts) : "");
                        var type = change.DataType ?? field.DataType.ToString();
                        // a column of technique names becomes one of technique ids
                        var converting = change.DataType != null && IsTechniqueType(change.DataType) && !IsTechniqueType(field.DataType);
                        problem = parts.Count == 0 ? "it changes nothing"
                            : change.Options?.Count > 0 && type != nameof(DataFieldType.String) ? "only a text column can be chosen from a list"
                            : converting && field.DataType is not (DataFieldType.String or DataFieldType.Html) ? "only a text column can become a technique column"
                            : converting && access.Refusal(TechniqueAdd) != null ? "turning it into a technique column adds techniques to the MSEL, which only its owner can do"
                            : null;
                        if (problem == null && converting)
                        {
                            change.Options = null;
                            change.Label += ConvertToTechniques(change, field, state, pool);
                        }
                        if (problem == null && change.Name != null)
                        {
                            if (!columns.Add(change.Name))
                                problem = $"there is already a column {change.Name}";
                            else
                                columns.Remove(field.Name);
                        }
                        problem ??= ParameterProblem(change, parameters, field.Name);
                        if (problem == null && fields.Remove(field.Name))
                            fields[change.Name ?? field.Name] = ColumnEntity(change, field);
                        break;
                    }
                    case PageAdd:
                    {
                        change.Content = PageHtml(change.Content, warnings);
                        change.AllCanView ??= false;
                        change.Label = $"Add page {change.Name} ({(change.AllCanView.Value ? "participants can see it" : "controllers only")})";
                        problem = change.Name == null ? "a new page needs a name"
                            : string.IsNullOrWhiteSpace(DataValueDraftService.StripHtml(change.Content)) ? "a new page needs content"
                            : !pages.Add(change.Name) ? $"there is already a page {change.Name}" : null;
                        break;
                    }
                    case PageUpdate:
                    {
                        var page = change.Target == null ? null : msel.Pages.FirstOrDefault(p => p.Name.Equals(change.Target, StringComparison.OrdinalIgnoreCase));
                        if (page == null)
                        {
                            change.Label = $"Edit page {change.Target}";
                            problem = $"there is no page {change.Target}";
                            break;
                        }
                        change.Target = page.Name;
                        change.Content = change.Content == null ? null : PageHtml(change.Content, warnings);
                        if (change.Content == page.Content)
                            change.Content = null;
                        if (change.Name != null && change.Name.Equals(page.Name, StringComparison.Ordinal))
                            change.Name = null;
                        if (change.AllCanView == page.AllCanView)
                            change.AllCanView = null;
                        var parts = new List<string>();
                        if (change.Name != null) parts.Add($"name \"{change.Name}\"");
                        if (change.Content != null) parts.Add("content");
                        if (change.AllCanView != null) parts.Add(change.AllCanView.Value ? "participants can see it" : "controllers only");
                        change.Label = $"Edit page {page.Name}" + (parts.Count > 0 ? ": " + string.Join(", ", parts) : "");
                        // the model saw only the start of a long page, so its content would lose the rest
                        problem = parts.Count == 0 ? "it changes nothing"
                            : change.Content != null && (page.Content?.Length ?? 0) > MaxPageShown ? "the page is too long to edit here; use Write with AI on the page"
                            : change.Content != null && string.IsNullOrWhiteSpace(DataValueDraftService.StripHtml(change.Content)) ? "it would empty the page"
                            : null;
                        if (problem == null && change.Name != null)
                        {
                            if (!pages.Add(change.Name))
                                problem = $"there is already a page {change.Name}";
                            else
                                pages.Remove(page.Name);
                        }
                        break;
                    }
                    case IntegrationAdd:
                    {
                        change.Integration = Integrations.FirstOrDefault(i => i.Equals(change.Integration, StringComparison.OrdinalIgnoreCase));
                        change.Preset = new[] { MselDraftRules.MissionImpact, MselDraftRules.Likelihood, MselDraftRules.Nciss }
                            .FirstOrDefault(p => p.Equals(change.Preset, StringComparison.OrdinalIgnoreCase));
                        var label = change.Integration == "Cite" ? "CITE" : change.Integration;
                        change.Label = $"Turn on {label ?? "an integration"}";
                        if (change.Integration == null)
                        {
                            problem = "it names no integration Blueprint has";
                            break;
                        }
                        if (Uses(msel, change.Integration) || !integrationsOn.Add(change.Integration))
                        {
                            problem = $"the MSEL already uses {label}";
                            break;
                        }
                        if (MselService.HasIntegrations(msel))
                        {
                            problem = "the MSEL has been pushed, and its integrations change only after a pull";
                            break;
                        }
                        switch (change.Integration)
                        {
                            case "Gallery":
                                var added = GalleryColumns(msel);
                                foreach (var column in added.Where(c => !MselDraftRules.IsServerFilled(c)))
                                    fields[column.Name] = column;
                                if (added.Any())
                                    change.Label += $", adding the columns {string.Join(", ", added.Select(c => c.Name))}";
                                break;
                            case "Cite":
                                var authors = change.Preset != null && msel.ScoringModel == null && msel.CiteScoringModelId == null;
                                change.Label += ", giving the teams CITE team types" + (authors ? $" and authoring the {MselDraftRules.AssessmentName(change.Preset)} scoring model" : "");
                                if (!authors)
                                    change.Preset = null;
                                break;
                            case "Caster":
                                change.Label += " (choose its directory on the Info tab)";
                                break;
                            default:
                                change.Label += $" (set it up on its tab)";
                                break;
                        }
                        break;
                    }
                    case TechniqueAdd:
                    {
                        change.TechniqueId = null;
                        change.Label = $"Add technique {change.Technique}";
                        var (technique, why) = pool.Find(change.Technique, change.Domain, null);
                        if (technique == null)
                        {
                            problem = why;
                            break;
                        }
                        var release = technique.TechniqueRelease;
                        change.Technique = technique.ExternalId;
                        change.Domain = release.Domain;
                        change.TechniqueId = technique.Id;
                        change.Label = $"Add technique {technique.ExternalId} ({technique.Name}) from {release.Name} {release.Version}";
                        problem = pool.Holds(technique) ? $"{technique.ExternalId} is already in the MSEL's techniques" : null;
                        // the injects after it in this proposal may name it
                        if (problem == null && access.Refusal(change.Op) == null)
                            pool.Add(technique);
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

        // a page's HTML, kept to what pages show: no script, style or event handlers
        private static string PageHtml(string html, List<string> warnings) =>
            string.IsNullOrWhiteSpace(html) ? null : GalleryArticleHtml.Prepare(html, warnings);

        // the column type by its name; a type the assistant may not give a column is none
        private static string ColumnType(string value) =>
            ColumnTypes.FirstOrDefault(t => t.Equals(value?.Trim(), StringComparison.OrdinalIgnoreCase));

        private static string ColumnKind(MselChange change) =>
            change.Options?.Count > 0 ? $"list: {string.Join(", ", change.Options)}" : change.DataType;

        private static List<string> CleanOptions(List<string> options) =>
            options?.Select(o => o?.Trim()).Where(o => !string.IsNullOrEmpty(o)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        // a Gallery parameter that exists and is not another column's
        private static string ParameterProblem(MselChange change, Dictionary<string, string> parameters, string column)
        {
            if (change.GalleryParameter == null)
                return null;
            var parameter = Enum.GetNames<GalleryArticleParameter>().FirstOrDefault(p => p.Equals(change.GalleryParameter, StringComparison.OrdinalIgnoreCase));
            if (parameter == null)
                return $"{change.GalleryParameter} is not a Gallery article parameter";
            change.GalleryParameter = parameter;
            if (parameters.TryGetValue(parameter, out var owner) && !owner.Equals(column, StringComparison.OrdinalIgnoreCase))
                return $"the column {owner} already fills the Gallery {parameter}";
            if (column != null)
                foreach (var held in parameters.Where(p => p.Value.Equals(column, StringComparison.OrdinalIgnoreCase)).Select(p => p.Key).ToList())
                    parameters.Remove(held);
            parameters[parameter] = change.Name ?? column;
            return null;
        }

        // the column as the injects after it in a proposal see it, for checking their values
        private static DataFieldEntity ColumnEntity(MselChange change, DataFieldEntity field)
        {
            var options = change.Options ?? field?.DataOptions.OrderBy(o => o.DisplayOrder).Select(o => o.OptionValue).ToList() ?? new List<string>();
            return new DataFieldEntity
            {
                Id = field?.Id ?? Guid.NewGuid(),
                Name = change.Name ?? field?.Name,
                Description = change.Description ?? field?.Description,
                DataType = change.DataType != null ? Enum.Parse<DataFieldType>(change.DataType) : field?.DataType ?? DataFieldType.String,
                IsChosenFromList = options.Count > 0,
                DataOptions = options.Select((o, i) => new DataOptionEntity { OptionName = o, OptionValue = o, DisplayOrder = i + 1 }).ToList(),
                GalleryArticleParameter = change.GalleryParameter ?? field?.GalleryArticleParameter
            };
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
                // an empty value clears the column, as when the planner asks to take a source type off a controller's step
                else if (string.IsNullOrWhiteSpace(raw))
                    cleaned[field.Name] = null;
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
            ScenarioEventId = change?.ScenarioEventId,
            DataType = Text(change?.DataType),
            Options = change?.Options?.ToList(),
            GalleryParameter = Text(change?.GalleryParameter),
            OnExerciseView = change?.OnExerciseView,
            OnEventList = change?.OnEventList,
            Integration = Text(change?.Integration),
            Preset = Text(change?.Preset),
            Content = Text(change?.Content),
            AllCanView = change?.AllCanView,
            Technique = Text(change?.Technique),
            Domain = Text(change?.Domain)
        };

        private static string Text(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
