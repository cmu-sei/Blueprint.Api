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
using Microsoft.Extensions.Logging;

namespace Blueprint.Api.Services
{
    public interface IMselDraftService
    {
        /// <summary>Asks the model for a MSEL draft shaped by a template. Nothing is saved.</summary>
        Task<MselDraftResult> DraftAsync(MselDraftRequest request, CancellationToken ct);
        /// <summary>The first of two steps: the design alone, and the questions the planner should answer before the injects</summary>
        Task<MselDraftResult> DesignAsync(MselDraftRequest request, CancellationToken ct);
        /// <summary>The second step: the injects, CITE tasks and questions, and read-in, written to an approved design</summary>
        Task<MselDraft> WriteInjectsAsync(MselEntity template, MselDraft design, string prompt, List<MselDesignAnswer> answers,
            Dictionary<string, CompetencyEntity> competencies, List<string> warnings, CancellationToken ct, CasterRange range = null);
        /// <summary>The full text of one long column of one outlined inject, written on its own so no answer runs out of room</summary>
        Task<string> WriteInjectTextAsync(MselEntity msel, MselDraft draft, int injectIndex, DataFieldEntity field, CancellationToken ct);
        /// <summary>The catalog metadata of a built MSEL, from everything written into it</summary>
        Task<MselDraftMetadata> WriteMetadataAsync(MselEntity msel, MselDraft draft, CancellationToken ct);
    }

    public class MselDraftService : IMselDraftService
    {
        private const string ToolName = "submit_msel_draft";
        private readonly ILogger<MselDraftService> _logger;
        private readonly BlueprintContext _context;
        private readonly IAiModelClient _aiModelClient;
        private readonly AiOptions _aiOptions;
        private readonly ClaimsPrincipal _user;
        private readonly IAiUsageContext _usage;

        private const string SystemPrompt =
@"You design and draft Master Scenario Event Lists (MSELs) for Crucible cyber exercises in Blueprint, the way an exercise design
team would: the scenario first, then the training objectives, then the moves and the injects that drive them.
A MSEL is organized as Move > Group > Inject. Moves are time-bounded phases of the exercise. Injects that share a delivery time form a group.
Write for an exercise planner who will review and edit every line before anything reaches participants.

Design:
- Start from the problem, the premise and the operational environment (political, military, economic, social, information,
  infrastructure, physical environment and time), and set an escalation ceiling the scenario never passes. Keep one scenario thread.
- Write 3 to 6 training objectives as task, condition and standard: the task participants perform, the conditions and resources they
  have, and the standard that shows it was done well (how fast, how complete, to what result). Number them O1, O2 ...
- Give each move a purpose (the reasoning it makes participants do), a situation update, the decisions participants are expected
  to reach, and controller notes for the facilitators.
- Make every inject earn its place: it forces a decision or action that serves an objective. Give each inject its expected action
  and the objective it serves. Keep participants inside the role being trained: the adversary acts; participants decide, coordinate
  and do their own staff work.
- Keep everything traceable: scenario, move, inject, expected action, objective, competency.
- Name the critical infrastructure sectors the scenario touches, from the CISA list, the primary one first. Give every
  organization its sector (none for one outside them, such as a news outlet), size, industry and country, and keep its people,
  systems, hours and reach in scale: a small water utility has no 24-hour security operations center.
- Follow the planner's design choices. Where they leave something open, choose a conservative default and say what you chose in
  plannerNotes.

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
- Write each move's situationDescription as a short briefing in HTML, not a block of text: an <h4> title, a paragraph on what has
  happened, a <ul> of what participants now know, and, where it helps, a two-column table of key facts (time, location, affected
  systems, who has been told). Use b, i, ul, ol, li, h4, p and table with border=""1"" cellpadding=""6"" cellspacing=""0"" only; no colors,
  no images, no style attributes, because it is shown in light and dark themes and in CITE.
- Write a read-in for participants to read before the first move: the scenario background, who they are and what they are
  responsible for, the organizations they will deal with, the rules of play, and how the two clocks work, with the scenario date
  and time the exercise opens. Use headings, short paragraphs, lists and a table where one helps; no images. Open the MSEL with an
  Information inject at minute 0 from EXCON telling participants to read the read-in page before play starts.
When the MSEL uses CITE:
- For each move, list the tasks each participant team must complete, as short imperative sentences the team checks off in CITE
  (for example: Isolate the affected hosts; Notify the state fusion center). They are tasks, never questions. Three to six per team per move.
- When the assessment is questions by move, write 3 to 5 open discussion questions for each move in citeQuestions: questions that
  make the team reason through the move's decisions and objectives, not questions with a single right answer.
When the MSEL uses Gallery:
- Most participant-facing injects are Gallery articles: news stories, social posts, emails, phone calls, intel and situation reports, orders.
  Pick the source type that matches the document, and name the outlet, account or office in the source name.
- From Org is the short name of the organization or team the article comes from. To Org is the team short names it goes to, or ALL.
- The summary is one plain-text sentence for the wall. The description or details column is the full document.
- Group the articles on 4 to 8 Gallery cards, starting with Exercise Control for the read-in and facilitator messages. Use the
  grouping the planner chose, or the one that best fits the scenario:
  by source or thread participants follow (the organization under attack, Public and Media, Government and Law Enforcement, the
  coordinating body); by organization, a card for each business, agency, country, military unit, subcommand or service; by critical
  infrastructure sector (Energy, Water, Communications, Transportation Systems ...); by country; or by network segment and key cyber
  terrain (the subnets, enclaves and critical assets the attack moves through). Put every Gallery article on one card. A card first
  appears on the wall with its first article.
Answer only by calling the tool.";

        public MselDraftService(BlueprintContext context, IAiModelClient aiModelClient, AiOptions aiOptions, IPrincipal user, IAiUsageContext usage,
            ILogger<MselDraftService> logger = null)
        {
            _logger = logger;
            _context = context;
            _aiModelClient = aiModelClient;
            _aiOptions = aiOptions;
            _user = user as ClaimsPrincipal;
            _usage = usage;
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
            var design = MselDraftRules.CleanDesign(request.Design, template);
            // one call writes the whole draft, so the assessment is settled here rather than by the model
            if (design.CiteAssessment == MselDraftRules.ModelChoice)
                design.CiteAssessment = MselDraftRules.DefaultAssessment(template);
            var schema = BuildSchema(fields, _aiOptions.MaxInjects, competencies, MselDraftRules.UsesCards(template), template.UseCite,
                design.CiteAssessment == MselDraftRules.QuestionsByMove);
            var prompt = BuildPrompt(request.Prompt, template, fields, _aiOptions.MaxInjects, competencies, design);

            // the draft's calls are counted under its own ID until it becomes a MSEL
            var draftId = Guid.NewGuid();
            _usage.Use("draft", draftId: draftId);
            var input = await _aiModelClient.InvokeToolAsync(SystemPrompt, prompt, ToolName,
                "Submit the drafted MSEL for the planner to review.", schema, ct);
            var unreadable = new List<string>();
            var draft = MselDraftRules.ReadDraft(input, unreadable);
            // the planner's choices, not the model's, decide what import builds
            draft.Design = design;
            draft.DraftId = draftId;

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
            result.Warnings.AddRange(unreadable);
            MselDraftRules.Normalize(draft, template, _aiOptions.MaxInjects, competencies, result.Warnings);
            result.Competencies = draft.Competencies
                .Select(id => new MselDraftCompetency { IdNumber = id, ShortName = competencies[id].ShortName })
                .ToList();
            return result;
        }

        private const string DesignStep =
@"This is the first of two steps. Write only the design: the name, description and length, the scenario, the objectives, the moves
(with each one's purpose and scenario time), the teams, organizations, Gallery cards and competencies.
The injects come in the second step, once the planner has reviewed the design.
Then ask the planner up to five questions whose answers would change the exercise materially, such as the escalation ceiling, who
the participants are, or what a move should test. Never ask what the description or the design choices already answer. Give each
question the answer you would use if the planner gives none, and say in a few words why it matters. Ask nothing when nothing material is open.";

        private const string InjectsStep =
@"This is the second of two steps. The planner has reviewed and approved the design below, and answered your questions. Keep to it:
its moves, teams, organizations and Gallery cards, by the same names and numbers. Write the injects, each move's situation briefing,
expected decisions, controller notes and discussion questions, the CITE tasks, and the read-in. Where an answer changes the design, follow the answer.
Move 0 is the pre-exercise, before STARTEX, and the last move the post-exercise hot wash, after ENDEX; Blueprint adds the STARTEX
and ENDEX rows. In move 0 put only the read-in notice and any welcome; in the last move only hot-wash and feedback material. Every
inject of play belongs to moves 1 and on, at or after move 1's start.
Write each inject as an outline: its time, type, card and short columns, its expected action and objective. Its long text (the
document, message or description) is written next, one inject at a time, from what you give here, so make each inject's title,
summary, sender and recipients specific enough to write it from and keep the storyline in them.
Write the scenario's indicator set once, in indicators, and use only it: the defenders' hosts (assets), each organization's own
domain (domains), and the adversary's infrastructure (adversary: C2 addresses, domains, files, phishing senders and user agents,
each with what it is for). Adversary addresses come from 192.0.2.0/24, 198.51.100.0/24 or 203.0.113.0/24, hosts from 10.0.0.0/8,
172.16.0.0/12 or 192.168.0.0/16, and every domain ends .example or .test; give a file its name, Blueprint makes up its hashes. Reuse
the same indicators across moves so participants can pivot from one alert or report to the next. In each inject, list in indicators
the values its document names (host names, addresses, domains, files, senders) and in techniques the ATT&CK techniques it shows, as
ID and name. Give two to four Intel articles the CtiEvent layout, a MISP-style CTI event of what is known by then, and every other
row the Standard layout.";

        public async Task<MselDraftResult> DesignAsync(MselDraftRequest request, CancellationToken ct)
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
            var design = MselDraftRules.CleanDesign(request.Design, template);
            var schema = DesignSchema(BuildSchema(fields, _aiOptions.MaxInjects, competencies, MselDraftRules.UsesCards(template), template.UseCite,
                design.CiteAssessment == MselDraftRules.QuestionsByMove),
                design.CiteAssessment == MselDraftRules.ModelChoice ? MselDraftRules.AssessmentChoices(template) : null);
            var prompt = BuildPrompt(request.Prompt, template, fields, _aiOptions.MaxInjects, competencies, design);

            var draftId = Guid.NewGuid();
            _usage.Use("design", draftId: draftId);
            var input = await _aiModelClient.InvokeToolAsync(SystemPrompt + "\n\n" + DesignStep, prompt, ToolName,
                "Submit the design and your questions for the planner to review.", schema, ct);
            var unreadable = new List<string>();
            var draft = MselDraftRules.ReadDraft(input, unreadable);
            foreach (var note in unreadable)
                _logger?.LogWarning("Design for template {TemplateId}: {Note}", template.Id, note);
            // a design without moves or teams is not one to review, however it came to be: the planner drafts again
            var missing = new[] { draft.Moves.Any() ? null : "moves", draft.Teams.Any() ? null : "teams" }.Where(m => m != null).ToList();
            if (missing.Any())
            {
                _logger?.LogWarning("Design for template {TemplateId} has no {Missing}", template.Id, string.Join(" or ", missing));
                throw new BadRequestException($"The model's design could not be read: it has no {string.Join(" or ", missing)}. Draft again; the answer differs each time.");
            }
            MselDraftRules.ResolveAssessment(design, input, template, unreadable);
            draft.Design = design;
            draft.DraftId = draftId;

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
                }).ToList(),
                Questions = MselDraftRules.ReadQuestions(input)
            };
            result.Warnings.AddRange(unreadable);
            MselDraftRules.Normalize(draft, template, _aiOptions.MaxInjects, competencies, result.Warnings, designOnly: true);
            result.Competencies = draft.Competencies
                .Select(id => new MselDraftCompetency { IdNumber = id, ShortName = competencies[id].ShortName })
                .ToList();
            return result;
        }

        public async Task<MselDraft> WriteInjectsAsync(MselEntity template, MselDraft design, string prompt, List<MselDesignAnswer> answers,
            Dictionary<string, CompetencyEntity> competencies, List<string> warnings, CancellationToken ct, CasterRange range = null)
        {
            if (!_aiOptions.Enabled)
                throw new InvalidOperationException("AI drafting is not enabled on this server.");
            var fields = MselDraftRules.DraftedFields(template).ToList();
            var schema = InjectsSchema(BuildSchema(fields, _aiOptions.MaxInjects, competencies, MselDraftRules.UsesCards(template), template.UseCite,
                design.Design?.CiteAssessment == MselDraftRules.QuestionsByMove), fields,
                (design.Organizations ?? new List<MselDraftOrganization>()).Select(o => o.ShortName).Where(n => !string.IsNullOrWhiteSpace(n)).ToList());
            var sb = new StringBuilder(BuildPrompt(prompt ?? design.Description ?? design.Name, template, fields, _aiOptions.MaxInjects, competencies, design.Design));
            sb.AppendLine();
            sb.AppendLine("The approved design:");
            sb.AppendLine(JsonSerializer.Serialize(new
            {
                design.Name,
                design.Description,
                design.DurationMinutes,
                design.Scenario,
                design.Objectives,
                Moves = design.Moves.Select(m => new { m.MoveNumber, m.Description, m.Purpose, m.StartMinutes, m.SituationTime, m.PlannedInjects }),
                Teams = design.Teams.Select(t => new { t.ShortName, t.Name, Description = DataValueDraftService.StripHtml(t.Description), t.Competencies }),
                Organizations = design.Organizations.Select(o => new { o.ShortName, o.Name, Description = DataValueDraftService.StripHtml(o.Description), o.Sector, o.Size, o.Industry, o.Country }),
                design.Cards,
                design.Competencies
            }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull }));
            // the design planned how many injects each move needs, within the cap, so none are written to be thrown away
            if (design.Moves.Sum(m => m.PlannedInjects ?? 0) is int planned && planned > 0)
                sb.AppendLine($"Each move's plannedInjects is the most it gets: write no more for a move than it plans, {planned} in all at most.");
            var answered = (answers ?? new List<MselDesignAnswer>()).Where(a => !string.IsNullOrWhiteSpace(a?.Answer)).ToList();
            if (answered.Any())
            {
                sb.AppendLine();
                sb.AppendLine("The planner's answers:");
                foreach (var answer in answered)
                    sb.AppendLine($"- {answer.Question?.Trim()}: {answer.Answer.Trim()}");
            }
            // the range participants work in: its machines are the scenario's hosts, so a report names one they can reach
            if (range?.Hosts.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("The range's machines, from its Caster workspace. They are the scenario's hosts: list each in assets by this name, with this address and network where given, and name no other host.");
                foreach (var host in range.Hosts)
                    sb.AppendLine($"- {host.Name}{(host.Role == null ? "" : $" ({host.Role})")}{(host.IpAddresses.Any() ? ": " + string.Join(", ", host.IpAddresses) : "")}{(host.Network == null ? "" : $" on {host.Network}")}{(host.NetworkName == null ? "" : $" ({host.NetworkName})")}{(host.Gateway == null ? "" : $", gateway {host.Gateway}")}");
                if (range.ExternalNetworks.Any())
                    sb.AppendLine($"The adversary's addresses may also come from the range's outside {(range.ExternalNetworks.Count == 1 ? "network" : "networks")}: {string.Join(", ", range.ExternalNetworks)}.");
            }

            // the MSEL being built, so its live progress and its usage are both found by it
            _usage.Use("injects", template.Id, design.DraftId == Guid.Empty ? null : design.DraftId);
            var input = await _aiModelClient.InvokeToolAsync(SystemPrompt + "\n\n" + InjectsStep, sb.ToString(), ToolName,
                "Submit the injects for the approved design.", schema, ct);
            var before = warnings.Count;
            var written = MselDraftRules.ReadDraft(input, warnings);
            foreach (var note in warnings.Skip(before))
                _logger?.LogWarning("Injects for MSEL {MselId}: {Note}", template.Id, note);

            // the design stays as approved; the second step adds to it
            design.Injects = written.Injects ?? new List<MselDraftInject>();
            design.ReadIn = written.ReadIn;
            design.CiteActions = written.CiteActions ?? new List<MselDraftCiteAction>();
            design.PlannerNotes = (design.PlannerNotes ?? new List<string>()).Concat(written.PlannerNotes ?? new List<string>()).ToList();
            foreach (var details in written.Moves ?? new List<MselDraftMove>())
            {
                var move = design.Moves.FirstOrDefault(m => m.MoveNumber == details.MoveNumber);
                if (move == null)
                    continue;
                if (!string.IsNullOrWhiteSpace(details.SituationDescription))
                    move.SituationDescription = details.SituationDescription;
                move.ExpectedDecisions = details.ExpectedDecisions;
                move.ControllerNotes = details.ControllerNotes;
                move.CiteQuestions = details.CiteQuestions;
            }
            // one indicator set for every document, made safe, and each inject's share of it
            design.Indicators = MselIndicatorRules.Clean(written.Indicators, design, range, warnings);
            MselDraftRules.Normalize(design, template, _aiOptions.MaxInjects, competencies, warnings);
            MselIndicatorRules.CleanInjects(design, template, warnings);
            return design;
        }

        private const string InjectTextPrompt =
@"You write the full text of one inject in a Master Scenario Event List (MSEL) for a Crucible cyber exercise in Blueprint.
The exercise design and the outline of every inject are below; write the one you are asked for, as its sender would write it, to its
recipients, at its time on the scenario clock. Keep to the outline and the storyline: the same organizations, people, places, times and
numbers as the injects around it, and nothing participants should only learn from a later inject. Make it something participants can act
on: it leads to the inject's expected action without stating it.
Use fictional organizations, people, domains and IP addresses (RFC 5737 / RFC 1918 ranges, example.com style domains); never real personal
data. Where the scenario's indicators are listed, name only those hosts, addresses, domains, hashes and senders.
Describe adversary activity as a defender would observe it; never working exploit code, malware or real credentials.
Write HTML with p, b, i, ul, ol, li, h4 and table with border=""1"" cellpadding=""6"" cellspacing=""0"" only: no colors, no images,
no style attributes. Answer only by calling the tool.";

        public async Task<string> WriteInjectTextAsync(MselEntity msel, MselDraft draft, int injectIndex, DataFieldEntity field, CancellationToken ct)
        {
            if (!_aiOptions.Enabled)
                throw new InvalidOperationException("AI drafting is not enabled on this server.");
            if (injectIndex < 0 || injectIndex >= (draft.Injects?.Count ?? 0))
                throw new BadRequestException("There is no such inject in the draft.");

            _usage.Use("rows", msel.Id, draft.DraftId == Guid.Empty ? null : draft.DraftId);
            var schema = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["html"] = new JsonObject { ["type"] = "string", ["description"] = $"The inject's {field.Name}, as an HTML fragment" },
                    ["notes"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" }, ["description"] = "Assumptions the planner should check" }
                },
                ["required"] = new JsonArray("html")
            };
            var input = await _aiModelClient.InvokeToolAsync(InjectTextPrompt, InjectTextRequest(msel, draft, injectIndex, field), "submit_inject_text",
                $"Submit the inject's {field.Name}.", schema, ct);
            var html = DataValueDraftService.CleanHtml(input.TryGetProperty("html", out var h) && h.ValueKind == JsonValueKind.String ? h.GetString() : null);
            if (string.IsNullOrWhiteSpace(DataValueDraftService.StripHtml(html)))
                throw new InvalidOperationException("The model wrote nothing for the inject. Try again.");
            return html;
        }

        private const string MetadataPrompt =
@"You catalog Master Scenario Event Lists (MSELs) for Crucible cyber exercises in Blueprint. From the exercise below, fill its IEEE 2881
learning metadata: the educational level of the participants it suits, its educational use, its course mode (Online for a remote
exercise, Onsite for one in a room, Blended for both), its subject areas, search keywords, and the language its material is written in.
Answer only by calling the tool.";

        public async Task<MselDraftMetadata> WriteMetadataAsync(MselEntity msel, MselDraft draft, CancellationToken ct)
        {
            if (!_aiOptions.Enabled)
                throw new InvalidOperationException("AI drafting is not enabled on this server.");
            _usage.Use("metadata", msel.Id, draft.DraftId == Guid.Empty ? null : draft.DraftId);
            var input = await _aiModelClient.InvokeToolAsync(MetadataPrompt, MetadataRequest(msel, draft), "submit_msel_metadata",
                "Submit the MSEL's catalog metadata.", MetadataSchema(), ct);
            var metadata = JsonSerializer.Deserialize<MselDraftMetadata>(input.GetRawText(), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return MselDraftRules.CleanMetadata(metadata, draft.Design);
        }

        /// <summary>
        /// The catalog metadata's schema: its own call at the finish, because in the design's schema it put the design over the size
        /// Bedrock's strict mode takes, and it describes the finished MSEL
        /// </summary>
        public static JsonObject MetadataSchema() => new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["educationalLevel"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("Beginner", "Intermediate", "Advanced") },
                ["educationalUse"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("Assessment", "Instruction", "Professional Support") },
                ["courseMode"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("Online", "Onsite", "Blended") },
                ["subject"] = new JsonObject { ["type"] = "string", ["description"] = "The topic areas the exercise covers, e.g. Incident response; Crisis communications" },
                ["keywords"] = new JsonObject { ["type"] = "string", ["description"] = "Comma-separated search tags, e.g. ransomware, tabletop exercise, healthcare" },
                ["language"] = new JsonObject { ["type"] = "string", ["description"] = "The language the exercise material is written in, as a BCP 47 tag, e.g. en-US" }
            },
            ["required"] = new JsonArray("educationalLevel", "educationalUse", "courseMode", "subject", "keywords", "language")
        };

        /// <summary>What the metadata is written from: the design in brief, the teams, the moves and the first inject's text</summary>
        public static string MetadataRequest(MselEntity msel, MselDraft draft)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Exercise: {draft.Name}");
            if (!string.IsNullOrWhiteSpace(draft.Description))
                sb.AppendLine($"Description: {draft.Description}");
            if (draft.Design != null)
                sb.AppendLine($"Format: {draft.Design.Format}; {draft.DurationMinutes} minutes; {draft.Moves?.Count ?? 0} moves; {draft.Injects?.Count ?? 0} injects");
            if (draft.Scenario != null)
            {
                sb.AppendLine($"Problem: {draft.Scenario.Problem}");
                if (!string.IsNullOrWhiteSpace(draft.Scenario.Environment))
                    sb.AppendLine($"Environment: {draft.Scenario.Environment}");
                if (draft.Scenario.Sectors?.Count > 0)
                    sb.AppendLine($"Sectors: {string.Join(", ", draft.Scenario.Sectors)}");
            }
            if (draft.Objectives?.Count > 0)
            {
                sb.AppendLine("Objectives:");
                foreach (var objective in draft.Objectives)
                    sb.AppendLine($"- {objective.Task}");
            }
            if (draft.Teams?.Count > 0)
                sb.AppendLine($"Teams: {string.Join("; ", draft.Teams.Select(t => $"{t.Name} ({OneLine(DataValueDraftService.StripHtml(t.Description))})"))}");
            if (draft.Competencies?.Count > 0)
                sb.AppendLine($"Competencies: {draft.Competencies.Count} chosen from the planner's frameworks");
            // the language is read from the material itself
            var sample = msel.ScenarioEvents?.SelectMany(e => e.DataValues).Select(v => DataValueDraftService.StripHtml(v.Value))
                .FirstOrDefault(t => !string.IsNullOrWhiteSpace(t) && t.Length > 80);
            if (sample != null)
                sb.AppendLine($"A passage of the material: {OneLine(sample.Length > 400 ? sample[..400] : sample)}");
            return sb.ToString();
        }

        /// <summary>
        /// What one inject's text is written from: the design, the inject's move, every inject in one line each, and the inject's
        /// outline. All but the last part is the same for every inject of the MSEL.
        /// </summary>
        public static string InjectTextRequest(MselEntity msel, MselDraft draft, int injectIndex, DataFieldEntity field)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Exercise: {draft.Name}");
            if (!string.IsNullOrWhiteSpace(draft.Description))
                sb.AppendLine($"Description: {draft.Description}");
            if (draft.Scenario != null)
            {
                sb.AppendLine($"Problem: {draft.Scenario.Problem}");
                sb.AppendLine($"Premise: {draft.Scenario.Premise}");
                if (!string.IsNullOrWhiteSpace(draft.Scenario.EscalationCeiling))
                    sb.AppendLine($"Escalation ceiling: {draft.Scenario.EscalationCeiling}");
            }
            sb.AppendLine();
            sb.AppendLine("Teams and organizations (short name: name, who they are):");
            foreach (var team in draft.Teams ?? new List<MselDraftTeam>())
                sb.AppendLine($"- {team.ShortName}: {team.Name}, participant team. {OneLine(DataValueDraftService.StripHtml(team.Description))}");
            foreach (var organization in draft.Organizations ?? new List<MselDraftOrganization>())
                sb.AppendLine($"- {organization.ShortName}: {organization.Name}{MselDraftRules.OrganizationFactsSuffix(organization)}. {OneLine(DataValueDraftService.StripHtml(organization.Description))}");
            sb.AppendLine();
            sb.AppendLine("Moves (number, starts at minute, scenario time: title, situation):");
            foreach (var move in draft.Moves ?? new List<MselDraftMove>())
                sb.AppendLine($"- {move.MoveNumber}, {move.StartMinutes}, {move.SituationTime}: {move.Description}. {OneLine(DataValueDraftService.StripHtml(move.SituationDescription))}");
            sb.AppendLine();
            sb.AppendLine("Every inject in order (#, minute, type, from, to: title):");
            var named = msel.DataFields.Where(f => f.GalleryArticleParameter == nameof(GalleryArticleParameter.Name)
                    || new[] { "title", "message", "name" }.Contains(f.Name.Trim().ToLowerInvariant()))
                .Select(f => f.Name).ToList();
            string Pick(MselDraftInject inject, string parameter) =>
                msel.DataFields.FirstOrDefault(f => f.GalleryArticleParameter == parameter) is DataFieldEntity f
                    && inject.Values != null && inject.Values.TryGetValue(f.Name, out var v) ? v : "";
            for (var i = 0; i < draft.Injects.Count; i++)
            {
                var inject = draft.Injects[i];
                var title = named.Select(n => inject.Values != null && inject.Values.TryGetValue(n, out var v) ? v : null).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
                sb.AppendLine($"#{i + 1}, {inject.Minutes}, {inject.EventType}, {Pick(inject, nameof(GalleryArticleParameter.FromOrg))}, {Pick(inject, nameof(GalleryArticleParameter.ToOrg))}: {OneLine(title)}");
            }
            sb.AppendLine();
            var target = draft.Injects[injectIndex];
            sb.AppendLine($"Write the {field.Name} of inject #{injectIndex + 1}. Its outline:");
            sb.AppendLine($"Minute: {target.Minutes}");
            sb.AppendLine($"Type: {target.EventType}");
            foreach (var (name, value) in target.Values ?? new Dictionary<string, string>())
                if (!string.IsNullOrWhiteSpace(value) && !string.Equals(name, field.Name, StringComparison.OrdinalIgnoreCase))
                    sb.AppendLine($"{name}: {value}");
            if (!string.IsNullOrWhiteSpace(target.ExpectedAction))
                sb.AppendLine($"Expected action: {target.ExpectedAction}");
            if (!string.IsNullOrWhiteSpace(field.Description))
                sb.AppendLine($"The {field.Name} column holds: {field.Description}");
            if (draft.Indicators != null)
            {
                sb.AppendLine();
                sb.Append(MselIndicatorRules.Describe(draft.Indicators, target, false));
            }
            return sb.ToString();
        }

        private static string OneLine(string text) =>
            string.IsNullOrEmpty(text) || text.Length <= 200 ? text : text[..200] + "...";

        /// <summary>
        /// Every property of every object required, but an inject's column values, of which a row fills only some. An answer
        /// constrained to its schema leaves out whatever is optional, so a design came back without its cards, metadata
        /// and CITE tasks; required, the model writes each part, empty where it has nothing.
        /// </summary>
        public static JsonObject RequireAll(JsonObject schema)
        {
            Require(schema);
            return schema;

            static void Require(JsonNode node)
            {
                if (node is not JsonObject obj)
                    return;
                if (obj["properties"] is JsonObject properties)
                {
                    obj["required"] = new JsonArray(properties.Select(p => (JsonNode)p.Key).ToArray());
                    foreach (var (name, property) in properties)
                        if (name != "values")
                            Require(property);
                }
                if (obj["items"] is JsonNode items)
                    Require(items);
            }
        }

        // the full draft's schema without the injects and the moves' details, and with the planner's questions
        private static JsonObject DesignSchema(JsonObject full, string[] assessmentChoices = null)
        {
            var properties = (JsonObject)full["properties"];
            foreach (var name in new[] { "injects", "readIn", "citeActions" })
                properties.Remove(name);
            // an enum costs little under strict mode, unlike a free-text field
            if (assessmentChoices != null)
                properties["citeAssessment"] = new JsonObject
                {
                    ["type"] = "string",
                    ["enum"] = new JsonArray(assessmentChoices.Select(c => (JsonNode)c).ToArray()),
                    ["description"] = "The CITE assessment that fits the exercise, as the design choices describe them"
                };
            // the briefing, decisions, notes and questions of a move are written with the injects, which know what happens in it;
            // each free-text field left out here keeps the design's schema within the size Bedrock's strict mode takes
            var move = (JsonObject)properties["moves"]["items"];
            foreach (var name in new[] { "situationDescription", "expectedDecisions", "controllerNotes", "citeQuestions" })
                ((JsonObject)move["properties"]).Remove(name);
            move["required"] = new JsonArray("moveNumber", "description", "startMinutes", "situationTime", "purpose", "plannedInjects");
            properties["questions"] = new JsonObject
            {
                ["type"] = "array",
                ["maxItems"] = 5,
                ["description"] = "Up to five questions whose answers would change the exercise materially; none when nothing material is open",
                ["items"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["question"] = new JsonObject { ["type"] = "string" },
                        ["why"] = new JsonObject { ["type"] = "string", ["description"] = "A few words on why the answer matters" },
                        ["suggested"] = new JsonObject { ["type"] = "string", ["description"] = "The answer the draft uses if the planner gives none" }
                    },
                    ["required"] = new JsonArray("question", "suggested")
                }
            };
            full["required"] = new JsonArray("name", "description", "durationMinutes", "scenario", "objectives", "moves", "teams", "organizations", "questions");
            return RequireAll(full);
        }

        /// <summary>The columns an outline leaves out, written for each inject on its own: those holding HTML documents</summary>
        public static List<DataFieldEntity> LongFields(IEnumerable<DataFieldEntity> fields) =>
            fields.Where(f => f.DataType == DataFieldType.Html).ToList();

        // the injects, read-in and CITE tasks of the full schema, and each move's details by number; the injects without their long columns
        private static JsonObject InjectsSchema(JsonObject full, List<DataFieldEntity> fields, List<string> organizations)
        {
            var all = (JsonObject)full["properties"];
            if (all["injects"]?["items"]?["properties"]?["values"] is JsonObject valuesSchema && valuesSchema["properties"] is JsonObject values)
            {
                foreach (var field in LongFields(fields))
                {
                    values.Remove(field.Name);
                    if (valuesSchema["required"] is JsonArray required && required.FirstOrDefault(r => r.GetValue<string>() == field.Name) is JsonNode node)
                        required.Remove(node);
                }
                // the address and network columns come from the indicator set, and are checked against it
                foreach (var field in fields.Where(f => values.ContainsKey(f.Name)))
                    if (field.DataType == DataFieldType.IpAddress)
                        values[field.Name]["description"] = "An IP address from the indicator set the row is about: one of its hosts' or the adversary's; empty for none";
                    else if (field.DataType == DataFieldType.Network)
                        values[field.Name]["description"] = "The network, in CIDR form, of one of the indicator set's hosts the row is about; empty for none";
            }
            // each inject's share of the indicator set, its ATT&CK techniques, and its layout
            if (all["injects"]?["items"]?["properties"] is JsonObject injectProperties)
            {
                injectProperties["indicators"] = new JsonObject
                {
                    ["type"] = "array",
                    ["description"] = "The indicator set's values the inject's document names: host names, addresses, domains, file names, senders; empty for none",
                    ["items"] = new JsonObject { ["type"] = "string" }
                };
                injectProperties["techniques"] = new JsonObject
                {
                    ["type"] = "array",
                    ["description"] = "The ATT&CK techniques the inject shows, as ID and name, e.g. T1566.001 Spearphishing Attachment; empty for none",
                    ["items"] = new JsonObject { ["type"] = "string" }
                };
                injectProperties["layout"] = new JsonObject
                {
                    ["type"] = "string",
                    ["enum"] = new JsonArray(MselIndicatorRules.StandardLayout, MselIndicatorRules.CtiEvent),
                    ["description"] = "CtiEvent for two to four Intel articles written as a MISP-style CTI event; Standard for every other row"
                };
            }
            var move = (JsonObject)all["moves"]["items"]["properties"];
            var moveDetails = new JsonObject
            {
                ["moveNumber"] = move["moveNumber"].DeepClone(),
                ["situationDescription"] = move["situationDescription"].DeepClone(),
                ["expectedDecisions"] = move["expectedDecisions"].DeepClone(),
                ["controllerNotes"] = move["controllerNotes"].DeepClone()
            };
            if (move["citeQuestions"] is JsonNode questions)
                moveDetails["citeQuestions"] = questions.DeepClone();
            var properties = new JsonObject
            {
                ["moves"] = new JsonObject
                {
                    ["type"] = "array",
                    ["description"] = "Each move of the design by its number, with what it asks of participants and controllers",
                    ["items"] = new JsonObject { ["type"] = "object", ["properties"] = moveDetails, ["required"] = new JsonArray("moveNumber", "situationDescription", "expectedDecisions") }
                },
                ["injects"] = all["injects"].DeepClone(),
                ["readIn"] = all["readIn"].DeepClone(),
                ["plannerNotes"] = all["plannerNotes"].DeepClone(),
                ["indicators"] = MselIndicatorRules.Schema(organizations)
            };
            if (all["citeActions"] is JsonNode actions)
                properties["citeActions"] = actions.DeepClone();
            return RequireAll(new JsonObject { ["type"] = "object", ["properties"] = properties, ["required"] = new JsonArray("moves", "injects", "readIn", "indicators") });
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
                .Include(m => m.ScoringModel)
                .Include(m => m.Cards)
                .AsSplitQuery()
                .SingleOrDefaultAsync(m => m.Id == templateMselId, ct);
            if (template == null)
                throw new EntityNotFoundException<MselEntity>("MSEL not found with ID=" + templateMselId);
            // the caller holds CreateMsels, which is what lets them view a template
            if (!await MselViewRequirement.IsMet(userId, templateMselId, true, context))
                throw new ForbiddenException();
            // untracked, so the columns added here go into the draft and never into the template
            MselDraftRules.AddDraftFields(template);
            return template;
        }

        // an enum makes the model's choice exact, but a whole framework would double the prompt
        private const int MaxCompetencyEnum = 200;

        private static JsonObject BuildSchema(List<DataFieldEntity> fields, int maxInjects, Dictionary<string, CompetencyEntity> competencies, bool useCards, bool useCite, bool citeQuestions)
        {
            var valueProperties = new JsonObject();
            var articleColumns = new List<string>();
            foreach (var field in fields)
            {
                var property = new JsonObject
                {
                    ["type"] = "string",
                    ["description"] = MselDraftRules.Describe(field)
                };
                // the columns Gallery shows on every article are asked for on every row: left optional, a constrained
                // answer leaves them out, and a row without a source type is no article at all
                if (MselDraftRules.IsArticleColumn(field))
                {
                    articleColumns.Add(field.Name);
                    if (field.GalleryArticleParameter == nameof(GalleryArticleParameter.Status))
                        property["description"] = MselDraftRules.GalleryStatusDescription;
                    else if (field.GalleryArticleParameter != nameof(GalleryArticleParameter.Name))
                        property["description"] = property["description"].GetValue<string>() + "; empty for a row that is not a Gallery article";
                }
                var options = MselDraftRules.AllowedOptions(field);
                if (options != null)
                    property["enum"] = new JsonArray(options.Select(o => (JsonNode)o).ToArray());
                valueProperties[field.Name] = property;
            }

            JsonObject Str(string description) => new JsonObject { ["type"] = "string", ["description"] = description };
            // the sixteen sectors are named in the description, not an enum: enums of them made the schema's grammar too large
            // for Bedrock's strict mode, and Normalize keeps only CISA's names anyway
            var sectorNames = string.Join("; ", MselDraftRules.Sectors);
            JsonObject Sector() => Str("One of CISA's critical infrastructure sectors, by its exact name: " + sectorNames);
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
            if (useCite)
                teamProperties["duties"] = Arr(Str("A duty, e.g. Incident Lead"), "2 to 4 duties members of the team take on during play, e.g. Incident Lead, Scribe, Communications; none for exercise control");
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
                    ["properties"] = valueProperties,
                    ["required"] = new JsonArray(articleColumns.Select(c => (JsonNode)c).ToArray())
                }
            };
            AddCompetencies(injectProperties, "Competencies participants demonstrate by acting on this inject; empty for injects that only set the scene");
            injectProperties["expectedAction"] = Str("What participants are expected to do when they get the inject; empty for one that only sets the scene");
            injectProperties["objective"] = Str("The ID of the objective the inject serves, e.g. O2");
            if (useCards)
                injectProperties["card"] = Str("The name of the Gallery card the article goes on, exactly as listed in cards; empty if the inject is not a Gallery article");

            var objectiveProperties = new JsonObject
            {
                ["id"] = Str("O1, O2 ..."),
                ["task"] = Str("What participants do"),
                ["condition"] = Str("The situation and resources they do it with"),
                ["standard"] = Str("How well, how fast or to what result")
            };
            AddCompetencies(objectiveProperties, "Competencies the objective exercises");
            var properties = new JsonObject
            {
                ["name"] = Str("Short exercise name"),
                ["description"] = Str("One paragraph: scenario, audience and objectives"),
                ["durationMinutes"] = Int("Total exercise length in minutes"),
                ["scenario"] = Obj(new JsonObject
                {
                    ["problem"] = Str("The one-line problem participants face"),
                    ["premise"] = Str("The scenario premise: what has happened and why it matters"),
                    ["environment"] = Str("The operational environment: political, military, economic, social, information, infrastructure, physical environment and time"),
                    ["escalationCeiling"] = Str("How far the scenario may escalate, and what it never does"),
                    ["assumptions"] = Arr(Str("A scenario assumption"), "What the scenario takes as given"),
                    ["sectors"] = Arr(Sector(), "The critical infrastructure sectors the scenario touches, the primary one first")
                }, "problem", "premise", "environment", "escalationCeiling", "sectors"),
                ["objectives"] = Arr(Obj(objectiveProperties, "id", "task", "condition", "standard"), "3 to 6 training objectives"),
                ["moves"] = Arr(Obj(new JsonObject
                {
                    ["moveNumber"] = Int("0, 1, 2 ... in time order"),
                    ["description"] = Str("Move title, e.g. Initial Detection"),
                    ["situationDescription"] = Str("The situation at the start of the move, as briefed to participants, in HTML"),
                    ["startMinutes"] = Int("Minutes from exercise start; the first move is 0"),
                    ["situationTime"] = Str("The scenario date and time when the move opens, in ISO 8601 UTC, e.g. 2026-03-04T08:00:00Z; later than the move before"),
                    ["purpose"] = Str("What the move is for: the reasoning it makes participants do"),
                    ["plannedInjects"] = Int("How many injects the move needs, counting every article, message and facilitation row; the whole exercise stays within the limit"),
                    ["expectedDecisions"] = Arr(Str("A decision participants are expected to reach"), "The decisions the move should produce"),
                    ["controllerNotes"] = Str("Notes for controllers and facilitators: what to watch for, when to push, what to hold back")
                }, "moveNumber", "description", "situationDescription", "startMinutes", "situationTime", "purpose", "expectedDecisions"), "Phases of the exercise in time order"),
                ["teams"] = Arr(Obj(teamProperties, "name", "shortName"), "Participant teams; reuse the template's teams when they fit"),
                ["organizations"] = Arr(Obj(new JsonObject
                {
                    ["name"] = Str("Fictional organization name"),
                    ["shortName"] = Str("Short name used in organization columns, no commas"),
                    ["description"] = Str("Who they are in the scenario"),
                    ["sector"] = Str("The organization's critical infrastructure sector, by CISA's exact name (" + sectorNames + "), or None for one outside them"),
                    ["size"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray(MselDraftRules.Sizes.Select(x => (JsonNode)x).ToArray()), ["description"] = "Small (under 100 people), Medium (100 to 999), Large (1,000 to 9,999) or Enterprise (10,000 or more)" },
                    ["industry"] = Str("What it does within its sector, e.g. regional acute-care hospital, container port, municipal water utility"),
                    ["country"] = Str("The country it is in; fictional where the scenario's geography is")
                }, "name", "shortName", "description", "sector", "size", "industry", "country"), "Simulated organizations that send or receive injects (exercise control, adversary, press, agencies)"),
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
            if (citeQuestions)
                ((JsonObject)properties["moves"]["items"]["properties"])["citeQuestions"] =
                    Arr(Str("An open discussion question"), "3 to 5 discussion questions for the move, for CITE");
            if (useCite)
                properties["citeActions"] = Arr(Obj(new JsonObject
                {
                    ["moveNumber"] = Int("The move the action belongs to"),
                    ["team"] = Str("The short name of the participant team that takes the action"),
                    ["description"] = Str("The task, as a short imperative sentence")
                }, "moveNumber", "team", "description"), "The tasks each participant team must complete in each move, for CITE");
            if (useCards)
                properties["cards"] = Arr(Obj(new JsonObject
                {
                    ["name"] = Str("Card name shown on the Gallery wall, e.g. Public and Media"),
                    ["description"] = Str("One sentence: whose articles or which thread the card holds")
                }, "name", "description"), "Gallery cards in wall order, starting with Exercise Control; reuse the template's cards when they fit");
            AddCompetencies(properties, "Every competency the exercise lets participants demonstrate");
            return Obj(properties, "name", "description", "durationMinutes", "scenario", "objectives", "moves", "teams", "organizations", "injects", "readIn");
        }

        public static string BuildPrompt(string plannerPrompt, MselEntity template, List<DataFieldEntity> fields, int maxInjects, Dictionary<string, CompetencyEntity> competencies, MselDraftDesign design = null)
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
            sb.AppendLine();
            sb.AppendLine("The planner's design choices (decide anything marked open, and note what you chose):");
            foreach (var line in MselDraftRules.DescribeDesign(design ?? new MselDraftDesign()))
                sb.AppendLine($"- {line}");
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
            _plannerGalleryParameters.Contains(field.GalleryArticleParameter) || field.DataType switch
            {
                // move and group follow from the delivery time in Blueprint
                DataFieldType.Integer => _derivedIntegerFields.Contains(field.Name.Trim().ToLower()),
                DataFieldType.DateTime => field.Name.Contains("delivery", StringComparison.OrdinalIgnoreCase),
                DataFieldType.Status => true,
                DataFieldType.User => true,
                DataFieldType.Card => true,
                // filled from each inject's competencies, which are checked against the list
                DataFieldType.Competency => true,
                // filled from each inject's expected action
                DataFieldType.String or DataFieldType.Html when IsExpectedActionField(field) => true,
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
            (GalleryArticleParameter.CardId, ["Card"], [DataFieldType.Card]),
            // Gallery needs these mapped too: the draft chooses each article's status color, and leaves the link to the planner
            (GalleryArticleParameter.Status, ["Card Status", "Gallery Status"], [DataFieldType.String]),
            (GalleryArticleParameter.Url, ["Url", "URL"], [DataFieldType.Url, DataFieldType.String]),
            (GalleryArticleParameter.OpenInNewTab, ["Open In New Tab"], [DataFieldType.Boolean, DataFieldType.Checkbox])
        ];
        private static readonly string[] _plannerGalleryParameters =
            [nameof(GalleryArticleParameter.Url), nameof(GalleryArticleParameter.OpenInNewTab)];

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
            // an article's body is a styled document, which a plain text column would show as markup, and which the Exercise
            // View would show whole on every row; participants read it in Gallery, the view keeps to its title and summary
            foreach (var body in msel.DataFields.Where(f => f.GalleryArticleParameter == nameof(GalleryArticleParameter.Description)))
            {
                if (body.DataType == DataFieldType.String)
                    body.DataType = DataFieldType.Html;
                body.OnExerciseView = false;
            }
            // a planner sorts and scans the injects by where each comes from, so the source type is on the list from the start
            foreach (var sourceType in msel.DataFields.Where(f => f.GalleryArticleParameter == nameof(GalleryArticleParameter.SourceType)))
            {
                sourceType.OnScenarioEventList = true;
                sourceType.IsShownOnDefaultTab = true;
            }
        }

        public const string PreExercise = "PreExercise";
        public const string PostExercise = "PostExercise";
        // the hot wash after ENDEX
        private const int PostExerciseMinutes = 30;

        /// <summary>
        /// Puts play between a pre-exercise move and a post-exercise move: move 0 opens at the doors-open time, so those who
        /// come early have the read-in and somewhere to wait; STARTEX opens move 1, so CITE and Gallery move to it; ENDEX opens
        /// the last move, the hot wash. The moves of play are numbered from 1 and start a lead time later, and the injects and
        /// CITE tasks follow them. A draft that already has them is left as it is.
        /// </summary>
        public static void AddPreAndPostMoves(MselDraft draft)
        {
            draft.Moves ??= new List<MselDraftMove>();
            if (!draft.Moves.Any() || draft.Moves.Any(m => m.Phase != null))
                return;
            var lead = draft.Design?.LeadMinutes ?? 15;
            var play = draft.Moves.OrderBy(m => m.StartMinutes).ThenBy(m => m.MoveNumber).ToList();
            var shift = 1 - play.Min(m => m.MoveNumber);
            var playMinutes = Math.Max(draft.DurationMinutes, play.Max(m => m.StartMinutes) + 30);
            foreach (var move in play)
            {
                move.MoveNumber += shift;
                move.StartMinutes += lead;
            }
            // the read-in notice stays when the doors open; the rest of play moves with it
            foreach (var inject in draft.Injects ?? new List<MselDraftInject>())
                if (!(inject.Minutes == 0 && inject.EventType == nameof(EventType.Information)))
                    inject.Minutes += lead;
            foreach (var action in draft.CiteActions ?? new List<MselDraftCiteAction>())
                action.MoveNumber += shift;

            draft.Moves = new List<MselDraftMove>
            {
                new()
                {
                    MoveNumber = 0,
                    Phase = PreExercise,
                    Description = "Pre-exercise",
                    StartMinutes = 0,
                    SituationTime = play[0].SituationTime,
                    Purpose = "Doors open: participants read the read-in, check they can reach their tools, and wait for STARTEX",
                    SituationDescription = "<h4>Pre-exercise</h4><p>Welcome. Read the read-in page and check that you can open Gallery, CITE and Player. " +
                        "Play starts at STARTEX.</p>"
                }
            };
            draft.Moves.AddRange(play);
            draft.Moves.Add(new MselDraftMove
            {
                MoveNumber = play.Max(m => m.MoveNumber) + 1,
                Phase = PostExercise,
                Description = "Post-exercise: hot wash",
                StartMinutes = lead + playMinutes,
                SituationTime = play[^1].SituationTime,
                Purpose = "ENDEX: the team reviews what it did against the objectives, and what it would do differently",
                SituationDescription = "<h4>ENDEX</h4><p>The exercise has ended. Join the hot wash: what went well, what did not, and what you would change.</p>"
            });
            draft.DurationMinutes = lead + playMinutes + PostExerciseMinutes;
        }

        public const string QuestionsByMove = "QuestionsByMove";
        public const string MissionImpact = "MissionImpact";
        public const string Likelihood = "Likelihood";
        public const string Nciss = "Nciss";
        public const string NoAssessment = "None";
        // the scoring model the template already links
        public const string TemplateModel = "Template";
        /// <summary>The planner leaves the assessment to the model, which chooses in the design step</summary>
        public const string ModelChoice = "Auto";
        private static readonly string[] _assessments = [QuestionsByMove, MissionImpact, Likelihood, Nciss, NoAssessment, TemplateModel, ModelChoice];

        /// <summary>The assessments the model may choose for a template: the template's own model only where it has one</summary>
        public static string[] AssessmentChoices(MselEntity template) =>
            HasModel(template)
                ? [QuestionsByMove, MissionImpact, Likelihood, Nciss, NoAssessment, TemplateModel]
                : [QuestionsByMove, MissionImpact, Likelihood, Nciss, NoAssessment];

        /// <summary>What stands in when nothing chose: the template's own model where it has one, else questions by move</summary>
        public static string DefaultAssessment(MselEntity template) => HasModel(template) ? TemplateModel : QuestionsByMove;

        /// <summary>The model's choice of assessment, where the planner left it to the model; the default where it chose nothing usable</summary>
        public static void ResolveAssessment(MselDraftDesign design, JsonElement input, MselEntity template, List<string> warnings)
        {
            if (design.CiteAssessment != ModelChoice)
                return;
            var chosen = input.ValueKind == JsonValueKind.Object && input.TryGetProperty("citeAssessment", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString()?.Trim() : null;
            var choice = AssessmentChoices(template).FirstOrDefault(a => a.Equals(chosen, StringComparison.OrdinalIgnoreCase));
            if (choice == null)
            {
                choice = DefaultAssessment(template);
                warnings.Add($"The model did not choose a CITE assessment; using {AssessmentName(choice)}. Choose another in the design choices and draft again if it does not fit.");
            }
            design.CiteAssessment = choice;
        }

        public static string AssessmentName(string assessment) => assessment switch
        {
            QuestionsByMove => "questions by move",
            MissionImpact => "the DoD CTT mission impact matrix",
            Likelihood => "the DoD CTT likelihood matrix",
            Nciss => "CISA NCISS",
            TemplateModel => "the template's scoring model",
            NoAssessment => "no assessment",
            _ => assessment
        };

        /// <summary>The columns Gallery shows on every article, which every row is asked for</summary>
        public static bool IsArticleColumn(DataFieldEntity field) =>
            field.DataType != DataFieldType.Html && field.GalleryArticleParameter is string parameter && _articleParameters.Contains(parameter);

        private static readonly string[] _articleParameters =
        [
            nameof(GalleryArticleParameter.Name), nameof(GalleryArticleParameter.Summary), nameof(GalleryArticleParameter.FromOrg),
            nameof(GalleryArticleParameter.ToOrg), nameof(GalleryArticleParameter.SourceType), nameof(GalleryArticleParameter.SourceName),
            nameof(GalleryArticleParameter.DatePosted), nameof(GalleryArticleParameter.Status)
        ];
        private static readonly string[] _cardGroupings = ["Sources", "Organizations", "Sectors", "Countries", "Terrain"];
        /// <summary>The 16 critical infrastructure sectors, as CISA names them</summary>
        public static readonly string[] Sectors =
        [
            "Chemical", "Commercial Facilities", "Communications", "Critical Manufacturing", "Dams", "Defense Industrial Base",
            "Emergency Services", "Energy", "Financial Services", "Food and Agriculture", "Government Services and Facilities",
            "Healthcare and Public Health", "Information Technology", "Nuclear Reactors, Materials, and Waste", "Transportation Systems",
            "Water and Wastewater Systems"
        ];
        public static readonly string[] Sizes = ["Small", "Medium", "Large", "Enterprise"];

        /// <summary>The sectors named that are CISA's, as CISA spells them, each once</summary>
        public static List<string> PickSectors(IEnumerable<string> names) =>
            (names ?? Enumerable.Empty<string>())
                .Select(n => Sectors.FirstOrDefault(s => s.Equals(n?.Trim(), StringComparison.OrdinalIgnoreCase)))
                .Where(s => s != null).Distinct().ToList();

        /// <summary>An organization's facts as one line, for the prompts and pages that describe it</summary>
        public static string OrganizationFacts(MselDraftOrganization o)
        {
            var parts = new[] { o.Sector, o.Size, o.Industry, o.Country }.Where(x => !string.IsNullOrWhiteSpace(x) && x != "None");
            return string.Join(", ", parts);
        }

        /// <summary>The facts in parentheses after an organization's name, or nothing when it has none</summary>
        public static string OrganizationFactsSuffix(MselDraftOrganization o) =>
            OrganizationFacts(o) is { Length: > 0 } facts ? $" ({facts})" : "";
        private static readonly string[] _formats = ["Discussion", "Functional", "Hybrid"];
        private static readonly string[] _expectedActionNames = ["Expected Actions", "Expected Action"];

        /// <summary>
        /// The planner's design choices in a form drafting and import can trust. Without CITE there is no assessment;
        /// with CITE and none chosen, it is the template's scoring model when it links one, else questions by move, the
        /// default discussion model of the author-ttx workflow.
        /// </summary>
        // a template with a scoring model links one in CITE or authors its own, which the drafted MSEL gets a copy of
        public static bool HasModel(MselEntity template) => template.CiteScoringModelId != null || template.ScoringModel != null;

        public static MselDraftDesign CleanDesign(MselDraftDesign design, MselEntity template)
        {
            design ??= new MselDraftDesign();
            // the planner's choice, else the template's
            var marking = string.IsNullOrWhiteSpace(design.Marking)
                ? ArticleMarkings.Clean(template.ArticleMarking, template.ArticleMarkingText)
                : ArticleMarkings.Clean(design.Marking, design.MarkingText);
            string Pick(string value, string[] allowed) => allowed.FirstOrDefault(a => a.Equals(value?.Trim(), StringComparison.OrdinalIgnoreCase));
            string Text(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim().Length > 300 ? value.Trim()[..300] : value.Trim();
            return new MselDraftDesign
            {
                Format = Pick(design.Format, _formats),
                Structure = Text(design.Structure),
                // the model chooses unless the planner did
                CiteAssessment = !template.UseCite ? NoAssessment
                    : Pick(design.CiteAssessment, _assessments) is string chosen && (chosen != TemplateModel || HasModel(template)) ? chosen
                    : ModelChoice,
                Moves = design.Moves is int moves ? Math.Clamp(moves, 1, 12) : null,
                DurationMinutes = design.DurationMinutes is int minutes ? Math.Clamp(minutes, 15, 2880) : null,
                ScenarioSpan = Text(design.ScenarioSpan),
                LeadershipBrief = design.LeadershipBrief,
                CardGrouping = template.UseGallery ? Pick(design.CardGrouping, _cardGroupings) : null,
                ProfilePages = design.ProfilePages,
                LeadMinutes = design.LeadMinutes is int lead ? Math.Clamp(lead, 0, 240) : null,
                Sectors = PickSectors(design.Sectors),
                Marking = marking.Marking,
                MarkingText = marking.Text
            };
        }

        /// <summary>The design choices as the model is told them, one line each</summary>
        public static IEnumerable<string> DescribeDesign(MselDraftDesign design)
        {
            const string open = "open";
            yield return $"Format: {design.Format switch { "Discussion" => "a discussion-based tabletop: participants talk through decisions, no hands-on tasks", "Functional" => "functional: participants work hands-on in the range", "Hybrid" => "hybrid: discussion moves with some hands-on work in the range", _ => open }}";
            yield return $"Participant structure: {design.Structure ?? open}";
            yield return $"Critical infrastructure sectors: {(design.Sectors.Any() ? string.Join(", ", design.Sectors) : open)}";
            yield return $"CITE assessment: {design.CiteAssessment switch { QuestionsByMove => "questions by move: write citeQuestions for every move", MissionImpact => "the DoD Cyber Table Top mission impact matrix: teams score mission impact each move, so write no discussion questions", Likelihood => "the DoD Cyber Table Top likelihood matrix: teams score each attack's cost and chance of success, so write no discussion questions", Nciss => "the CISA National Cyber Incident Scoring System (NCISS): each move, teams score the incident's functional impact, observed activity, where in the network it is, the actor, information impact, recoverability, cross-sector dependency and potential impact, so write no discussion questions, and give the injects the details teams need to score each of these", TemplateModel => "the template's own scoring model, so write no discussion questions", NoAssessment => "none", ModelChoice => "open: choose the one that fits and return it as citeAssessment. QuestionsByMove: discussion questions each move, the usual choice for a tabletop. MissionImpact: teams score the mission impact of cyber effects each move (DoD Cyber Table Top). Likelihood: teams score each attack's cost and chance of success (DoD CTT). Nciss: teams score the incident as CISA's NCISS does; suits incident response play with enough detail to score. None: CITE adds nothing. Template: the template's own scoring model, when its matrix fits the scenario", _ => open }}";
            yield return $"Moves: {(design.Moves?.ToString() ?? open)}";
            yield return $"Exercise length in real time: {(design.DurationMinutes is int minutes ? $"{minutes} minutes" : open)}";
            yield return $"Scenario time the exercise covers: {design.ScenarioSpan ?? open}";
            yield return $"Final move is a brief to leadership: {design.LeadershipBrief switch { true => "yes", false => "no", _ => open }}";
            yield return $"Gallery cards: {design.CardGrouping switch { "Sources" => "one per source or thread participants follow", "Organizations" => "one per organization: each business, agency, country, military unit, subcommand or service", "Sectors" => "one per critical infrastructure sector the scenario touches", "Countries" => "one per country", "Terrain" => "one per network segment or piece of key cyber terrain: the subnets, enclaves and critical assets", _ => open }}";
        }

        /// <summary>
        /// The move and inject number Gallery gives each row, as <c>ScenarioEventService.GetMovesAndInjects</c> numbers them
        /// for the push: rows in time order, the inject number starting at 0 in each move and rising with each new time.
        /// </summary>
        public static Dictionary<ScenarioEventEntity, (int Move, int Inject)> GalleryMovesAndInjects(IEnumerable<ScenarioEventEntity> events, IEnumerable<MoveEntity> moves)
        {
            var rows = events.OrderBy(e => e.DeltaSeconds).ToList();
            var ordered = moves.OrderBy(m => m.DeltaSeconds).ToList();
            var result = new Dictionary<ScenarioEventEntity, (int, int)>();
            var m = 0;
            var inject = 0;
            var deltaSeconds = rows.FirstOrDefault()?.DeltaSeconds ?? 0;
            foreach (var row in rows)
            {
                if (ordered.Count == 0 || m == ordered.Count - 1 || row.DeltaSeconds < ordered[m + 1].DeltaSeconds)
                {
                    if (row.DeltaSeconds != deltaSeconds)
                        inject++;
                }
                else
                {
                    while (m < ordered.Count - 1 && row.DeltaSeconds >= ordered[m + 1].DeltaSeconds)
                        m++;
                    inject = 0;
                }
                deltaSeconds = row.DeltaSeconds;
                result[row] = (ordered.Count > m ? ordered[m].MoveNumber : 0, inject);
            }
            return result;
        }

        /// <summary>The column that names a row: the Gallery title, else a text column called Title, Message or Name</summary>
        public static DataFieldEntity TitleField(MselEntity msel) =>
            msel.DataFields.FirstOrDefault(f => f.GalleryArticleParameter == nameof(GalleryArticleParameter.Name))
            ?? msel.DataFields.OrderBy(f => f.DisplayOrder).FirstOrDefault(f => f.DataType == DataFieldType.String
                && new[] { "title", "message", "name" }.Contains(f.Name.Trim().ToLowerInvariant()));

        /// <summary>The column a draft writes each inject's expected action to; shown to controllers, not on the exercise view</summary>
        public static bool IsExpectedActionField(DataFieldEntity field) =>
            field.DataType is DataFieldType.String or DataFieldType.Html
            && _expectedActionNames.Any(n => field.Name.Trim().Equals(n, StringComparison.OrdinalIgnoreCase));

        /// <summary>The template's columns plus those a draft needs: the Gallery article columns, and Expected Actions</summary>
        public static void AddDraftFields(MselEntity msel)
        {
            AddGalleryFields(msel);
            if (!msel.DataFields.Any(IsExpectedActionField))
                msel.DataFields.Add(new DataFieldEntity
                {
                    Id = Guid.NewGuid(),
                    MselId = msel.Id,
                    Name = "Expected Actions",
                    Description = "What participants are expected to do; for controllers",
                    DataType = DataFieldType.String,
                    DisplayOrder = msel.DataFields.Select(f => f.DisplayOrder).DefaultIfEmpty(0).Max() + 1,
                    OnScenarioEventList = true,
                    OnExerciseView = false,
                    IsShownOnDefaultTab = true
                });
            // the read-in notice and the facilitator's notes are Information and Facilitation rows, which show only the columns
            // marked for them: with none marked they listed as a bare type
            var shown = msel.DataFields.Where(f => IsExpectedActionField(f) || f.GalleryArticleParameter == nameof(GalleryArticleParameter.Name)
                || new[] { "title", "name", "message" }.Contains(f.Name.Trim().ToLowerInvariant())).ToList();
            if (!msel.DataFields.Any(f => f.IsInformationField))
                foreach (var field in shown)
                    field.IsInformationField = true;
            if (!msel.DataFields.Any(f => f.IsFacilitationField))
                foreach (var field in shown)
                    field.IsFacilitationField = true;
            // the Assessor View lists only the columns marked for it, so with none marked an assessor saw no events: it gets the
            // title and Expected Actions, the competencies assessed, and who the inject goes to, which picks the team to assess
            if (!msel.DataFields.Any(f => f.IsAssessorVisible))
            {
                foreach (var field in shown.Concat(msel.DataFields.Where(f => f.DataType == DataFieldType.Competency
                    || (f.DataType == DataFieldType.Organization && !f.Name.Trim().StartsWith("From", StringComparison.OrdinalIgnoreCase)))))
                    field.IsAssessorVisible = true;
                if (!msel.ShowTimeOnAssessorView && !msel.ShowMoveOnAssessorView && !msel.ShowGroupOnAssessorView && !msel.ShowIntegrationTargetOnAssessorView)
                {
                    msel.ShowMoveOnAssessorView = true;
                    msel.ShowTimeOnAssessorView = true;
                }
            }
        }

        /// <summary>Whether the draft puts Gallery articles on cards: the MSEL uses Gallery and has a column for the card</summary>
        public static bool UsesCards(MselEntity template) =>
            template.UseGallery && template.DataFields.Any(f => f.GalleryArticleParameter == nameof(GalleryArticleParameter.CardId));

        public static IEnumerable<DataFieldEntity> DraftedFields(MselEntity template) =>
            template.DataFields
                .Where(f => !IsServerFilled(f))
                .OrderBy(f => f.DisplayOrder);

        public const string GalleryStatusDescription =
            "The article's status color on the Gallery wall: Gray for background or routine news, Green for good news or a " +
            "problem resolved, Yellow for a developing or worrying situation, Orange for a serious one, Red for a critical one; " +
            "Gray for a row that is not a Gallery article";

        /// <summary>A Gallery status color as Gallery names it, Gray when the answer is none of them or is missing</summary>
        public static string GalleryStatus(string value) =>
            Enum.TryParse<GalleryItemStatus>((value ?? "").Trim(), true, out var status) && Enum.IsDefined(status)
                ? status.ToString()
                : nameof(GalleryItemStatus.Gray);

        public static List<string> AllowedOptions(DataFieldEntity field)
        {
            // Gallery's status colors, which Blueprint offers as a color picker
            if (field.GalleryArticleParameter == nameof(GalleryArticleParameter.Status))
                return Enum.GetNames(typeof(GalleryItemStatus)).ToList();
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
        public static void Normalize(MselDraft draft, MselEntity template, int maxInjects, IReadOnlyDictionary<string, CompetencyEntity> competencies, List<string> warnings, bool designOnly = false)
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
            draft.Design = CleanDesign(draft.Design, template);
            // past the design step the model has had its say; a choice still open takes the default
            if (!designOnly && draft.Design.CiteAssessment == ModelChoice)
                draft.Design.CiteAssessment = DefaultAssessment(template);
            draft.Metadata = CleanMetadata(draft.Metadata, draft.Design);
            draft.Scenario ??= new MselDraftScenario();
            draft.Scenario.Assumptions = Lines(draft.Scenario.Assumptions);
            draft.Objectives ??= new List<MselDraftObjective>();

            // objectives: a task each, and a unique ID injects can name
            var objectives = new List<MselDraftObjective>();
            foreach (var objective in draft.Objectives.Where(o => !string.IsNullOrWhiteSpace(o?.Task)))
            {
                var id = objective.Id?.Trim().ToUpperInvariant();
                if (string.IsNullOrEmpty(id) || objectives.Any(o => o.Id == id))
                    id = $"O{Enumerable.Range(1, 100).First(n => objectives.All(o => o.Id != $"O{n}"))}";
                objective.Id = id;
                objective.Task = objective.Task.Trim();
                objectives.Add(objective);
            }
            draft.Objectives = objectives;

            // teams and organizations: unique short names, without the commas that separate team lists
            draft.Teams = UniqueByShortName(draft.Teams, t => t.ShortName, (t, s) => t.ShortName = s, t => t.Name, warnings, "team");
            draft.Organizations = UniqueByShortName(draft.Organizations, o => o.ShortName, (o, s) => o.ShortName = s, o => o.Name, warnings, "organization");
            // sectors by CISA's names, the planner's choice first where it was made
            draft.Scenario ??= new MselDraftScenario();
            var named = draft.Scenario.Sectors ?? new List<string>();
            var unknownSectors = named.Where(n => !Sectors.Any(s => s.Equals(n?.Trim(), StringComparison.OrdinalIgnoreCase))).ToList();
            if (unknownSectors.Any())
                warnings.Add($"Dropped sectors that are not CISA critical infrastructure sectors: {string.Join(", ", unknownSectors)}.");
            draft.Scenario.Sectors = PickSectors((draft.Design?.Sectors ?? new List<string>()).Concat(named).Append(draft.Scenario.PrimarySector));
            // the primary sector is the first the model named, unless the draft says otherwise
            draft.Scenario.PrimarySector = PickSectors([draft.Scenario.PrimarySector]).FirstOrDefault() ?? PickSectors(named).FirstOrDefault() ?? draft.Scenario.Sectors.FirstOrDefault();
            foreach (var organization in draft.Organizations)
            {
                organization.Sector = PickSectors([organization.Sector]).FirstOrDefault();
                organization.Size = Sizes.FirstOrDefault(z => z.Equals(organization.Size?.Trim(), StringComparison.OrdinalIgnoreCase));
                organization.Industry = string.IsNullOrWhiteSpace(organization.Industry) ? null : organization.Industry.Trim();
                organization.Country = string.IsNullOrWhiteSpace(organization.Country) ? null : organization.Country.Trim();
            }
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
            PlanInjects(draft.Moves, maxInjects);
            // what each move asks of participants and facilitators; discussion questions only for a questions-by-move assessment
            foreach (var move in draft.Moves)
            {
                move.SituationDescription = SituationHtml(move.SituationDescription);
                move.ExpectedDecisions = Lines(move.ExpectedDecisions);
                move.CiteQuestions = draft.Design.CiteAssessment == QuestionsByMove ? Lines(move.CiteQuestions) : new List<string>();
                move.Purpose = string.IsNullOrWhiteSpace(move.Purpose) ? null : move.Purpose.Trim();
                move.ControllerNotes = string.IsNullOrWhiteSpace(move.ControllerNotes) ? null : move.ControllerNotes.Trim();
            }
            // the pre-exercise has nothing to discuss, and the hot wash has the after-action review
            var unasked = draft.Moves.Where(m => m.Phase == null && !m.CiteQuestions.Any()).ToList();
            if (!designOnly && draft.Design.CiteAssessment == QuestionsByMove && unasked.Any())
                warnings.Add($"No discussion questions for move {string.Join(", ", unasked.Select(m => m.MoveNumber))}; add them before creating the MSEL, or CITE shows those moves empty.");

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
            var objectiveIds = new HashSet<string>(draft.Objectives.Select(o => o.Id), StringComparer.OrdinalIgnoreCase);
            var unknownObjectives = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var inject in draft.Injects)
            {
                inject.ExpectedAction = string.IsNullOrWhiteSpace(inject.ExpectedAction) ? null : inject.ExpectedAction.Trim();
                var objective = inject.Objective?.Trim();
                if (string.IsNullOrEmpty(objective))
                    inject.Objective = null;
                else if (objectiveIds.Contains(objective))
                    inject.Objective = objectiveIds.First(id => id.Equals(objective, StringComparison.OrdinalIgnoreCase));
                else
                {
                    unknownObjectives.Add(objective);
                    inject.Objective = null;
                }
            }
            if (unknownObjectives.Any())
                warnings.Add($"Took injects off objectives the draft does not have: {string.Join(", ", unknownObjectives)}.");
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
            foreach (var objective in draft.Objectives)
                objective.Competencies = KnownCompetencies(objective.Competencies);
            draft.Competencies = KnownCompetencies(draft.Competencies
                .Concat(draft.Injects.SelectMany(i => i.Competencies))
                .Concat(draft.Teams.SelectMany(t => t.Competencies))
                .Concat(draft.Objectives.SelectMany(o => o.Competencies))
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

        /// <summary>A move's briefing as HTML Blueprint and CITE show as written; plain text becomes a paragraph</summary>
        public static string SituationHtml(string situation)
        {
            if (string.IsNullOrWhiteSpace(situation))
                return null;
            situation = situation.Trim();
            return System.Text.RegularExpressions.Regex.IsMatch(situation, "<[a-zA-Z]")
                ? GalleryArticleHtml.Prepare(situation)
                : $"<p>{WebUtility.HtmlEncode(situation)}</p>";
        }

        private static readonly string[] _levels = ["Beginner", "Intermediate", "Advanced"];
        private static readonly string[] _uses = ["Assessment", "Instruction", "Professional Support"];
        private static readonly string[] _modes = ["Online", "Onsite", "Blended"];

        /// <summary>The catalog metadata, with the Info page's choices only, and the format among the keywords</summary>
        public static MselDraftMetadata CleanMetadata(MselDraftMetadata metadata, MselDraftDesign design)
        {
            metadata ??= new MselDraftMetadata();
            string Pick(string value, string[] allowed) => allowed.FirstOrDefault(a => a.Equals(value?.Trim(), StringComparison.OrdinalIgnoreCase));
            string Text(string value, int length) => string.IsNullOrWhiteSpace(value) ? null : value.Trim().Length > length ? value.Trim()[..length] : value.Trim();
            var keywords = (metadata.Keywords ?? "").Split(',').Select(k => k.Trim()).Where(k => k.Length > 0).ToList();
            var format = design?.Format switch { "Discussion" => "tabletop exercise", "Functional" => "functional exercise", "Hybrid" => "hybrid exercise", _ => null };
            if (format != null && !keywords.Contains(format, StringComparer.OrdinalIgnoreCase))
                keywords.Add(format);
            return new MselDraftMetadata
            {
                EducationalLevel = Pick(metadata.EducationalLevel, _levels),
                EducationalUse = Pick(metadata.EducationalUse, _uses),
                CourseMode = Pick(metadata.CourseMode, _modes),
                Subject = Text(metadata.Subject, 500),
                Keywords = Text(string.Join(", ", keywords.Distinct(StringComparer.OrdinalIgnoreCase)), 500),
                // a BCP 47 tag: letters, digits and hyphens
                Language = metadata.Language is string language && System.Text.RegularExpressions.Regex.IsMatch(language.Trim(), "^[A-Za-z]{2,3}(-[A-Za-z0-9]{2,8})*$")
                    ? language.Trim() : null
            };
        }

        /// <summary>
        /// The injects each move plans, within the cap: a plan over it is scaled down move by move, so the model writes what
        /// is kept rather than injects that are thrown away
        /// </summary>
        public static void PlanInjects(List<MselDraftMove> moves, int maxInjects)
        {
            foreach (var move in moves.Where(m => m.PlannedInjects is < 0))
                move.PlannedInjects = 0;
            var planned = moves.Sum(m => m.PlannedInjects ?? 0);
            if (planned <= maxInjects)
                return;
            var kept = 0;
            foreach (var move in moves.Where(m => m.PlannedInjects > 0))
            {
                move.PlannedInjects = Math.Max(1, (int)Math.Floor(move.PlannedInjects.Value * (double)maxInjects / planned));
                kept += move.PlannedInjects.Value;
            }
            // rounding down leaves a few; the busiest moves take them
            foreach (var move in moves.Where(m => m.PlannedInjects > 0).OrderByDescending(m => m.PlannedInjects))
            {
                if (kept >= maxInjects)
                    break;
                move.PlannedInjects++;
                kept++;
            }
        }

        private static List<string> Lines(List<string> lines) =>
            (lines ?? new List<string>()).Where(l => !string.IsNullOrWhiteSpace(l)).Select(l => l.Trim()).Distinct().ToList();

        /// <summary>
        /// Reads the model's draft. A part of it that cannot be read is left out and named in warnings, so one bad
        /// field does not cost the whole draft: the call that wrote it is the expensive part.
        /// </summary>
        public static MselDraft ReadDraft(JsonElement input, List<string> warnings)
        {
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var node = BedrockAiModelClient.ToNode(input);
            for (var attempt = 0; attempt < 20; attempt++)
            {
                try
                {
                    return node.Deserialize<MselDraft>(options) ?? new MselDraft();
                }
                // a list or object the model wrote as a string of JSON, as an answer without strict mode can, is read as JSON
                catch (JsonException ex) when (!string.IsNullOrEmpty(ex.Path) && ex.Path != "$" && UnpackAt(node, ex.Path))
                {
                }
                catch (JsonException ex) when (!string.IsNullOrEmpty(ex.Path) && ex.Path != "$" && RemoveAt(node, ex.Path))
                {
                    warnings.Add($"Left out the model's {ex.Path.TrimStart('$', '.')}, which it wrote in a form the draft cannot use. Check it before creating the MSEL.");
                }
            }
            throw new InvalidOperationException("The model's draft could not be read. Draft again; the answer differs each time.");
        }

        // replaces a string holding a JSON list or object, at a System.Text.Json path, with what it holds
        private static bool UnpackAt(JsonNode root, string path)
        {
            if (!Locate(root, path, out var parent, out var last))
                return false;
            var current = last is int i ? (parent as JsonArray)?.ElementAtOrDefault(i) : FindProperty(parent as JsonObject, (string)last);
            if (current is not JsonValue value || !value.TryGetValue<string>(out var text))
                return false;
            text = text.Trim();
            if (!(text.StartsWith('[') && text.EndsWith(']')) && !(text.StartsWith('{') && text.EndsWith('}')))
                return false;
            JsonNode parsed;
            try
            {
                parsed = JsonNode.Parse(text);
            }
            catch (JsonException)
            {
                return false;
            }
            if (last is int index && parent is JsonArray array)
                array[index] = parsed;
            else if (parent is JsonObject obj && FindKey(obj, (string)last) is string key)
                obj[key] = parsed;
            else
                return false;
            return true;
        }

        // the parent of the value at a System.Text.Json path such as $.moves[2].expectedDecisions, and the last step to it
        private static bool Locate(JsonNode root, string path, out JsonNode parent, out object last)
        {
            var steps = System.Text.RegularExpressions.Regex.Matches(path, @"\.([^.\[]+)|\['([^']+)'\]|\[(\d+)\]")
                .Select(m => m.Groups[3].Success ? (object)int.Parse(m.Groups[3].Value) : m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value)
                .ToList();
            parent = root;
            last = null;
            if (!steps.Any())
                return false;
            foreach (var step in steps.Take(steps.Count - 1))
            {
                parent = step is int i ? (parent as JsonArray)?.ElementAtOrDefault(i) : FindProperty(parent as JsonObject, (string)step);
                if (parent == null)
                    return false;
            }
            last = steps[^1];
            return true;
        }

        // removes the value at a System.Text.Json path such as $.moves[2].expectedDecisions or $.scenario
        private static bool RemoveAt(JsonNode root, string path)
        {
            if (!Locate(root, path, out var parent, out var last))
                return false;
            switch (last)
            {
                case int index when parent is JsonArray array && index < array.Count:
                    array.RemoveAt(index);
                    return true;
                case string name when parent is JsonObject obj && FindKey(obj, name) is string key:
                    obj.Remove(key);
                    return true;
                default:
                    return false;
            }
        }

        private static string FindKey(JsonObject obj, string name) =>
            obj?.Select(p => p.Key).FirstOrDefault(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase));

        private static JsonNode FindProperty(JsonObject obj, string name) =>
            FindKey(obj, name) is string key ? obj[key] : null;

        /// <summary>The design step's questions, numbered, without empty ones; a question without a suggestion is kept</summary>
        public static List<MselDesignQuestion> ReadQuestions(JsonElement input)
        {
            var questions = new List<MselDesignQuestion>();
            if (input.ValueKind != JsonValueKind.Object || !input.TryGetProperty("questions", out var list) || list.ValueKind != JsonValueKind.Array)
                return questions;
            string Text(JsonElement item, string name) =>
                item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()?.Trim() : null;
            foreach (var item in list.EnumerateArray().Take(5))
            {
                var question = item.ValueKind == JsonValueKind.String ? item.GetString()?.Trim() : Text(item, "question");
                if (string.IsNullOrEmpty(question))
                    continue;
                questions.Add(new MselDesignQuestion { Id = $"Q{questions.Count + 1}", Question = question, Why = Text(item, "why"), Suggested = Text(item, "suggested") });
            }
            return questions;
        }

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
