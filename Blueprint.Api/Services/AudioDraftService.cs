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
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
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
    public interface IAudioDraftService
    {
        /// <summary>Which audio this server can make for the MSEL's injects</summary>
        Task<AudioDraftAvailability> GetAvailabilityAsync(Guid mselId, bool hasSystemPermission, CancellationToken ct);

        /// <summary>Writes, where the kind needs one, and voices a clip for one inject. Nothing is saved.</summary>
        Task<AudioDraftResult> DraftAsync(Guid mselId, AudioDraftRequest request, bool hasSystemPermission, CancellationToken ct);
    }

    /// <summary>
    /// Audio and video for an inject: its article read aloud, or a voicemail, an intercepted call, a broadcast segment or a social
    /// news reel the model writes from it. The lines come back voiced, one MP3 each with its sentence and word timings, and the
    /// planner's browser joins them into the audio or video that goes into the article.
    /// </summary>
    public class AudioDraftService : IAudioDraftService
    {
        public const string ToolName = "submit_script";
        // how many lines are voiced at once
        private const int ParallelLines = 4;
        private const int ValueLength = 2000;
        private const int ArticleLength = 6000;
        private static readonly HtmlParser Parser = new();
        private readonly BlueprintContext _context;
        private readonly IAiModelClient _aiModelClient;
        private readonly IAiSpeechClient _speech;
        private readonly IAiImageClient _images;
        private readonly AiOptions _aiOptions;
        private readonly ClaimsPrincipal _user;
        private readonly IAiUsageContext _usage;
        private readonly ILogger<AudioDraftService> _logger;

        public const string SystemPrompt =
@"You write the script of a short audio clip for a Crucible cyber exercise in Blueprint: something participants hear on the Gallery
wall beside the inject's article. Each line is read aloud by a text-to-speech voice, so write for the ear:
- Write only the words spoken. No stage directions, sound effects, speaker names, brackets or markup inside a line.
- Short sentences in plain spoken language. Write numbers, times, sums and abbreviations as a speaker would say them
  (""two thirty in the afternoon"", ""four million dollars"", ""C-I-S-A"" only if it is spelled out when spoken).
- Stay consistent with the inject and the scenario: the same organizations, people, places, times and numbers. Do not reveal
  anything participants should only learn from a later inject.
- Use fictional organizations, people, phone numbers (555-01xx) and domains. Never use real personal data.
- Describe adversary activity as people on a phone or in the news would. Never include working exploit code or real credentials.
- Give each speaker one voice from the list, matching their gender, and keep it for all their lines. Different speakers get
  different voices.
- Put any assumption you had to make, or conflict you found, in notes, not in the script.
Answer only by calling the tool.";

        public AudioDraftService(BlueprintContext context, IAiModelClient aiModelClient, IAiSpeechClient speech, IAiImageClient images,
            AiOptions aiOptions, IPrincipal user, IAiUsageContext usage, ILogger<AudioDraftService> logger)
        {
            _context = context;
            _aiModelClient = aiModelClient;
            _speech = speech;
            _images = images;
            _aiOptions = aiOptions;
            _user = user as ClaimsPrincipal;
            _usage = usage;
            _logger = logger;
        }

        public async Task<AudioDraftAvailability> GetAvailabilityAsync(Guid mselId, bool hasSystemPermission, CancellationToken ct)
        {
            await AuthorizeAsync(mselId, hasSystemPermission, ct);
            if (!await _context.Msels.AnyAsync(m => m.Id == mselId, ct))
                throw new EntityNotFoundException<MselEntity>("MSEL not found with ID=" + mselId);
            var available = _speech.IsConfigured && _speech.Voices.Count > 0;
            return new AudioDraftAvailability
            {
                Available = available,
                Scripts = available && _aiOptions.Enabled,
                Photos = available && _images.IsConfigured
            };
        }

        public async Task<AudioDraftResult> DraftAsync(Guid mselId, AudioDraftRequest request, bool hasSystemPermission, CancellationToken ct)
        {
            await AuthorizeAsync(mselId, hasSystemPermission, ct);
            var msel = await _context.Msels
                .AsNoTracking()
                .Include(m => m.DataFields)
                .Include(m => m.Organizations)
                .AsSplitQuery()
                .SingleOrDefaultAsync(m => m.Id == mselId, ct)
                ?? throw new EntityNotFoundException<MselEntity>("MSEL not found with ID=" + mselId);
            var field = msel.DataFields.SingleOrDefault(f => f.Id == request.DataFieldId)
                ?? throw new EntityNotFoundException<DataFieldEntity>("The field is not one of this MSEL's.");
            if (!_speech.IsConfigured || _speech.Voices.Count == 0)
                throw new BadRequestException("No speech provider is configured on this server. Set Ai:Speech on the Blueprint API to make audio.");
            if (field.DataType != DataFieldType.Html || field.IsChosenFromList)
                throw new BadRequestException($"{field.Name} is not an HTML field, so it cannot hold audio or video.");
            var scripted = request.Kind != AudioDraftKind.Listen;
            if (scripted && !_aiOptions.Enabled)
                throw new BadRequestException("AI drafting is not enabled on this server, so it cannot write a script. Listen to this article still works.");

            var inject = Inject.From(msel, request.Values, field);
            var voices = _speech.Voices;
            var outletVoice = PickVoice(voices, inject.SourceName ?? msel.Name);
            var result = new AudioDraftResult
            {
                Kind = request.Kind,
                Phone = request.Kind is AudioDraftKind.Call or AudioDraftKind.Voicemail,
                Headline = inject.Headline,
                Marking = Marking(msel)
            };

            List<ScriptLine> lines;
            string scene = null;
            if (!scripted)
            {
                var text = ReadAloud(inject.Headline, inject.Article);
                if (string.IsNullOrWhiteSpace(text))
                    throw new BadRequestException($"{field.Name} has no article text to read aloud.");
                lines = [new ScriptLine(inject.SourceName ?? "Reader", outletVoice.Name, text)];
                _usage.Use(AiUsageRecorder.Audio, mselId);
            }
            else
            {
                _usage.Use("script", mselId);
                var input = await _aiModelClient.InvokeToolAsync(SystemPrompt + "\n\n" + KindRules(request.Kind), BuildPrompt(msel, field, request, inject, voices, outletVoice),
                    ToolName, "Submit the clip's script for the planner to review.", Schema(request.Kind, voices), ct);
                (lines, scene) = ReadScript(input, request.Kind, voices, outletVoice, inject, result);
                if (lines.Count == 0)
                    throw new InvalidOperationException("The model returned an empty script. Try again or add an instruction.");
            }
            var characters = lines.Sum(l => l.Text.Length);
            var max = _aiOptions.Speech?.MaxCharacters > 0 ? _aiOptions.Speech.MaxCharacters : 6000;
            if (characters > max)
                throw new BadRequestException($"That is {characters:N0} characters to read aloud, and this server voices at most {max:N0} a clip (Ai:Speech:MaxCharacters).");

            result.Lines = await VoiceAsync(lines, request.Kind, voices, ct);
            result.Seconds = (int)Math.Ceiling(result.Lines.Sum(l => l.DurationMs) / 1000.0);
            result.Title = request.Kind switch
            {
                AudioDraftKind.Voicemail => "Voicemail",
                AudioDraftKind.Call => "Intercept",
                AudioDraftKind.Broadcast or AudioDraftKind.Reel => result.Outlet ?? inject.SourceName ?? "News",
                _ => null
            };
            if (scripted)
                result.Transcript = Transcript(result.Lines);
            if (request.Kind == AudioDraftKind.Reel)
                await PictureAsync(result, inject, scene, ct);
            return result;
        }

        private async Task AuthorizeAsync(Guid mselId, bool hasSystemPermission, CancellationToken ct)
        {
            // the same roles that may save the inject may have it voiced
            var userId = _user.GetId();
            if (!hasSystemPermission &&
                !await MselOwnerRequirement.IsMet(userId, mselId, _context) &&
                !await MselApproverRequirement.IsMet(userId, mselId, _context) &&
                !await MselEditorRequirement.IsMet(userId, mselId, _context))
                throw new ForbiddenException();
        }

        /// <summary>A line of the script, before it is voiced</summary>
        public record ScriptLine(string Speaker, string Voice, string Text);

        /// <summary>What the clip is made from: the inject's headline, source and article</summary>
        public record Inject(string Headline, string SourceName, string SourceType, string Article, string Photo, Dictionary<Guid, string> Values)
        {
            public static Inject From(MselEntity msel, Dictionary<Guid, string> values, DataFieldEntity field)
            {
                values ??= new Dictionary<Guid, string>();
                string Value(Func<DataFieldEntity, bool> which)
                {
                    var f = msel.DataFields.Where(which).OrderBy(x => x.DisplayOrder).FirstOrDefault();
                    var value = f == null ? null : DataValueDraftService.StripHtml(values.GetValueOrDefault(f.Id));
                    return string.IsNullOrWhiteSpace(value) ? null : value;
                }
                var headline = Value(f => f.GalleryArticleParameter == nameof(GalleryArticleParameter.Name))
                    ?? Value(f => f.Name.Trim().ToLowerInvariant() is "title" or "name" or "subject");
                var article = values.GetValueOrDefault(field.Id);
                return new Inject(headline,
                    Value(f => f.GalleryArticleParameter == nameof(GalleryArticleParameter.SourceName)),
                    Value(f => f.GalleryArticleParameter == nameof(GalleryArticleParameter.SourceType) || f.DataType == DataFieldType.SourceType),
                    article,
                    ArticlePhoto(values.Values),
                    values);
            }
        }

        /// <summary>
        /// The voice an outlet or sender always gets: the same name picks the same voice from the same list, so a newspaper's
        /// articles are all read by one voice.
        /// </summary>
        public static AiVoice PickVoice(IReadOnlyList<AiVoice> voices, string name)
        {
            // FNV-1a, which unlike string.GetHashCode is the same in every process
            uint hash = 2166136261;
            foreach (var c in (name ?? "").Trim().ToLowerInvariant())
                hash = (hash ^ c) * 16777619;
            return voices[(int)(hash % (uint)voices.Count)];
        }

        /// <summary>
        /// An article's words as a reader would read them: the headline, then its headings, paragraphs, list items and quotes. The
        /// marking, captions in small print, transcripts and media are skipped. An article laid out without paragraphs is read whole.
        /// </summary>
        public static string ReadAloud(string headline, string html)
        {
            var body = Parser.ParseDocument($"<body>{html ?? ""}</body>").Body;
            foreach (var skipped in body.QuerySelectorAll("table[title=Marking], details, audio, video, script, style, figcaption, font[size='1']").ToList())
                skipped.Remove();
            const string blocks = "p, li, blockquote, h1, h2, h3, h4, h5, h6";
            var parts = body.QuerySelectorAll(blocks)
                .Where(e => !e.QuerySelectorAll(blocks).Any())
                .Select(e => Normalize(e.TextContent))
                .Where(t => t.Length > 0)
                .ToList();
            var whole = Normalize(BlockText(body));
            // a layout of table cells and fonts has its words outside paragraphs
            if (parts.Sum(p => p.Length) < Math.Min(200, whole.Length * 0.4))
                parts = [whole];
            var h = Normalize(headline);
            if (h.Length > 0)
            {
                parts.RemoveAll(p => string.Equals(p.TrimEnd('.', '!', '?'), h.TrimEnd('.', '!', '?'), StringComparison.OrdinalIgnoreCase));
                parts.Insert(0, h);
            }
            return string.Join(" ", parts.Where(p => p.Length > 0).Select(Sentence));
        }

        // a block that ends without a full stop gets one, so the voice pauses before the next
        private static string Sentence(string text) => Regex.IsMatch(text, @"[.!?:;][""'”’)]*$") ? text : text + ".";

        private static string Normalize(string text) => Regex.Replace(WebUtility.HtmlDecode(text ?? ""), @"\s+", " ").Trim();

        // the text of the elements with a space between blocks, so cell text does not run together
        private static string BlockText(IElement element)
        {
            var sb = new StringBuilder();
            void Walk(INode node)
            {
                foreach (var child in node.ChildNodes)
                {
                    if (child is IText text)
                        sb.Append(text.Data);
                    else if (child is IElement e)
                    {
                        var block = e.LocalName is "p" or "div" or "td" or "th" or "tr" or "li" or "br" or "table" or "h1" or "h2" or "h3" or "h4" or "h5" or "h6" or "blockquote";
                        if (block)
                            sb.Append(' ');
                        Walk(e);
                        if (block)
                            sb.Append(' ');
                    }
                }
            }
            Walk(element);
            return sb.ToString();
        }

        /// <summary>The largest JPEG, PNG or WebP photo in the inject's values, for a reel's picture; graphics drawn as SVG are not photos</summary>
        public static string ArticlePhoto(IEnumerable<string> values) =>
            values.Where(v => !string.IsNullOrEmpty(v))
                .SelectMany(v => Regex.Matches(v, @"<img\b[^>]*\bsrc=""(data:image/(?:jpeg|png|webp);base64,[A-Za-z0-9+/=]+)""", RegexOptions.IgnoreCase).Select(m => m.Groups[1].Value))
                .OrderByDescending(src => src.Length)
                .FirstOrDefault();

        /// <summary>
        /// A reel's picture: the article's own photo; or, where an image model is configured, a photo of the scene the script
        /// describes; or none, for a card in the outlet's colours.
        /// </summary>
        private async Task PictureAsync(AudioDraftResult result, Inject inject, string scene, CancellationToken ct)
        {
            if (inject.Photo != null)
            {
                result.Photo = inject.Photo;
                result.PhotoSource = "article";
                return;
            }
            if (_images.IsConfigured && !string.IsNullOrWhiteSpace(scene))
            {
                try
                {
                    result.Photo = await _images.PhotoAsync(scene, ct);
                    result.PhotoSource = "generated";
                    return;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "No photo was made for a reel");
                    result.Notes.Add("No photo could be made, so the reel is on a plain card: " + ex.Message);
                }
            }
            result.PhotoSource = "none";
        }

        public static string KindRules(AudioDraftKind kind) => kind switch
        {
            AudioDraftKind.Voicemail =>
@"This clip is a voicemail the inject's sender leaves for its recipient: one speaker, 20 to 45 seconds (60 to 120 words). They
say who they are and where they are calling from, give the message as a person would on the phone, with its urgency, and end
with what they want done and a callback number.",
            AudioDraftKind.Call =>
@"This clip is a recorded or intercepted phone call between two or more speakers, 30 to 60 seconds in all (80 to 160 words), in
6 to 14 short lines that go back and forth. Speakers talk as people do on a phone: guarded when they would be, with the
interruptions and short answers of a real call. Name each speaker as an intercept transcript would: their name or role when
the inject gives it, otherwise CALLER 1, CALLER 2.",
            AudioDraftKind.Broadcast =>
@"This clip is the outlet's broadcast news segment on the story: 30 to 60 seconds in all (80 to 160 words). The anchor opens and
closes it; one or two short quotes from a named official, expert or witness may come between, in their own voice. It is the
spoken version of the story, not the article read out: the lead first, then what it means for listeners.",
            AudioDraftKind.Reel =>
@"This clip is a vertical social media news reel the outlet posts: one anchor voice reading 15 to 30 seconds (40 to 80 words)
over a photo, with the words shown as captions. Open with the hook in one short sentence, give the two or three facts that
matter, and close with what happens next. Write the post's text too: one or two sentences and up to three hashtags. Name the
outlet as viewers know it, its handle, and describe the scene a news photo of the story would show, with no text, signs or
recognizable faces in it.",
            _ => ""
        };

        private static JsonObject Schema(AudioDraftKind kind, IReadOnlyList<AiVoice> voices)
        {
            var notes = new JsonObject
            {
                ["type"] = "array",
                ["description"] = "Assumptions the planner should check, and conflicts with the scenario or the inject",
                ["items"] = new JsonObject { ["type"] = "string" }
            };
            if (kind == AudioDraftKind.Reel)
                return new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["script"] = new JsonObject { ["type"] = "string", ["description"] = "What the anchor says, 40 to 80 words" },
                        ["post"] = new JsonObject { ["type"] = "string", ["description"] = "The post's text, with up to three hashtags" },
                        ["outlet"] = new JsonObject { ["type"] = "string", ["description"] = "The outlet's name as viewers know it" },
                        ["handle"] = new JsonObject { ["type"] = "string", ["description"] = "The outlet's handle, starting with @" },
                        ["scene"] = new JsonObject { ["type"] = "string", ["description"] = "The scene a news photo of the story shows, with no text in it" },
                        ["notes"] = notes
                    },
                    ["required"] = new JsonArray("script", "post", "outlet", "handle", "scene")
                };
            return new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["lines"] = new JsonObject
                    {
                        ["type"] = "array",
                        ["description"] = "The lines in the order they are spoken",
                        ["items"] = new JsonObject
                        {
                            ["type"] = "object",
                            ["properties"] = new JsonObject
                            {
                                ["speaker"] = new JsonObject { ["type"] = "string", ["description"] = "Who speaks, as the transcript names them" },
                                ["voice"] = new JsonObject
                                {
                                    ["type"] = "string",
                                    ["description"] = "The speaker's voice",
                                    ["enum"] = new JsonArray(voices.Select(v => (JsonNode)v.Name).ToArray())
                                },
                                ["text"] = new JsonObject { ["type"] = "string", ["description"] = "The words spoken, nothing else" }
                            },
                            ["required"] = new JsonArray("speaker", "voice", "text")
                        }
                    },
                    ["notes"] = notes
                },
                ["required"] = new JsonArray("lines")
            };
        }

        /// <summary>What the script is written from: the scenario, the inject's values and its article</summary>
        public static string BuildPrompt(MselEntity msel, DataFieldEntity field, AudioDraftRequest request, Inject inject, IReadOnlyList<AiVoice> voices, AiVoice outletVoice)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Exercise: {msel.Name}");
            if (!string.IsNullOrWhiteSpace(msel.Description))
                sb.AppendLine($"Scenario: {Excerpt(DataValueDraftService.StripHtml(msel.Description), ValueLength)}");
            if (msel.Organizations.Any())
                sb.AppendLine($"Organizations: {string.Join("; ", msel.Organizations.OrderBy(o => o.ShortName).Select(o => $"{o.ShortName} ({o.Name})"))}");
            sb.AppendLine();
            sb.AppendLine($"The inject, delivered at {request.DeltaSeconds / 3600}:{request.DeltaSeconds / 60 % 60:00}:");
            foreach (var other in msel.DataFields.OrderBy(f => f.DisplayOrder))
            {
                if (other.Id == field.Id)
                    continue;
                var value = DataValueDraftService.StripHtml(inject.Values.GetValueOrDefault(other.Id));
                if (!string.IsNullOrWhiteSpace(value))
                    sb.AppendLine($"- {other.Name}: {Excerpt(value, ValueLength)}");
            }
            var article = ReadAloud(null, inject.Article);
            if (!string.IsNullOrWhiteSpace(article))
            {
                sb.AppendLine();
                sb.AppendLine($"Its {field.Name}:");
                sb.AppendLine(Excerpt(article, ArticleLength));
            }
            sb.AppendLine();
            sb.AppendLine("Voices (name: gender, accent):");
            foreach (var voice in voices)
                sb.AppendLine($"- {voice.Name}: {voice.Gender}, {voice.Label}{(voice.News ? ", reads as a newscaster" : "")}");
            if (request.Kind is AudioDraftKind.Broadcast or AudioDraftKind.Reel)
                sb.AppendLine($"The anchor is the outlet's own voice, {outletVoice.Name}: give it to the anchor and to no one else.");
            if (!string.IsNullOrWhiteSpace(request.Instruction))
            {
                sb.AppendLine();
                sb.AppendLine("The planner adds:");
                sb.AppendLine(request.Instruction.Trim());
            }
            return sb.ToString();
        }

        /// <summary>
        /// The model's script as lines to voice. Each speaker keeps the first voice they are given, and no two share one; the
        /// anchor of a broadcast or reel is always the outlet's voice. A reel's post, outlet and handle go on the result, and the
        /// scene of its photo comes back with its line.
        /// </summary>
        public static (List<ScriptLine> Lines, string Scene) ReadScript(JsonElement input, AudioDraftKind kind, IReadOnlyList<AiVoice> voices, AiVoice outletVoice, Inject inject, AudioDraftResult result)
        {
            string Text(string name) => input.ValueKind == JsonValueKind.Object && input.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
                ? Regex.Replace(v.GetString(), @"\s+", " ").Trim() : null;
            if (input.ValueKind == JsonValueKind.Object && input.TryGetProperty("notes", out var notes) && notes.ValueKind == JsonValueKind.Array)
                result.Notes.AddRange(notes.EnumerateArray().Where(n => n.ValueKind == JsonValueKind.String).Select(n => n.GetString().Trim()).Where(n => n.Length > 0));

            if (kind == AudioDraftKind.Reel)
            {
                result.Post = Text("post");
                result.Outlet = Text("outlet") is { Length: > 0 } outlet ? outlet : inject.SourceName?.TrimStart('@');
                result.Handle = Handle(Text("handle"), inject.SourceName ?? result.Outlet);
                var script = Text("script");
                return (string.IsNullOrWhiteSpace(script) ? [] : [new ScriptLine("Anchor", outletVoice.Name, script)], Text("scene"));
            }

            var names = voices.Select(v => v.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var bySpeaker = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var lines = new List<ScriptLine>();
            if (input.ValueKind == JsonValueKind.Object && input.TryGetProperty("lines", out var array) && array.ValueKind == JsonValueKind.Array)
            {
                foreach (var line in array.EnumerateArray().Where(l => l.ValueKind == JsonValueKind.Object))
                {
                    string Get(string name) => line.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? Regex.Replace(v.GetString(), @"\s+", " ").Trim() : null;
                    var text = Get("text");
                    if (string.IsNullOrWhiteSpace(text))
                        continue;
                    var speaker = string.IsNullOrWhiteSpace(Get("speaker")) ? "Speaker" : Get("speaker");
                    if (!bySpeaker.TryGetValue(speaker, out var voice))
                    {
                        var anchor = kind == AudioDraftKind.Broadcast && bySpeaker.Count == 0;
                        var asked = voices.FirstOrDefault(v => v.Name.Equals(Get("voice") ?? "", StringComparison.OrdinalIgnoreCase))?.Name;
                        var taken = bySpeaker.Values.ToHashSet(StringComparer.OrdinalIgnoreCase);
                        if (kind == AudioDraftKind.Broadcast)
                            taken.Add(outletVoice.Name);
                        voice = anchor ? outletVoice.Name
                            : asked != null && !taken.Contains(asked) ? asked
                            : voices.Select(v => v.Name).FirstOrDefault(n => !taken.Contains(n)) ?? asked ?? voices[0].Name;
                        bySpeaker[speaker] = voice;
                    }
                    lines.Add(new ScriptLine(speaker, voice, text));
                }
            }
            if (kind == AudioDraftKind.Broadcast)
                result.Outlet = inject.SourceName;
            return (lines, null);
        }

        /// <summary>A handle as social media writes one: the model's, or one made of the source's name</summary>
        public static string Handle(string handle, string source)
        {
            var own = Regex.Replace(handle ?? "", @"[^A-Za-z0-9_]", "");
            if (own.Length > 0)
                return "@" + own;
            var made = Regex.Replace(source ?? "", @"[^A-Za-z0-9_]", "");
            return made.Length > 0 ? "@" + made : null;
        }

        /// <summary>Each line voiced, a few at once, in the order they are heard</summary>
        private async Task<List<AudioDraftLine>> VoiceAsync(List<ScriptLine> lines, AudioDraftKind kind, IReadOnlyList<AiVoice> voices, CancellationToken ct)
        {
            using var gate = new SemaphoreSlim(ParallelLines);
            var anchor = lines.FirstOrDefault()?.Speaker;
            var tasks = lines.Select(async line =>
            {
                await gate.WaitAsync(ct);
                try
                {
                    var voice = voices.FirstOrDefault(v => v.Name.Equals(line.Voice, StringComparison.OrdinalIgnoreCase));
                    // the anchor of a broadcast or reel reads as a newscaster where the voice can
                    var news = kind is AudioDraftKind.Broadcast or AudioDraftKind.Reel && line.Speaker == anchor && voice?.News == true;
                    var speech = await _speech.SpeakAsync(line.Text, line.Voice, news ? AiSpeechStyle.News : null, ct);
                    return new AudioDraftLine
                    {
                        Speaker = line.Speaker,
                        Voice = line.Voice,
                        Text = line.Text,
                        Audio = "data:audio/mpeg;base64," + Convert.ToBase64String(speech.Mp3),
                        DurationMs = speech.DurationMs,
                        Marks = speech.Sentences.Select(m => new AudioDraftMark { StartMs = m.Time, Text = m.Text }).ToList(),
                        Words = speech.Words.Select(m => new AudioDraftMark { StartMs = m.Time, Text = m.Text }).ToList()
                    };
                }
                finally
                {
                    gate.Release();
                }
            }).ToList();
            return (await Task.WhenAll(tasks)).ToList();
        }

        /// <summary>The script as a Transcript disclosure, each line labelled with its speaker</summary>
        public static string Transcript(IEnumerable<AudioDraftLine> lines) =>
            "<details><summary><b>Transcript</b></summary>" +
            string.Concat(lines.Select(l => $"<p><b>{WebUtility.HtmlEncode(l.Speaker.ToUpperInvariant())}:</b> {WebUtility.HtmlEncode(l.Text)}</p>")) +
            "</details>";

        private static AudioDraftMarking Marking(MselEntity msel) =>
            ArticleMarkings.Bar(msel.ArticleMarking, msel.ArticleMarkingText) is var (text, background, color)
                ? new AudioDraftMarking { Text = text, Background = background, Color = color }
                : null;

        private static string Excerpt(string value, int length)
        {
            value = value.Trim();
            return value.Length <= length ? value : value[..length] + "...";
        }
    }
}
