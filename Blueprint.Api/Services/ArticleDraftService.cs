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
using Blueprint.Api.Infrastructure.Exceptions;
using Blueprint.Api.Infrastructure.Extensions;
using Blueprint.Api.Infrastructure.Options;
using Blueprint.Api.ViewModels;
using Microsoft.Extensions.Logging;

namespace Blueprint.Api.Services
{
    public interface IArticleDraftService
    {
        /// <summary>Writes the body of one of a draft's Gallery articles as a document of its source type</summary>
        Task<ArticleDraftResult> DraftAsync(ArticleDraftRequest request, CancellationToken ct);
    }

    /// <summary>
    /// Writes a Gallery article's body so it reads like a real document of its source type: a news story with its
    /// outlet's masthead, a social post in its platform's frame, an email with its headers, an intelligence product
    /// under its handling banner. One article per call, so a whole MSEL's articles do not have to fit in one answer.
    /// </summary>
    public class ArticleDraftService : IArticleDraftService
    {
        private const string ToolName = "submit_article";

        public const string SystemPrompt =
@"You write one Gallery article for a Crucible cyber exercise: the document participants open on the Gallery wall. It must look like a
real document of its source type, made by the fictional source that sent it, not like a course slide or a plain block of text.
Keep the facts you are given: the headline, summary, source, sender, recipients and time. Expand the draft text into the full
document, consistent with the scenario and the organizations listed.

Every document starts with a full-width exercise banner: a cell with bgcolor #FDB516 holding EXERCISE · EXERCISE · EXERCISE in bold,
and under it the exercise name followed by · FICTIONAL SCENARIO in small text.

Lay the document out for its source type:
- News: the outlet's masthead (its name large in the outlet's colour, a section label at the right) and a coloured rule; the headline;
  a one-line standfirst in italics; the byline and dateline in small grey text; a lead map, chart or diagram with a caption when the
  story has one to show; body paragraphs; a pull
  quote in a shaded cell when one fits. 250 to 450 words.
- Social: the platform's frame: a header row with a round avatar showing the account's initials, the display name in bold, the
  @handle and the time in grey; the post text; an attached graphic only if the post needs one; a row of reply, repost and like counts.
  Invent the platform's name. Under 80 words, plus up to three short replies from other accounts if they add to the story.
- Email: a mail client's header block with From, To, Cc when needed, Date and Subject in label and value columns; an [EXTERNAL]
  warning line when the sender is outside the recipient's organization; the message; a signature block with the sender's logo.
- Phone: a call record: caller, number, called party, time and duration in a header block, then the transcript as alternating
  Caller: and Recipient: lines, ending with any callback request.
- Intel: an intelligence product (INTSUM, INTREP or SPOT REPORT): an EXERCISE USE ONLY handling banner at top and bottom, the product
  title, originator, date-time group and reference number, then headed sections: SUMMARY, DETAILS, ASSESSMENT, INDICATORS, ACTIONS
  REQUESTED, with confidence levels where an analyst would give them.
- Reporting: a bulletin, advisory or situation report: the issuing body's letterhead with its logo, a reference number and date, the
  title, a TLP marking, then headed sections: Summary, Affected Systems, Recommended Actions, Indicators (as a table), and Contact.
  A press release or official statement from the same kind of body has its letterhead and logo, FOR IMMEDIATE RELEASE, a dateline,
  the title, the statement, a quote from a named spokesperson, and a media contact block.
- Orders: a tasking order: the issuing headquarters' letterhead, ORDER or TASKING number, date-time group and references, then
  numbered paragraphs: Situation, Mission, Execution with numbered tasks and deadlines, Coordinating Instructions, Acknowledge.

HTML rules, because Gallery strips everything else:
- Answer with an HTML fragment only, no html, head or body tags.
- No style attributes, no style or script tags, no event handlers, no classes. Design for a white page 720 pixels wide.
- Lay out with tables: <table width=""720"" cellpadding=""12"" cellspacing=""0"" border=""0"">, nested tables for columns, banners and
  boxes. A 1px border is an outer table with bgcolor showing through cellspacing=""1"" around an inner cell with bgcolor #FFFFFF.
- Colour with bgcolor on table, tr and td, and text with <font color=""#333333"" face=""Arial, Helvetica, sans-serif"" size=""2"">.
  Use align on p, div and td, valign on td, and width and height on table, td and img.
- Allowed tags: div, p, h1 to h6, b, strong, i, em, u, br, hr, ul, ol, li, blockquote, table, tbody, tr, td, th, font, a, img, svg.
- Rounded corners, letter spacing, flexbox and CSS text transforms are not available; write upper case in the text itself.
- Gallery and Blueprint show the article in light and dark themes, so it is placed on white paper with dark text for you, as a real
  document would be. Give every coloured band, masthead and box its own bgcolor, and colour text only with dark colours on light
  cells or white on dark cells, never a colour that depends on the page behind it.
- Draw logos, mastheads, avatars, maps, charts and network diagrams as inline <svg> with width, height and viewBox, and an
  aria-label saying what it shows. Keep each one simple: shapes, paths and short text in Arial or Georgia. Use only &amp; &lt; &gt;
  &quot; and numeric entities inside SVG. No external images, links to fonts, or scripts.
- Draw only logos, mastheads, avatars, maps, charts and diagrams. Never draw an illustration of a scene, a person or an animal: an SVG
  drawing of one looks like clip art or a cartoon, not part of a real document. Leave a story without a picture rather than draw one.
- Graphics show maps, equipment layouts and data, never identifiable people, uniforms, national insignia, flags or real company logos.
  Logos are fictional marks: the organization's initials in a simple shape.

Content rules:
- Use fictional organizations, people, domains, phone numbers and IP addresses (RFC 5737 / RFC 1918 ranges, example.com style
  domains). Never use real personal data.
- Describe adversary activity as the source would see and report it. Never include working exploit code, malware, or real credentials.
- Put anything you had to assume in notes.
Answer only by calling the tool.";

        /// <summary>Added to the system prompt when the server has an image model</summary>
        public const string PhotoRules =
@"Photos: this server can make one realistic news photo for the article.
- For a News article, or a Social post that would carry a picture, describe one photo in photo.scene for the lead
  graphic, and put " + PhotoMarker + @" alone in the cell where it goes; the photo fills 720 pixels across.
- Describe a scene a news photographer would take: the place, equipment, weather and light, and what people are doing, seen from
  behind or at a distance. No text, signs, screens, logos, flags, insignia or identifiable people: the photo model cannot spell.
- photo.caption is the line under the photo, in the outlet's voice.
- Logos, mastheads, maps, charts and diagrams stay inline SVG; a photo cannot show them.";

        /// <summary>Where the article model puts its photo; replaced by the photo, or removed when there is none</summary>
        public const string PhotoMarker = "[[PHOTO]]";

        private readonly BlueprintContext _context;
        private readonly IAiModelClient _aiModelClient;
        private readonly IAiImageClient _aiImageClient;
        private readonly AiOptions _aiOptions;
        private readonly ClaimsPrincipal _user;
        private readonly ILogger<ArticleDraftService> _logger;

        public ArticleDraftService(BlueprintContext context, IAiModelClient aiModelClient, IAiImageClient aiImageClient, AiOptions aiOptions, IPrincipal user, ILogger<ArticleDraftService> logger)
        {
            _context = context;
            _aiModelClient = aiModelClient;
            _aiImageClient = aiImageClient;
            _aiOptions = aiOptions;
            _user = user as ClaimsPrincipal;
            _logger = logger;
        }

        public async Task<ArticleDraftResult> DraftAsync(ArticleDraftRequest request, CancellationToken ct)
        {
            if (!_aiOptions.Enabled)
                throw new InvalidOperationException("AI drafting is not enabled on this server.");
            var draft = request?.Draft ?? throw new BadRequestException("The draft is empty.");
            if (request.InjectIndex < 0 || request.InjectIndex >= (draft.Injects?.Count ?? 0))
                throw new BadRequestException("There is no such inject in the draft.");

            var template = await MselDraftService.LoadTemplateAsync(_context, _user.GetId(), request.TemplateMselId, ct);
            var body = ArticleFields.Body(template)
                ?? throw new BadRequestException("The template has no column for a Gallery article's body.");
            var inject = draft.Injects[request.InjectIndex];
            var values = inject.Values ?? new Dictionary<string, string>();

            var photos = _aiImageClient.IsConfigured;
            var input = await _aiModelClient.InvokeToolAsync(photos ? SystemPrompt + "\n\n" + PhotoRules : SystemPrompt,
                BuildPrompt(draft, template, values), ToolName, "Submit the article's HTML.", Schema(photos), ct);
            var result = new ArticleDraftResult { FieldName = body.Name };
            result.Html = GalleryArticleHtml.Prepare(GetString(input, "html"), result.Notes, GalleryArticleHtml.ArticleWidth);
            if (string.IsNullOrWhiteSpace(result.Html))
                throw new InvalidOperationException("The model wrote no article. Try again.");
            if (input.TryGetProperty("notes", out var notes) && notes.ValueKind == JsonValueKind.Array)
                result.Notes.AddRange(notes.EnumerateArray().Where(n => n.ValueKind == JsonValueKind.String).Select(n => n.GetString()));
            if (photos)
            {
                var photo = input.TryGetProperty("photo", out var p) && p.ValueKind == JsonValueKind.Object ? p : default;
                result.Html = await PlacePhotoAsync(result.Html,
                    photo.ValueKind == JsonValueKind.Object ? GetString(photo, "scene") : null,
                    photo.ValueKind == JsonValueKind.Object ? GetString(photo, "caption") : null, result.Notes, ct);
            }
            return result;
        }

        // the photo goes where the model marked its place; an article without a place for it gets none, so none is paid for
        private async Task<string> PlacePhotoAsync(string html, string scene, string caption, List<string> notes, CancellationToken ct)
        {
            if (!html.Contains(PhotoMarker))
            {
                if (!string.IsNullOrWhiteSpace(scene))
                    notes.Add("The article had no place for its photo, so none was made.");
                return html;
            }
            string photo = null;
            if (!string.IsNullOrWhiteSpace(scene))
            {
                try
                {
                    photo = await _aiImageClient.PhotoAsync(scene, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "No photo was made for a drafted article");
                    notes.Add($"No photo was made: {ex.Message}");
                }
            }
            var alt = WebUtility.HtmlEncode(string.IsNullOrWhiteSpace(caption) ? scene ?? "" : caption.Trim());
            var figure = photo == null ? ""
                : $"<img src=\"{photo}\" width=\"{GalleryArticleHtml.ArticleWidth}\" height=\"{GalleryArticleHtml.ArticleWidth * 9 / 16}\" alt=\"{alt}\">" +
                  (string.IsNullOrWhiteSpace(caption) ? "" : $"<br><font color=\"#666666\" size=\"1\">{WebUtility.HtmlEncode(caption.Trim())}</font>");
            var first = html.IndexOf(PhotoMarker, StringComparison.Ordinal);
            return html[..first] + figure + html[(first + PhotoMarker.Length)..].Replace(PhotoMarker, "");
        }

        public static string BuildPrompt(MselDraft draft, MselEntity template, IReadOnlyDictionary<string, string> values)
        {
            string Value(GalleryArticleParameter parameter)
            {
                var field = template.DataFields.FirstOrDefault(f => f.GalleryArticleParameter == parameter.ToString());
                return field != null && values.TryGetValue(field.Name, out var value) ? value : null;
            }

            var sb = new StringBuilder();
            sb.AppendLine($"Exercise: {draft.Name}");
            if (!string.IsNullOrWhiteSpace(draft.Description))
                sb.AppendLine($"Scenario: {draft.Description}");
            sb.AppendLine();
            sb.AppendLine("Organizations and teams (short name: name, who they are):");
            foreach (var organization in draft.Organizations ?? new List<MselDraftOrganization>())
                sb.AppendLine($"- {organization.ShortName}: {organization.Name}, {organization.Description}");
            foreach (var team in draft.Teams ?? new List<MselDraftTeam>())
                sb.AppendLine($"- {team.ShortName}: {team.Name}, participant team. {team.Description}");
            sb.AppendLine();
            sb.AppendLine("The article:");
            sb.AppendLine($"Source type: {Value(GalleryArticleParameter.SourceType) ?? "News"}");
            sb.AppendLine($"Source name: {Value(GalleryArticleParameter.SourceName)}");
            sb.AppendLine($"Sent by: {Value(GalleryArticleParameter.FromOrg)}");
            sb.AppendLine($"Sent to: {Value(GalleryArticleParameter.ToOrg)}");
            sb.AppendLine($"Date and time: {Value(GalleryArticleParameter.DatePosted)}");
            sb.AppendLine($"Headline or subject: {Value(GalleryArticleParameter.Name)}");
            sb.AppendLine($"Summary: {Value(GalleryArticleParameter.Summary)}");
            sb.AppendLine("Draft text:");
            sb.AppendLine(DataValueDraftService.StripHtml(Value(GalleryArticleParameter.Description)) ?? "");
            return sb.ToString();
        }

        private static JsonObject Schema(bool photos)
        {
            var properties = new JsonObject
            {
                ["html"] = new JsonObject { ["type"] = "string", ["description"] = "The article as an HTML fragment that follows the HTML rules." },
                ["notes"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" }, ["description"] = "Assumptions the planner should check." }
            };
            if (photos)
                properties["photo"] = new JsonObject
                {
                    ["type"] = "object",
                    ["description"] = "The article's one news photo, when it should have one; its place is marked " + PhotoMarker + " in the HTML.",
                    ["properties"] = new JsonObject
                    {
                        ["scene"] = new JsonObject { ["type"] = "string", ["description"] = "What the photo shows, for the photo model; no text, signs, logos or identifiable people." },
                        ["caption"] = new JsonObject { ["type"] = "string", ["description"] = "The caption under the photo." }
                    },
                    ["required"] = new JsonArray("scene", "caption")
                };
            return new JsonObject { ["type"] = "object", ["properties"] = properties, ["required"] = new JsonArray("html") };
        }

        private static string GetString(JsonElement element, string property) =>
            element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    /// <summary>The columns of a MSEL's Gallery articles</summary>
    public static class ArticleFields
    {
        /// <summary>The column holding an article's body</summary>
        public static DataFieldEntity Body(MselEntity msel) =>
            msel.UseGallery ? msel.DataFields.FirstOrDefault(f => f.GalleryArticleParameter == nameof(GalleryArticleParameter.Description)) : null;

        /// <summary>Whether a drafted inject is a Gallery article: it has a source type</summary>
        public static bool IsArticle(MselEntity msel, IReadOnlyDictionary<string, string> values)
        {
            var sourceType = msel.DataFields.FirstOrDefault(f => f.GalleryArticleParameter == nameof(GalleryArticleParameter.SourceType));
            return Body(msel) != null && sourceType != null && values != null && values.TryGetValue(sourceType.Name, out var value) && !string.IsNullOrWhiteSpace(value);
        }
    }
}
