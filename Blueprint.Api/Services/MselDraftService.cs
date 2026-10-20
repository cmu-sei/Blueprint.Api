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
            var fields = MselDraftRules.DraftedFields(template).ToList();
            var schema = BuildSchema(fields, _aiOptions.MaxInjects);
            var prompt = BuildPrompt(request.Prompt, template, fields, _aiOptions.MaxInjects);

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
            MselDraftRules.Normalize(draft, template, _aiOptions.MaxInjects, result.Warnings);
            return result;
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
                .AsSplitQuery()
                .SingleOrDefaultAsync(m => m.Id == templateMselId, ct);
            if (template == null)
                throw new EntityNotFoundException<MselEntity>("MSEL not found with ID=" + templateMselId);
            // the caller holds CreateMsels, which is what lets them view a template
            if (!await MselViewRequirement.IsMet(userId, templateMselId, true, context))
                throw new ForbiddenException();
            return template;
        }

        private static JsonObject BuildSchema(List<DataFieldEntity> fields, int maxInjects)
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

            return Obj(new JsonObject
            {
                ["name"] = Str("Short exercise name"),
                ["description"] = Str("One paragraph: scenario, audience and objectives"),
                ["durationMinutes"] = Int("Total exercise length in minutes"),
                ["moves"] = Arr(Obj(new JsonObject
                {
                    ["moveNumber"] = Int("0, 1, 2 ... in time order"),
                    ["description"] = Str("Move title, e.g. Initial Detection"),
                    ["situationDescription"] = Str("The situation at the start of the move, as briefed to participants"),
                    ["startMinutes"] = Int("Minutes from exercise start; the first move is 0")
                }, "moveNumber", "description", "situationDescription", "startMinutes"), "Phases of the exercise in time order"),
                ["teams"] = Arr(Obj(new JsonObject
                {
                    ["name"] = Str("Participant team name"),
                    ["shortName"] = Str("Short name used in team columns, no commas"),
                    ["description"] = Str("Role of the team")
                }, "name", "shortName"), "Participant teams; reuse the template's teams when they fit"),
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
                    ["items"] = Obj(new JsonObject
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
                    }, "minutes", "eventType", "values")
                },
                ["competencies"] = Arr(Str("A DCWF or NICE ID number, e.g. 511 or T0161"), "Competencies the exercise lets participants demonstrate"),
                ["plannerNotes"] = Arr(Str("Assumption or open question"), "Things the planner should check")
            }, "name", "description", "durationMinutes", "moves", "teams", "organizations", "injects");
        }

        private static string BuildPrompt(string plannerPrompt, MselEntity template, List<DataFieldEntity> fields, int maxInjects)
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
            sb.AppendLine($"Columns to fill for each inject: {string.Join(", ", fields.Select(f => f.Name))}");
            sb.AppendLine($"Write at most {maxInjects} injects.");
            sb.AppendLine();
            sb.AppendLine("The planner's request:");
            sb.AppendLine(plannerPrompt.Trim());
            return sb.ToString();
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
                _ => false
            };

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
        public static void Normalize(MselDraft draft, MselEntity template, int maxInjects, List<string> warnings)
        {
            draft.Name = string.IsNullOrWhiteSpace(draft.Name) ? "AI draft" : draft.Name.Trim();
            draft.Moves ??= new List<MselDraftMove>();
            draft.Teams ??= new List<MselDraftTeam>();
            draft.Organizations ??= new List<MselDraftOrganization>();
            draft.Injects ??= new List<MselDraftInject>();
            draft.Competencies ??= new List<string>();
            draft.PlannerNotes ??= new List<string>();

            // teams and organizations: unique short names, without the commas that separate team lists
            draft.Teams = UniqueByShortName(draft.Teams, t => t.ShortName, (t, s) => t.ShortName = s, t => t.Name, warnings, "team");
            draft.Organizations = UniqueByShortName(draft.Organizations, o => o.ShortName, (o, s) => o.ShortName = s, o => o.Name, warnings, "organization");
            var teamNames = new HashSet<string>(template.Teams.Select(t => t.ShortName).Concat(draft.Teams.Select(t => t.ShortName)), StringComparer.OrdinalIgnoreCase);
            var orgNames = new HashSet<string>(template.Organizations.Select(o => o.ShortName).Concat(draft.Organizations.Select(o => o.ShortName)).Concat(teamNames), StringComparer.OrdinalIgnoreCase);

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

            if (draft.Injects.Count > maxInjects)
            {
                warnings.Add($"Kept the first {maxInjects} of {draft.Injects.Count} injects.");
                draft.Injects = draft.Injects.Take(maxInjects).ToList();
            }

            var fields = DraftedFields(template).ToDictionary(f => f.Name, StringComparer.OrdinalIgnoreCase);
            var droppedColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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
            }
            draft.Injects = draft.Injects.OrderBy(i => i.Minutes).ToList();
            if (droppedColumns.Any())
                warnings.Add($"Dropped values for columns the template does not have: {string.Join(", ", droppedColumns)}.");

            var lastInject = draft.Injects.Any() ? draft.Injects.Max(i => i.Minutes) : 0;
            var lastMove = draft.Moves.Max(m => m.StartMinutes);
            if (draft.DurationMinutes <= Math.Max(lastInject, lastMove))
            {
                draft.DurationMinutes = Math.Max(lastInject, lastMove) + 30;
                warnings.Add($"Set the duration to {draft.DurationMinutes} minutes to cover the last inject.");
            }

            draft.Competencies = draft.Competencies
                .Where(c => !string.IsNullOrWhiteSpace(c))
                .Select(c => c.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static string CleanValue(DataFieldEntity field, string value, HashSet<string> teamNames, HashSet<string> orgNames, List<string> warnings)
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
                    return DateTime.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var dt)
                        ? dt.ToString("yyyy-MM-ddTHH:mm:ss.fffZ") : null;
                default:
                    return value;
            }
        }

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
