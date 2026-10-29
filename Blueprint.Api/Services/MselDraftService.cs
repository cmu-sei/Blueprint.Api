// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
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
    public interface IMselDraftService
    {
        /// <summary>Asks the model for a MSEL draft shaped by a template. Nothing is saved.</summary>
        Task<MselDraftResult> DraftAsync(MselDraftRequest request, CancellationToken ct);
    }

    public class MselDraftService : IMselDraftService
    {
        private const string ToolName = "submit_msel_draft";
        private readonly BlueprintContext _context;
        private readonly IAiModelClient _aiModelClient;
        private readonly AiOptions _aiOptions;
        private readonly ClaimsPrincipal _user;

        private const string SystemPrompt =
@"You draft Master Scenario Event Lists (MSELs) for Crucible cyber exercises in Blueprint.
A MSEL is organized as Move > Group > Inject. Moves are time-bounded phases of the exercise. Injects that share a delivery time form a group.
Write for an exercise planner who will review and edit every line before anything reaches participants.
Rules:
- Use fictional organizations, people, domains and IP addresses (RFC 5737 / RFC 1918 ranges, example.com style domains). Never use real personal data.
- Describe adversary activity at the level a defender would observe it. Never include working exploit code, malware, or real credentials.
- Every inject must have a clear purpose: what participants receive, who sends it, and what they are expected to do.
- Spread injects across the moves; the first move starts at minute 0.
- Use team and organization short names exactly as you define them, and only the columns you are given.
- Put any assumption you had to make, or question for the planner, in plannerNotes.
- Include an EXCON team for exercise control alongside the participant teams, unless the template already has one.
- The exercise runs on two clocks. The exercise clock is real time: startMinutes and each inject's minutes. The scenario clock is
  the story's date and time: each move's situationTime. The scenario clock can jump ahead between moves (move 2 is the next morning,
  move 3 is a week later) while the exercise clock runs on. Give every move a situationTime, in order, and date anything in the
  story (articles, emails, reports) on the scenario clock within its move.
- Write a read-in for participants to read before the first move: the scenario background, who they are and what they are
  responsible for, the organizations they will deal with, the rules of play, and how the two clocks work, with the scenario date
  and time the exercise opens. Use headings, short paragraphs, lists and a table where one helps; no images.
When the MSEL uses CITE:
- For each move, list the actions each participant team should take, as short imperative sentences the team checks off in CITE
  (for example: Isolate the affected hosts; Notify the state fusion center). Three to six actions per team per move.
When the MSEL uses Gallery:
- Most participant-facing injects are Gallery articles: news stories, social posts, emails, phone calls, intel and situation reports, orders.
  Pick the source type that matches the document, and name the outlet, account or office in the source name.
- From Org is the short name of the organization or team the article comes from. To Org is the team short names it goes to, or ALL.
- The summary is one plain-text sentence for the wall. The description or details column is the full document.
- Group the articles on 4 to 6 Gallery cards, one per source or thread participants follow: for example Exercise Control (read-in and
  facilitator messages), the organization under attack, Public and Media, Government and Law Enforcement, and the coordinating body.
  List the cards in the order the wall should show them, starting with Exercise Control, and put every Gallery article on one card.
Answer only by calling the tool.";

        public MselDraftService(BlueprintContext context, IAiModelClient aiModelClient, AiOptions aiOptions, IPrincipal user)
        {
            _context = context;
            _aiModelClient = aiModelClient;
            _aiOptions = aiOptions;
            _user = user as ClaimsPrincipal;
        }

        public async Task<MselDraftResult> DraftAsync(MselDraftRequest request, CancellationToken ct)
        {
            if (!_aiOptions.Enabled)
                throw new InvalidOperationException("AI drafting is not enabled on this server.");
            if (string.IsNullOrWhiteSpace(request.Prompt))
                throw new ArgumentException("Describe the exercise to draft.");

            var template = await LoadTemplateAsync(_context, _user.GetId(), request.TemplateMselId, ct);
            var competencies = await LoadCompetencyChoicesAsync(_context, template.Id, request.CompetencyFrameworkIds, ct);
            if (competencies.Count > _aiOptions.MaxCompetencies)
                throw new ArgumentException($"The chosen frameworks have {competencies.Count} competencies, more than the {_aiOptions.MaxCompetencies} a draft can choose from. Choose fewer frameworks.");
            var fields = MselDraftRules.DraftedFields(template).ToList();
            var schema = BuildSchema(fields, _aiOptions.MaxInjects, competencies, MselDraftRules.UsesCards(template), template.UseCite);
            var prompt = BuildPrompt(request.Prompt, template, fields, _aiOptions.MaxInjects, competencies);

            var input = await _aiModelClient.InvokeToolAsync(SystemPrompt, prompt, ToolName,
                "Submit the drafted MSEL for the planner to review.", schema, ct);
            var draft = input.Deserialize<MselDraft>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? new MselDraft();

            var result = new MselDraftResult
            {
                TemplateMselId = template.Id,
                TemplateName = template.Name,
                Draft = draft,
                Fields = fields.Select(f => new MselDraftField
                {
                    Name = f.Name,
                    DataType = f.DataType.ToString(),
                    Options = f.DataOptions.OrderBy(o => o.DisplayOrder).Select(o => o.OptionValue).ToList()
                }).ToList()
            };
            MselDraftRules.Normalize(draft, template, _aiOptions.MaxInjects, competencies, result.Warnings);
            result.Competencies = draft.Competencies
                .Select(id => new MselDraftCompetency { IdNumber = id, ShortName = competencies[id].ShortName })
                .ToList();
            return result;
        }

        /// <summary>
        /// The competencies a draft may assign, keyed by ID number: the leaves of the chosen frameworks,
        /// else the template's own MSEL competencies. Shared with import, which checks the same list.
        /// </summary>
        public static async Task<Dictionary<string, CompetencyEntity>> LoadCompetencyChoicesAsync(BlueprintContext context, Guid templateMselId, List<Guid> frameworkIds, CancellationToken ct)
        {
            List<CompetencyEntity> competencies;
            if (frameworkIds?.Any() == true)
            {
                // categories (Tasks, Skills, work role groups) are not something a participant demonstrates
                competencies = await context.Competencies
                    .AsNoTracking()
                    .Where(c => frameworkIds.Contains(c.CompetencyFrameworkId) && !c.Children.Any())
                    .OrderBy(c => c.CompetencyFramework.Name).ThenBy(c => c.SortOrder).ThenBy(c => c.IdNumber)
                    .ToListAsync(ct);
            }
            else
            {
                competencies = await context.MselCompetencies
                    .AsNoTracking()
                    .Where(mc => mc.MselId == templateMselId)
                    .Select(mc => mc.Competency)
                    .OrderBy(c => c.SortOrder).ThenBy(c => c.IdNumber)
                    .ToListAsync(ct);
            }
            // an ID number shared by two frameworks resolves to the first, as the planner sees it listed
            var choices = new Dictionary<string, CompetencyEntity>(StringComparer.OrdinalIgnoreCase);
            foreach (var competency in competencies.Where(c => !string.IsNullOrWhiteSpace(c.IdNumber)))
                choices.TryAdd(competency.IdNumber.Trim(), competency);
            return choices;
        }

        /// <summary>Loads a template with its columns, teams and organizations, if the user may draft from it</summary>
        public static async Task<MselEntity> LoadTemplateAsync(BlueprintContext context, Guid userId, Guid templateMselId, CancellationToken ct)
        {
            var template = await context.Msels
                .AsNoTracking()
                .Include(m => m.DataFields)
                .ThenInclude(df => df.DataOptions)
                .Include(m => m.Teams)
                .Include(m => m.Organizations)
                .Include(m => m.Cards)
                .AsSplitQuery()
                .SingleOrDefaultAsync(m => m.Id == templateMselId, ct);
            if (template == null)
                throw new EntityNotFoundException<MselEntity>("MSEL not found with ID=" + templateMselId);
            // the caller holds CreateMsels, which is what lets them view a template
            if (!await MselViewRequirement.IsMet(userId, templateMselId, true, context))
                throw new ForbiddenException();
            // untracked, so the columns added here go into the draft and never into the template
            MselDraftRules.AddGalleryFields(template);
            return template;
        }

        // an enum makes the model's choice exact, but a whole framework would double the prompt
        private const int MaxCompetencyEnum = 200;

        private static JsonObject BuildSchema(List<DataFieldEntity> fields, int maxInjects, Dictionary<string, CompetencyEntity> competencies, bool useCards, bool useCite)
        {
            var valueProperties = new JsonObject();
            foreach (var field in fields)
            {
                var property = new JsonObject
                {
                    ["type"] = "string",
                    ["description"] = MselDraftRules.Describe(field)
                };
                var options = MselDraftRules.AllowedOptions(field);
                if (options != null)
                    property["enum"] = new JsonArray(options.Select(o => (JsonNode)o).ToArray());
                valueProperties[field.Name] = property;
            }

            JsonObject Str(string description) => new JsonObject { ["type"] = "string", ["description"] = description };
            JsonObject Int(string description) => new JsonObject { ["type"] = "integer", ["minimum"] = 0, ["description"] = description };
            JsonObject Obj(JsonObject properties, params string[] required) => new JsonObject
            {
                ["type"] = "object",
                ["properties"] = properties,
                ["required"] = new JsonArray(required.Select(r => (JsonNode)r).ToArray())
            };
            JsonObject Arr(JsonObject items, string description) => new JsonObject { ["type"] = "array", ["description"] = description, ["items"] = items };
            JsonObject CompetencyId()
            {
                var id = Str("An ID number from the competency list");
                if (competencies.Count <= MaxCompetencyEnum)
                    id["enum"] = new JsonArray(competencies.Keys.Select(k => (JsonNode)k).ToArray());
                return id;
            }
            void AddCompetencies(JsonObject properties, string description)
            {
                if (competencies.Any())
                    properties["competencies"] = Arr(CompetencyId(), description);
            }

            var teamProperties = new JsonObject
            {
                ["name"] = Str("Participant team name"),
                ["shortName"] = Str("Short name used in team columns, no commas"),
                ["description"] = Str("Role of the team")
            };
            AddCompetencies(teamProperties, "Competencies the team is expected to demonstrate");
            var injectProperties = new JsonObject
            {
                ["minutes"] = Int("Delivery time in minutes from exercise start"),
                ["eventType"] = new JsonObject
                {
                    ["type"] = "string",
                    ["enum"] = new JsonArray("Inject", "Information", "Facilitation"),
                    ["description"] = "Inject goes to participants; Information is background for controllers; Facilitation is a controller action"
                },
                ["values"] = new JsonObject
                {
                    ["type"] = "object",
                    ["description"] = "Column values keyed by column name",
                    ["properties"] = valueProperties
                }
            };
            AddCompetencies(injectProperties, "Competencies participants demonstrate by acting on this inject; empty for injects that only set the scene");
            if (useCards)
                injectProperties["card"] = Str("The name of the Gallery card the article goes on, exactly as listed in cards; empty if the inject is not a Gallery article");

            var properties = new JsonObject
            {
                ["name"] = Str("Short exercise name"),
                ["description"] = Str("One paragraph: scenario, audience and objectives"),
                ["durationMinutes"] = Int("Total exercise length in minutes"),
                ["moves"] = Arr(Obj(new JsonObject
                {
                    ["moveNumber"] = Int("0, 1, 2 ... in time order"),
                    ["description"] = Str("Move title, e.g. Initial Detection"),
                    ["situationDescription"] = Str("The situation at the start of the move, as briefed to participants"),
                    ["startMinutes"] = Int("Minutes from exercise start; the first move is 0"),
                    ["situationTime"] = Str("The scenario date and time when the move opens, in ISO 8601 UTC, e.g. 2026-03-04T08:00:00Z; later than the move before")
                }, "moveNumber", "description", "situationDescription", "startMinutes", "situationTime"), "Phases of the exercise in time order"),
                ["teams"] = Arr(Obj(teamProperties, "name", "shortName"), "Participant teams; reuse the template's teams when they fit"),
                ["organizations"] = Arr(Obj(new JsonObject
                {
                    ["name"] = Str("Fictional organization name"),
                    ["shortName"] = Str("Short name used in organization columns, no commas"),
                    ["description"] = Str("Who they are in the scenario")
                }, "name", "shortName"), "Simulated organizations that send or receive injects (exercise control, adversary, press, agencies)"),
                ["injects"] = new JsonObject
                {
                    ["type"] = "array",
                    ["maxItems"] = maxInjects,
                    ["description"] = "MSEL rows in time order",
                    ["items"] = Obj(injectProperties, "minutes", "eventType", "values")
                },
                ["readIn"] = Str("The participants' read-in, in HTML: background, roles, organizations, rules of play, the two clocks"),
                ["plannerNotes"] = Arr(Str("Assumption or open question"), "Things the planner should check")
            };
            if (useCite)
                properties["citeActions"] = Arr(Obj(new JsonObject
                {
                    ["moveNumber"] = Int("The move the action belongs to"),
                    ["team"] = Str("The short name of the participant team that takes the action"),
                    ["description"] = Str("The action, as a short imperative sentence")
                }, "moveNumber", "team", "description"), "What each participant team should do in each move, for CITE");
            if (useCards)
                properties["cards"] = Arr(Obj(new JsonObject
                {
                    ["name"] = Str("Card name shown on the Gallery wall, e.g. Public and Media"),
                    ["description"] = Str("One sentence: whose articles or which thread the card holds")
                }, "name", "description"), "Gallery cards in wall order, starting with Exercise Control; reuse the template's cards when they fit");
            AddCompetencies(properties, "Every competency the exercise lets participants demonstrate");
            return Obj(properties, "name", "description", "durationMinutes", "moves", "teams", "organizations", "injects", "readIn");
        }

        public static string BuildPrompt(string plannerPrompt, MselEntity template, List<DataFieldEntity> fields, int maxInjects, Dictionary<string, CompetencyEntity> competencies)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Template: {template.Name}");
            if (!string.IsNullOrWhiteSpace(template.Description))
                sb.AppendLine($"Template description: {template.Description}");
            var integrations = new List<string>();
            if (template.UsePlayer) integrations.Add("Player");
            if (template.UseGallery) integrations.Add("Gallery (news, social, email and intel articles)");
            if (template.UseCite) integrations.Add("CITE (team assessments)");
            if (template.UseSteamfitter) integrations.Add("Steamfitter (automated VM tasks)");
            if (template.UseCaster) integrations.Add("Caster (deployed range)");
            if (integrations.Any())
                sb.AppendLine($"Integrations: {string.Join("; ", integrations)}");
            if (template.Teams.Any())
                sb.AppendLine($"Template teams (short names): {string.Join(", ", template.Teams.Select(t => t.ShortName))}");
            if (template.Organizations.Any())
                sb.AppendLine($"Template organizations (short names): {string.Join(", ", template.Organizations.Select(o => o.ShortName))}");
            if (MselDraftRules.UsesCards(template) && template.Cards.Any())
                sb.AppendLine($"Template Gallery cards: {string.Join("; ", template.Cards.OrderBy(c => c.Move).ThenBy(c => c.Inject).Select(c => $"{c.Name} ({c.Description})"))}");
            sb.AppendLine($"Columns to fill for each inject: {string.Join(", ", fields.Select(f => f.Name))}");
            sb.AppendLine($"Write at most {maxInjects} injects.");
            if (competencies.Any())
            {
                sb.AppendLine();
                sb.AppendLine("Assign competencies only from this list (ID: name). Tag each inject with what participants demonstrate by acting on it, each team with what it is expected to demonstrate, and list every one you used for the MSEL.");
                foreach (var (id, competency) in competencies)
                    sb.AppendLine($"{id}: {Shorten(competency.ShortName ?? competency.Description, 120)}");
            }
            sb.AppendLine();
            sb.AppendLine("The planner's request:");
            sb.AppendLine(plannerPrompt.Trim());
            return sb.ToString();
        }

        private static string Shorten(string value, int length)
        {
            value = (value ?? "").Trim();
            return value.Length <= length ? value : value[..length] + "...";
        }
    }

    /// <summary>
    /// How a draft maps onto a template's columns. Shared by drafting and by import, because the
    /// planner can edit the draft in between.
    /// </summary>
    public static class MselDraftRules
    {
        private static readonly string[] _derivedIntegerFields = ["move", "group"];

        /// <summary>Columns the server fills, so the model is not asked for them</summary>
        public static bool IsServerFilled(DataFieldEntity field) =>
            field.DataType switch
            {
                // move and group follow from the delivery time in Blueprint
                DataFieldType.Integer => _derivedIntegerFields.Contains(field.Name.Trim().ToLower()),
                DataFieldType.DateTime => field.Name.Contains("delivery", StringComparison.OrdinalIgnoreCase),
                DataFieldType.Status => true,
                DataFieldType.User => true,
                DataFieldType.Card => true,
                // filled from each inject's competencies, which are checked against the list
                DataFieldType.Competency => true,
                _ => false
            };

        // The article parameters a Gallery push needs, mapped as the exercises built with the blueprint-ttx loader map
        // them: a short Description is the article's summary and the Details are its body. Each lists the column names
        // that mean it, the first being the one added when none exists, and the column types that can carry it.
        private static readonly (GalleryArticleParameter Parameter, string[] Names, DataFieldType[] Types)[] _galleryFields =
        [
            (GalleryArticleParameter.Name, ["Title", "Name"], [DataFieldType.String]),
            (GalleryArticleParameter.Summary, ["Description", "Summary"], [DataFieldType.String, DataFieldType.Html]),
            (GalleryArticleParameter.Description, ["Details"], [DataFieldType.Html, DataFieldType.String]),
            (GalleryArticleParameter.FromOrg, ["From Org"], [DataFieldType.Organization]),
            (GalleryArticleParameter.ToOrg, ["To Org"], [DataFieldType.TeamsMultiple]),
            (GalleryArticleParameter.SourceType, ["SourceType", "Source Type"], [DataFieldType.SourceType]),
            (GalleryArticleParameter.SourceName, ["Source Name"], [DataFieldType.String]),
            (GalleryArticleParameter.DatePosted, ["Simulated Time", "Date Posted"], [DataFieldType.DateTime]),
            (GalleryArticleParameter.CardId, ["Card"], [DataFieldType.Card])
        ];

        /// <summary>
        /// A MSEL that uses Gallery needs columns mapped to the article parameters, or its injects never become
        /// articles. Maps a column the template already has under one of the parameter's names, and adds a column
        /// for any still missing. Names are fixed, so the template a draft was written from and the MSEL it is
        /// imported into match.
        /// </summary>
        public static void AddGalleryFields(MselEntity msel)
        {
            if (!msel.UseGallery)
                return;
            var mapped = new HashSet<string>(msel.DataFields.Select(f => f.GalleryArticleParameter).Where(p => !string.IsNullOrEmpty(p)));
            var displayOrder = msel.DataFields.Select(f => f.DisplayOrder).DefaultIfEmpty(0).Max();
            foreach (var (parameter, names, types) in _galleryFields.Where(g => !mapped.Contains(g.Parameter.ToString())))
            {
                var existing = names
                    .Select(n => msel.DataFields.FirstOrDefault(f => string.IsNullOrEmpty(f.GalleryArticleParameter)
                        && types.Contains(f.DataType)
                        && f.Name.Trim().Equals(n, StringComparison.OrdinalIgnoreCase)))
                    .FirstOrDefault(f => f != null);
                if (existing != null)
                {
                    existing.GalleryArticleParameter = parameter.ToString();
                    continue;
                }
                var name = names[0];
                msel.DataFields.Add(new DataFieldEntity
                {
                    Id = Guid.NewGuid(),
                    MselId = msel.Id,
                    Name = msel.DataFields.Any(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) ? $"Gallery {name}" : name,
                    DataType = types[0],
                    DisplayOrder = ++displayOrder,
                    GalleryArticleParameter = parameter.ToString()
                });
            }
            // an article's body is a styled document, which a plain text column would show as markup
            foreach (var body in msel.DataFields.Where(f => f.GalleryArticleParameter == nameof(GalleryArticleParameter.Description) && f.DataType == DataFieldType.String))
                body.DataType = DataFieldType.Html;
        }

        /// <summary>Whether the draft puts Gallery articles on cards: the MSEL uses Gallery and has a column for the card</summary>
        public static bool UsesCards(MselEntity template) =>
            template.UseGallery && template.DataFields.Any(f => f.GalleryArticleParameter == nameof(GalleryArticleParameter.CardId));

        public static IEnumerable<DataFieldEntity> DraftedFields(MselEntity template) =>
            template.DataFields
                .Where(f => !IsServerFilled(f))
                .OrderBy(f => f.DisplayOrder);

        public static List<string> AllowedOptions(DataFieldEntity field)
        {
            if (field.IsChosenFromList && field.DataOptions.Any())
                return field.DataOptions.OrderBy(o => o.DisplayOrder).Select(o => o.OptionValue).ToList();
            if (field.DataType == DataFieldType.SourceType)
                return Enum.GetNames(typeof(GallerySourceType)).ToList();
            if (field.DataType == DataFieldType.Boolean || field.DataType == DataFieldType.Checkbox)
                return ["true", "false"];
            return null;
        }

        public static string Describe(DataFieldEntity field)
        {
            var description = field.DataType switch
            {
                DataFieldType.Html => "HTML using p, ul, ol, li, strong and em only",
                DataFieldType.Integer => "A whole number",
                DataFieldType.Double => "A number",
                DataFieldType.DateTime => "Simulated date and time in ISO 8601 UTC, e.g. 2026-03-04T14:30:00Z",
                DataFieldType.Organization => "The short name of one organization or team",
                DataFieldType.Team => "The short name of one team",
                DataFieldType.TeamsMultiple => "Team short names separated by \", \", or ALL",
                DataFieldType.Url => "A URL on a fictional domain",
                _ => "Plain text"
            };
            if (!string.IsNullOrWhiteSpace(field.Description))
                description = $"{field.Description}. {description}";
            if (!string.IsNullOrWhiteSpace(field.GalleryArticleParameter))
                description += $". Used as the Gallery article {field.GalleryArticleParameter}";
            return description;
        }

        /// <summary>
        /// Puts a draft into a shape import can trust: known columns and names only, times in range,
        /// at most maxInjects rows. Every correction is reported.
        /// </summary>
        public static void Normalize(MselDraft draft, MselEntity template, int maxInjects, IReadOnlyDictionary<string, CompetencyEntity> competencies, List<string> warnings)
        {
            draft.Name = string.IsNullOrWhiteSpace(draft.Name) ? "AI draft" : draft.Name.Trim();
            draft.Moves ??= new List<MselDraftMove>();
            draft.Teams ??= new List<MselDraftTeam>();
            draft.Organizations ??= new List<MselDraftOrganization>();
            draft.Injects ??= new List<MselDraftInject>();
            draft.Cards ??= new List<MselDraftCard>();
            draft.Competencies ??= new List<string>();
            draft.PlannerNotes ??= new List<string>();
            draft.CiteActions ??= new List<MselDraftCiteAction>();

            // teams and organizations: unique short names, without the commas that separate team lists
            draft.Teams = UniqueByShortName(draft.Teams, t => t.ShortName, (t, s) => t.ShortName = s, t => t.Name, warnings, "team");
            draft.Organizations = UniqueByShortName(draft.Organizations, o => o.ShortName, (o, s) => o.ShortName = s, o => o.Name, warnings, "organization");
            var teamNames = new HashSet<string>(template.Teams.Select(t => t.ShortName).Concat(draft.Teams.Select(t => t.ShortName)), StringComparer.OrdinalIgnoreCase);
            var orgNames = new HashSet<string>(template.Organizations.Select(o => o.ShortName).Concat(draft.Organizations.Select(o => o.ShortName)).Concat(teamNames), StringComparer.OrdinalIgnoreCase);

            // cards: unique names in wall order; an article may name one of these or one of the template's
            var cardNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (UsesCards(template))
            {
                foreach (var card in template.Cards)
                    cardNames.TryAdd(card.Name.Trim(), card.Name.Trim());
                var cards = new List<MselDraftCard>();
                foreach (var card in draft.Cards.Where(c => !string.IsNullOrWhiteSpace(c?.Name)))
                {
                    card.Name = card.Name.Trim();
                    if (cards.Any(c => c.Name.Equals(card.Name, StringComparison.OrdinalIgnoreCase)))
                    {
                        warnings.Add($"Dropped the second card named \"{card.Name}\".");
                        continue;
                    }
                    cards.Add(card);
                    cardNames.TryAdd(card.Name, card.Name);
                }
                draft.Cards = cards;
            }
            else
            {
                draft.Cards = new List<MselDraftCard>();
            }

            // moves: unique numbers in time order, and one at minute 0 so every inject has a move
            draft.Moves = draft.Moves
                .GroupBy(m => m.MoveNumber).Select(g => g.First())
                .OrderBy(m => m.StartMinutes).ThenBy(m => m.MoveNumber)
                .ToList();
            if (!draft.Moves.Any() || draft.Moves[0].StartMinutes > 0)
            {
                draft.Moves.Insert(0, new MselDraftMove { MoveNumber = 0, Description = "Start of exercise", StartMinutes = 0 });
                warnings.Add("Added a move at minute 0 so every inject belongs to a move.");
            }
            // the scenario clock: each move's time in UTC, never earlier than the move before
            DateTime? previousTime = null;
            foreach (var move in draft.Moves)
            {
                var time = ParseUtc(move.SituationTime);
                if (time == null && !string.IsNullOrWhiteSpace(move.SituationTime))
                    warnings.Add($"Move {move.MoveNumber}'s scenario time \"{move.SituationTime.Trim()}\" is not a date and time; left blank.");
                else if (time < previousTime)
                {
                    warnings.Add($"Move {move.MoveNumber}'s scenario time is earlier than the move before; left blank.");
                    time = null;
                }
                move.SituationTime = time?.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
                previousTime = time ?? previousTime;
            }

            // the read-in becomes a page, which shows on the same white paper as the articles in either theme
            if (string.IsNullOrWhiteSpace(draft.ReadIn))
                draft.ReadIn = null;
            else
            {
                var readInNotes = new List<string>();
                draft.ReadIn = GalleryArticleHtml.Prepare(draft.ReadIn.Trim(), readInNotes, GalleryArticleHtml.ArticleWidth);
                warnings.AddRange(readInNotes.Select(n => $"Read-in: {n}"));
            }

            if (draft.Injects.Count > maxInjects)
            {
                warnings.Add($"Kept the first {maxInjects} of {draft.Injects.Count} injects.");
                draft.Injects = draft.Injects.Take(maxInjects).ToList();
            }

            var fields = DraftedFields(template).ToDictionary(f => f.Name, StringComparer.OrdinalIgnoreCase);
            var droppedColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var unknownCards = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var inject in draft.Injects)
            {
                inject.Minutes = Math.Max(0, inject.Minutes);
                if (!Enum.TryParse<EventType>(inject.EventType, true, out var eventType) || !Enum.IsDefined(eventType))
                    inject.EventType = EventType.Inject.ToString();
                var values = new Dictionary<string, string>();
                foreach (var (key, raw) in inject.Values ?? new Dictionary<string, string>())
                {
                    if (!fields.TryGetValue(key, out var field))
                    {
                        droppedColumns.Add(key);
                        continue;
                    }
                    var value = CleanValue(field, raw?.Trim(), teamNames, orgNames, warnings);
                    if (!string.IsNullOrEmpty(value))
                        values[field.Name] = value;
                }
                inject.Values = values;
                if (string.IsNullOrWhiteSpace(inject.Card))
                    inject.Card = null;
                else if (cardNames.TryGetValue(inject.Card.Trim(), out var cardName))
                    inject.Card = cardName;
                else
                {
                    unknownCards.Add(inject.Card.Trim());
                    inject.Card = null;
                }
            }
            draft.Injects = draft.Injects.OrderBy(i => i.Minutes).ToList();
            if (unknownCards.Any())
                warnings.Add($"Took injects off cards the draft does not have: {string.Join(", ", unknownCards)}.");
            if (droppedColumns.Any())
                warnings.Add($"Dropped values for columns the template does not have: {string.Join(", ", droppedColumns)}.");

            // an article dated outside its move's stretch of the scenario clock reads out of order on the wall, so it is
            // dated as far into the move's scenario time as it is into the move's exercise time; an undated one is too
            var postedField = fields.Values.FirstOrDefault(f => f.GalleryArticleParameter == nameof(GalleryArticleParameter.DatePosted));
            if (postedField != null)
            {
                var redated = new SortedSet<int>();
                var dated = 0;
                foreach (var inject in draft.Injects)
                {
                    var index = draft.Moves.FindLastIndex(m => m.StartMinutes <= inject.Minutes);
                    var move = draft.Moves[index];
                    if (ParseUtc(move.SituationTime) is not DateTime opens)
                        continue;
                    var closes = index + 1 < draft.Moves.Count ? ParseUtc(draft.Moves[index + 1].SituationTime) : null;
                    var hasDate = inject.Values.TryGetValue(postedField.Name, out var posted);
                    if (!hasDate && !ArticleFields.IsArticle(template, inject.Values))
                        continue;
                    var postedTime = ParseUtc(posted);
                    if (postedTime >= opens && !(postedTime >= closes))
                        continue;
                    var time = opens.AddMinutes(inject.Minutes - move.StartMinutes);
                    inject.Values[postedField.Name] = (time >= closes ? opens : time).ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
                    if (hasDate)
                        redated.Add(move.MoveNumber);
                    else
                        dated++;
                }
                if (redated.Any())
                    warnings.Add($"Moved {postedField.Name} into the move's scenario time for articles in move {string.Join(", ", redated)}.");
                if (dated > 0)
                    warnings.Add($"Dated {dated} undated {(dated == 1 ? "article" : "articles")} by {(dated == 1 ? "its" : "their")} move's scenario time.");
            }

            // CITE actions: a team and move the draft has, a description, and no repeats
            if (!template.UseCite)
                draft.CiteActions = new List<MselDraftCiteAction>();
            else
            {
                var moveNumbers = new HashSet<int>(draft.Moves.Select(m => m.MoveNumber));
                var actions = new List<MselDraftCiteAction>();
                var droppedActions = 0;
                foreach (var action in draft.CiteActions.Where(a => a != null))
                {
                    action.Description = action.Description?.Trim();
                    action.Team = teamNames.FirstOrDefault(n => n.Equals(action.Team?.Trim(), StringComparison.OrdinalIgnoreCase));
                    if (action.Team == null || !moveNumbers.Contains(action.MoveNumber) || string.IsNullOrEmpty(action.Description) ||
                        actions.Any(a => a.MoveNumber == action.MoveNumber && a.Team == action.Team && a.Description.Equals(action.Description, StringComparison.OrdinalIgnoreCase)))
                    {
                        droppedActions++;
                        continue;
                    }
                    actions.Add(action);
                }
                if (droppedActions > 0)
                    warnings.Add($"Dropped {droppedActions} CITE {(droppedActions == 1 ? "action" : "actions")} with an unknown team or move, no description, or a repeat.");
                // OrderBy is stable, so each team's actions keep the model's order
                draft.CiteActions = actions.OrderBy(a => a.MoveNumber).ToList();
            }

            var lastInject = draft.Injects.Any() ? draft.Injects.Max(i => i.Minutes) : 0;
            var lastMove = draft.Moves.Max(m => m.StartMinutes);
            if (draft.DurationMinutes <= Math.Max(lastInject, lastMove))
            {
                draft.DurationMinutes = Math.Max(lastInject, lastMove) + 30;
                warnings.Add($"Set the duration to {draft.DurationMinutes} minutes to cover the last inject.");
            }

            // competencies: only the ones the draft could choose from, and the MSEL holds every one used
            var unknownCompetencies = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            List<string> KnownCompetencies(List<string> ids)
            {
                var known = new List<string>();
                foreach (var id in (ids ?? new List<string>()).Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => c.Trim()))
                {
                    if (!competencies.TryGetValue(id, out var competency))
                        unknownCompetencies.Add(id);
                    else if (!known.Contains(competency.IdNumber, StringComparer.OrdinalIgnoreCase))
                        known.Add(competency.IdNumber.Trim());
                }
                return known;
            }
            foreach (var inject in draft.Injects)
                inject.Competencies = KnownCompetencies(inject.Competencies);
            foreach (var team in draft.Teams)
                team.Competencies = KnownCompetencies(team.Competencies);
            draft.Competencies = KnownCompetencies(draft.Competencies
                .Concat(draft.Injects.SelectMany(i => i.Competencies))
                .Concat(draft.Teams.SelectMany(t => t.Competencies))
                .ToList());
            if (unknownCompetencies.Any())
                warnings.Add($"Dropped competencies that are not in the list the draft could choose from: {string.Join(", ", unknownCompetencies)}.");
            if (draft.Injects.Any(i => i.Competencies.Any()) && !template.DataFields.Any(f => f.DataType == DataFieldType.Competency))
                warnings.Add("The template has no Competency column, so each inject's competencies are kept only on the MSEL.");
        }

        public static string CleanValue(DataFieldEntity field, string value, HashSet<string> teamNames, HashSet<string> orgNames, List<string> warnings)
        {
            if (string.IsNullOrEmpty(value))
                return null;
            var options = AllowedOptions(field);
            if (options != null)
            {
                var match = options.FirstOrDefault(o => string.Equals(o, value, StringComparison.OrdinalIgnoreCase));
                if (match == null)
                    warnings.Add($"\"{value}\" is not an option for {field.Name}; left blank.");
                return match;
            }
            switch (field.DataType)
            {
                case DataFieldType.TeamsMultiple:
                    if (value.Equals("ALL", StringComparison.OrdinalIgnoreCase))
                        return "ALL";
                    var teams = value.Split(',').Select(t => t.Trim()).Where(t => t.Length > 0).ToList();
                    var unknown = teams.Where(t => !teamNames.Contains(t)).ToList();
                    if (unknown.Any())
                        warnings.Add($"Removed unknown teams from {field.Name}: {string.Join(", ", unknown)}.");
                    var known = teams.Where(teamNames.Contains).Select(t => teamNames.First(n => n.Equals(t, StringComparison.OrdinalIgnoreCase))).Distinct().ToList();
                    return known.Any() ? string.Join(", ", known) : null;
                case DataFieldType.Team:
                    return Known(teamNames, value, field, warnings);
                case DataFieldType.Organization:
                    return Known(orgNames, value, field, warnings);
                case DataFieldType.Integer:
                    return int.TryParse(value, out var i) ? i.ToString() : null;
                case DataFieldType.Double:
                    return double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d.ToString(System.Globalization.CultureInfo.InvariantCulture) : null;
                case DataFieldType.DateTime:
                    return ParseUtc(value)?.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
                default:
                    return value;
            }
        }

        /// <summary>
        /// A page of profiles, as the exercises built with blueprint-ttx have: a heading, a line on what the page is, then
        /// each team's or organization's profile under its name, on white paper. Null when none has a profile.
        /// </summary>
        public static string ProfilesPage(string title, string intro, IEnumerable<(string Heading, string Profile)> profiles)
        {
            var articles = profiles
                .Where(p => !string.IsNullOrWhiteSpace(p.Profile))
                .Select(p => $"<br><hr><br><article><h3>{WebUtility.HtmlEncode(p.Heading)}</h3>" +
                    (_markup.IsMatch(p.Profile) ? p.Profile.Trim() : $"<p>{WebUtility.HtmlEncode(p.Profile.Trim())}</p>") + "</article>")
                .ToList();
            if (!articles.Any())
                return null;
            var html = $"<section><h2>{WebUtility.HtmlEncode(title)}</h2><p>{WebUtility.HtmlEncode(intro)}</p>{string.Concat(articles)}</section>";
            return GalleryArticleHtml.Prepare(html, null, GalleryArticleHtml.ArticleWidth);
        }

        private static readonly System.Text.RegularExpressions.Regex _markup = new(@"<[a-zA-Z]");

        /// <summary>A date and time in UTC; one without a zone is taken as UTC</summary>
        public static DateTime? ParseUtc(string value) =>
            DateTime.TryParse(value?.Trim(), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var time)
                ? time : null;

        private static string Known(HashSet<string> names, string value, DataFieldEntity field, List<string> warnings)
        {
            var match = names.FirstOrDefault(n => n.Equals(value, StringComparison.OrdinalIgnoreCase));
            if (match == null)
                warnings.Add($"\"{value}\" is not a known short name for {field.Name}; left blank.");
            return match;
        }

        private static List<T> UniqueByShortName<T>(List<T> items, Func<T, string> getShortName, Action<T, string> setShortName, Func<T, string> getName, List<string> warnings, string kind)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var kept = new List<T>();
            foreach (var item in items)
            {
                var shortName = (getShortName(item) ?? getName(item) ?? "").Replace(",", "").Trim();
                if (shortName.Length == 0 || !seen.Add(shortName))
                {
                    warnings.Add($"Dropped a duplicate or unnamed {kind}: \"{getName(item)}\".");
                    continue;
                }
                setShortName(item, shortName);
                kept.Add(item);
            }
            return kept;
        }
    }
}
