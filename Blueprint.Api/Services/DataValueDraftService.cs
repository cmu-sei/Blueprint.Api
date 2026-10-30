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
using System.Text.RegularExpressions;
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
using Microsoft.Extensions.Logging;

namespace Blueprint.Api.Services
{
    public interface IDataValueDraftService
    {
        /// <summary>Asks the model for one field of an inject, written from the rest of the MSEL. Nothing is saved.</summary>
        Task<DataValueDraftResult> DraftAsync(Guid mselId, DataValueDraftRequest request, bool hasSystemPermission, CancellationToken ct);
    }

    public class DataValueDraftService : IDataValueDraftService
    {
        private const string ToolName = "submit_field_value";
        // how much of the MSEL goes into the prompt
        private const int EarlierInjects = 25;
        private const int LaterInjects = 10;
        private const int ValueLength = 2000;
        private const int ExcerptLength = 400;
        private readonly BlueprintContext _context;
        private readonly IAiModelClient _aiModelClient;
        private readonly IAiImageClient _aiImageClient;
        private readonly AiOptions _aiOptions;
        private readonly ClaimsPrincipal _user;
        private readonly ILogger<DataValueDraftService> _logger;

        private const string SystemPrompt =
@"You write one field of one inject in a Master Scenario Event List (MSEL) for a Crucible cyber exercise in Blueprint.
A MSEL is organized as Move > Group > Inject. Many injects are pushed to Gallery as articles that participants read: news stories, social posts, emails, phone calls, intel and situation reports, orders.
Write for an exercise planner who reviews the text before anything reaches participants.
Rules:
- Stay consistent with the scenario, the moves and the injects before this one: the same organizations, people, places, times and numbers. Do not reveal anything participants should only learn from a later inject.
- Write in the voice of the inject's source (a reporter, an official, an attacker's ransom note, a help desk ticket) and address it to its recipients.
- Use fictional organizations, people, domains and IP addresses (RFC 5737 / RFC 1918 ranges, example.com style domains). Never use real personal data.
- Describe adversary activity at the level a defender would observe it. Never include working exploit code, malware, or real credentials.
- Put any assumption you had to make, or conflict you found, in notes, not in the value.
Answer only by calling the tool.";

        private readonly IAiUsageContext _usage;

        public DataValueDraftService(BlueprintContext context, IAiModelClient aiModelClient, IAiImageClient aiImageClient, AiOptions aiOptions, IPrincipal user, ILogger<DataValueDraftService> logger, IAiUsageContext usage)
        {
            _usage = usage;
            _context = context;
            _aiModelClient = aiModelClient;
            _aiImageClient = aiImageClient;
            _aiOptions = aiOptions;
            _user = user as ClaimsPrincipal;
            _logger = logger;
        }

        public async Task<DataValueDraftResult> DraftAsync(Guid mselId, DataValueDraftRequest request, bool hasSystemPermission, CancellationToken ct)
        {
            if (!_aiOptions.Enabled)
                throw new InvalidOperationException("AI drafting is not enabled on this server.");

            // the same roles that may save the inject may have it written
            var userId = _user.GetId();
            if (!hasSystemPermission &&
                !await MselOwnerRequirement.IsMet(userId, mselId, _context) &&
                !await MselApproverRequirement.IsMet(userId, mselId, _context) &&
                !await MselEditorRequirement.IsMet(userId, mselId, _context))
                throw new ForbiddenException();

            var msel = await _context.Msels
                .AsNoTracking()
                .Include(m => m.DataFields)
                .Include(m => m.Moves)
                .Include(m => m.Teams)
                .Include(m => m.Organizations)
                .AsSplitQuery()
                .SingleOrDefaultAsync(m => m.Id == mselId, ct);
            if (msel == null)
                throw new EntityNotFoundException<MselEntity>("MSEL not found with ID=" + mselId);
            var field = msel.DataFields.SingleOrDefault(f => f.Id == request.DataFieldId);
            if (field == null)
                throw new EntityNotFoundException<DataFieldEntity>("The field is not one of this MSEL's.");
            if (!CanDraft(field))
                throw new ArgumentException($"{field.Name} is not a text field.");
            var current = request.Values?.GetValueOrDefault(field.Id);
            if (request.Action != DataValueDraftAction.Draft && string.IsNullOrWhiteSpace(StripHtml(current)))
                throw new ArgumentException($"{field.Name} is empty, so there is nothing to {request.Action.ToString().ToLower()}. Draft it instead.");

            var others = await _context.ScenarioEvents
                .AsNoTracking()
                .Where(e => e.MselId == mselId && e.Id != request.ScenarioEventId)
                .Include(e => e.DataValues)
                .OrderBy(e => e.DeltaSeconds).ThenBy(e => e.GroupOrder)
                .ToListAsync(ct);

            var prompt = BuildPrompt(msel, field, request, others);
            // a Gallery article's body is the whole document, laid out for its source type
            var isArticle = ArticleFields.Body(msel)?.Id == field.Id;
            _usage.Use("value", mselId);
            // an article gets a news photo where it calls for one, as a drafted article does
            var photos = isArticle && _aiImageClient.IsConfigured;
            var system = isArticle
                ? SystemPrompt + "\n\nThis field is the body of a Gallery article. Write it as the full document:\n\n" + ArticleDraftService.SystemPrompt
                : SystemPrompt;
            if (photos)
                system += "\n\n" + ArticleDraftService.PhotoRules;
            var schema = BuildSchema(field);
            if (photos)
                ((JsonObject)schema["properties"])["photo"] = ArticleDraftService.PhotoProperty();
            var input = await _aiModelClient.InvokeToolAsync(system, prompt, ToolName,
                $"Submit the new value of {field.Name} for the planner to review.", schema, ct);
            var result = input.Deserialize<DataValueDraftResult>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? new DataValueDraftResult();
            result.Notes = (result.Notes ?? new List<string>()).Where(n => !string.IsNullOrWhiteSpace(n)).ToList();
            result.Value = isArticle ? GalleryArticleHtml.Prepare(result.Value, result.Notes, GalleryArticleHtml.ArticleWidth)
                : field.DataType == DataFieldType.Html ? CleanHtml(result.Value) : result.Value?.Trim();
            if (string.IsNullOrWhiteSpace(StripHtml(result.Value)))
                throw new InvalidOperationException("The model returned an empty value. Try again or add an instruction.");
            if (photos)
                result.Value = await ArticleDraftService.PlacePhotoAsync(_aiImageClient, _logger, result.Value, input, result.Notes, ct);
            if (isArticle)
                result.Value = ArticleMarkings.Apply(result.Value, msel.ArticleMarking, msel.ArticleMarkingText, msel.Name);
            return result;
        }

        public static bool CanDraft(DataFieldEntity field) =>
            !field.IsChosenFromList && (field.DataType == DataFieldType.Html || field.DataType == DataFieldType.String);

        private static JsonObject BuildSchema(DataFieldEntity field) => new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["value"] = new JsonObject
                {
                    ["type"] = "string",
                    ["description"] = MselDraftRules.Describe(field)
                },
                ["notes"] = new JsonObject
                {
                    ["type"] = "array",
                    ["description"] = "Assumptions the planner should check, and conflicts with the scenario or other injects",
                    ["items"] = new JsonObject { ["type"] = "string" }
                }
            },
            ["required"] = new JsonArray("value")
        };

        public static string BuildPrompt(MselEntity msel, DataFieldEntity field, DataValueDraftRequest request, List<ScenarioEventEntity> others)
        {
            var fields = msel.DataFields.ToDictionary(f => f.Id);
            var sb = new StringBuilder();
            sb.AppendLine($"Exercise: {msel.Name}");
            if (!string.IsNullOrWhiteSpace(msel.Description))
                sb.AppendLine($"Scenario: {Excerpt(msel.Description, ValueLength)}");
            if (msel.Teams.Any())
                sb.AppendLine($"Participant teams: {string.Join("; ", msel.Teams.OrderBy(t => t.ShortName).Select(t => $"{t.ShortName} ({t.Name})"))}");
            if (msel.Organizations.Any())
                sb.AppendLine($"Organizations: {string.Join("; ", msel.Organizations.OrderBy(o => o.ShortName).Select(o => $"{o.ShortName} ({o.Name})"))}");

            var moves = msel.Moves.OrderBy(m => m.DeltaSeconds).ThenBy(m => m.MoveNumber).ToList();
            if (moves.Any())
            {
                sb.AppendLine();
                sb.AppendLine("Moves:");
                foreach (var move in moves)
                {
                    sb.Append($"- Move {move.MoveNumber} at {Clock(move.DeltaSeconds)}: {move.Description}");
                    if (!string.IsNullOrWhiteSpace(move.SituationDescription))
                        sb.Append($". Situation: {Excerpt(move.SituationDescription, ExcerptLength)}");
                    sb.AppendLine();
                }
            }

            var earlier = others.Where(e => e.DeltaSeconds <= request.DeltaSeconds).TakeLast(EarlierInjects).ToList();
            var later = others.Where(e => e.DeltaSeconds > request.DeltaSeconds).Take(LaterInjects).ToList();
            if (earlier.Any())
            {
                sb.AppendLine();
                sb.AppendLine("Injects before this one, oldest first:");
                foreach (var e in earlier)
                    sb.AppendLine($"- {Clock(e.DeltaSeconds)} {Summarize(e.DataValues.ToDictionary(v => v.DataFieldId, v => v.Value), fields, field.Id, true)}");
            }
            if (later.Any())
            {
                sb.AppendLine();
                sb.AppendLine("Injects after this one (do not give them away):");
                foreach (var e in later)
                    sb.AppendLine($"- {Clock(e.DeltaSeconds)} {Summarize(e.DataValues.ToDictionary(v => v.DataFieldId, v => v.Value), fields, field.Id, false)}");
            }

            sb.AppendLine();
            sb.AppendLine($"This inject, delivered at {Clock(request.DeltaSeconds)}:");
            foreach (var other in msel.DataFields.OrderBy(f => f.DisplayOrder))
            {
                var value = StripHtml(request.Values?.GetValueOrDefault(other.Id));
                if (other.Id == field.Id || string.IsNullOrWhiteSpace(value))
                    continue;
                sb.AppendLine($"- {Label(other)}: {Excerpt(value, ValueLength)}");
            }

            sb.AppendLine();
            sb.AppendLine($"Field to write: {Label(field)}");
            var current = request.Values?.GetValueOrDefault(field.Id);
            if (!string.IsNullOrWhiteSpace(StripHtml(current)))
            {
                sb.AppendLine("Its current value:");
                sb.AppendLine(Excerpt(current, ValueLength * 2));
            }
            sb.AppendLine(request.Action switch
            {
                DataValueDraftAction.Rewrite => string.IsNullOrWhiteSpace(request.Style)
                    ? "Rewrite it, keeping its facts."
                    : $"Rewrite it as {StyleName(request.Style)}, keeping its facts.",
                DataValueDraftAction.Consistent => "Keep it as written, except fix anything that conflicts with the scenario, the moves or the earlier injects. List each fix in notes.",
                DataValueDraftAction.Shorten => "Make it about half as long, keeping what participants need to act on.",
                DataValueDraftAction.Expand => "Add realistic detail participants can act on, without changing its facts.",
                _ => "Write it from the inject's other values and the scenario."
            });
            if (!string.IsNullOrWhiteSpace(request.Instruction))
            {
                sb.AppendLine("The planner adds:");
                sb.AppendLine(request.Instruction.Trim());
            }
            return sb.ToString();
        }

        // a Gallery source type, as the kind of text it is
        private static string StyleName(string style) =>
            Enum.TryParse<GallerySourceType>(style, true, out var sourceType) ? sourceType switch
            {
                GallerySourceType.News => "a news article",
                GallerySourceType.Social => "a social media post",
                GallerySourceType.Email => "an email",
                GallerySourceType.Phone => "a phone call transcript",
                GallerySourceType.Intel => "an intelligence report",
                GallerySourceType.Reporting => "a situation report",
                GallerySourceType.Orders => "orders",
                _ => style
            } : style.Trim();

        // one line per inject: the fields that name it, and an excerpt of the field being written
        private static string Summarize(Dictionary<Guid, string> values, Dictionary<Guid, DataFieldEntity> fields, Guid fieldId, bool withExcerpt)
        {
            var parts = new List<string>();
            foreach (var (id, raw) in values)
            {
                if (!fields.TryGetValue(id, out var f))
                    continue;
                var value = StripHtml(raw);
                if (string.IsNullOrWhiteSpace(value))
                    continue;
                if (id == fieldId)
                {
                    if (withExcerpt)
                        parts.Add($"{f.Name}: {Excerpt(value, ExcerptLength)}");
                }
                else if (IsIdentifying(f))
                    parts.Add($"{f.Name}: {Excerpt(value, 160)}");
            }
            return string.Join(" | ", parts);
        }

        private static bool IsIdentifying(DataFieldEntity f) =>
            f.GalleryArticleParameter is "Name" or "Summary" or "SourceType" or "SourceName" or "FromOrg" or "ToOrg" ||
            f.DataType is DataFieldType.TeamsMultiple or DataFieldType.Team or DataFieldType.Organization or DataFieldType.SourceType ||
            f.Name.Trim().ToLower() is "title" or "name" or "subject" or "summary" or "from" or "to";

        private static string Label(DataFieldEntity f) =>
            string.IsNullOrWhiteSpace(f.GalleryArticleParameter) ? f.Name : $"{f.Name} (Gallery article {f.GalleryArticleParameter})";

        private static string Clock(int seconds) =>
            $"{seconds / 3600}:{seconds / 60 % 60:00}";

        private static string Excerpt(string value, int length)
        {
            value = value.Trim();
            return value.Length <= length ? value : value[..length] + "...";
        }

        public static string StripHtml(string html)
        {
            if (string.IsNullOrEmpty(html))
                return html;
            var text = Regex.Replace(html, @"<(br|/p|/li|/h\d|/div)\b[^>]*>", " ", RegexOptions.IgnoreCase);
            text = Regex.Replace(text, "<[^>]*>", "");
            return Regex.Replace(WebUtility.HtmlDecode(text), @"\s+", " ").Trim();
        }

        private static readonly HashSet<string> _allowedTags = new(StringComparer.OrdinalIgnoreCase)
        {
            "p", "br", "ul", "ol", "li", "strong", "b", "em", "i", "u", "h3", "h4", "blockquote"
        };

        /// <summary>
        /// Keeps the formatting the editor offers and nothing else: unknown tags go, and every attribute
        /// goes, so the model cannot add scripts, styles or links.
        /// </summary>
        public static string CleanHtml(string html)
        {
            if (string.IsNullOrWhiteSpace(html))
                return html;
            html = Regex.Replace(html, @"<(script|style)\b[^>]*>.*?</\1\s*>", "", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            html = Regex.Replace(html, @"<(/?)([a-zA-Z][a-zA-Z0-9]*)\b[^>]*?(/?)>", m =>
                _allowedTags.Contains(m.Groups[2].Value)
                    ? $"<{m.Groups[1].Value}{m.Groups[2].Value.ToLower()}>"
                    : "");
            html = html.Trim();
            // plain text from the model still renders as a paragraph
            return html.StartsWith('<') ? html : $"<p>{html}</p>";
        }
    }
}
