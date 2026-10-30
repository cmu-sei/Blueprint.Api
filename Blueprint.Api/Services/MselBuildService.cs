// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Security.Principal;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Blueprint.Api.Data;
using Blueprint.Api.Data.Models;
using Blueprint.Api.Infrastructure.Authorization;
using Blueprint.Api.Infrastructure.Exceptions;
using Blueprint.Api.Infrastructure.Extensions;
using Blueprint.Api.Infrastructure.Options;
using Blueprint.Api.ViewModels;
using Cite.Api.Client;
using Microsoft.EntityFrameworkCore;

namespace Blueprint.Api.Services
{
    /// <summary>
    /// What the stages of building a MSEL from its AI draft keep between requests: the draft so far, what it was written
    /// from, and what has been written. Kept as JSON on the MSEL, so a build can stop and resume.
    /// </summary>
    /// <summary>The stages a MSEL built from its AI draft goes through; none once it is built</summary>
    public static class MselBuildStage
    {
        public const string Injects = "Injects";
        public const string Articles = "Articles";
        public const string Finish = "Finish";
    }

    public class MselBuildState
    {
        public Guid TemplateMselId { get; set; }
        public string Prompt { get; set; }
        public List<Guid> CompetencyFrameworkIds { get; set; } = new List<Guid>();
        public List<MselDesignAnswer> Answers { get; set; } = new List<MselDesignAnswer>();
        public MselDraft Draft { get; set; }
        public Guid? ReadInPageId { get; set; }
        /// <summary>Each Gallery article's scenario event, and its inject's position in the draft</summary>
        public Dictionary<Guid, int> Articles { get; set; } = new Dictionary<Guid, int>();
        public List<Guid> ArticlesWritten { get; set; } = new List<Guid>();
        /// <summary>The other rows whose long text is written one at a time, and each one's position in the draft</summary>
        public Dictionary<Guid, int> Rows { get; set; } = new Dictionary<Guid, int>();
        public List<Guid> RowsWritten { get; set; } = new List<Guid>();
        /// <summary>The drafted teams and organizations, whose profiles are written</summary>
        public List<Guid> Profiles { get; set; } = new List<Guid>();
        public List<Guid> ProfilesWritten { get; set; } = new List<Guid>();

        private static readonly JsonSerializerOptions _options = new() { PropertyNameCaseInsensitive = true };

        public static string Serialize(MselBuildState state) => JsonSerializer.Serialize(state, _options);

        public static MselBuildState Deserialize(string json) =>
            string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<MselBuildState>(json, _options);
    }

    public interface IMselBuildService
    {
        Task<MselBuildStatus> GetStatusAsync(Guid mselId, bool hasSystemPermission, CancellationToken ct);
        Task<MselBuildStatus> WriteInjectsAsync(Guid mselId, bool hasSystemPermission, CancellationToken ct);
        Task<MselBuildContent> WriteRowAsync(Guid mselId, Guid scenarioEventId, bool hasSystemPermission, CancellationToken ct);
        Task<ArticleDraftResult> WriteArticleAsync(Guid mselId, Guid scenarioEventId, bool hasSystemPermission, CancellationToken ct);
        Task SaveArticleAsync(Guid mselId, Guid scenarioEventId, string html, bool hasSystemPermission, CancellationToken ct);
        Task<ProfileDraftResult> WriteProfileAsync(Guid mselId, Guid ownerId, bool hasSystemPermission, CancellationToken ct);
        Task SaveProfileAsync(Guid mselId, Guid ownerId, string html, bool hasSystemPermission, CancellationToken ct);
        Task<MselBuildStatus> FinishAsync(Guid mselId, bool hasSystemPermission, CancellationToken ct);
    }

    /// <summary>
    /// Builds a MSEL from its approved AI design, stage by stage: the injects, then each article and profile, then what
    /// needs the whole MSEL. Every stage saves what it wrote, so nothing is lost if the planner leaves; the MSEL says
    /// which stage is left until it is built.
    /// </summary>
    public class MselBuildService : IMselBuildService
    {
        private readonly BlueprintContext _context;
        private readonly IMselDraftService _mselDraftService;
        private readonly IArticleDraftService _articleDraftService;
        private readonly IProfileDraftService _profileDraftService;
        private readonly IAiUsageService _aiUsageService;
        private readonly ICiteApiClient _citeApiClient;
        private readonly AiOptions _aiOptions;
        private readonly ClaimsPrincipal _user;
        // what the stage loaded; anything else it tracks at save was written by the stage
        private readonly HashSet<object> _loaded = new HashSet<object>(ReferenceEqualityComparer.Instance);

        public MselBuildService(BlueprintContext context, IMselDraftService mselDraftService, IArticleDraftService articleDraftService,
            IProfileDraftService profileDraftService, IAiUsageService aiUsageService, ICiteApiClient citeApiClient, IPrincipal user,
            AiOptions aiOptions)
        {
            _context = context;
            _mselDraftService = mselDraftService;
            _articleDraftService = articleDraftService;
            _profileDraftService = profileDraftService;
            _aiUsageService = aiUsageService;
            _citeApiClient = citeApiClient;
            _aiOptions = aiOptions;
            _user = user as ClaimsPrincipal;
        }

        public async Task<MselBuildStatus> GetStatusAsync(Guid mselId, bool hasSystemPermission, CancellationToken ct)
        {
            var msel = await _context.Msels.AsNoTracking().SingleOrDefaultAsync(m => m.Id == mselId, ct)
                ?? throw new EntityNotFoundException<MselEntity>("MSEL not found with ID=" + mselId);
            await AuthorizeAsync(mselId, hasSystemPermission, ct);
            return await StatusAsync(msel, MselBuildState.Deserialize(msel.AiDraft), ct);
        }

        public async Task<MselBuildStatus> WriteInjectsAsync(Guid mselId, bool hasSystemPermission, CancellationToken ct)
        {
            var (msel, state) = await LoadAsync(mselId, hasSystemPermission, MselBuildStage.Injects, ct);
            var warnings = new List<string>();
            var competencies = await MselDraftService.LoadCompetencyChoicesAsync(_context, state.TemplateMselId, state.CompetencyFrameworkIds, ct);
            // the MSEL now holds the design's teams, organizations, cards and columns, so the injects are checked against it
            var draft = await _mselDraftService.WriteInjectsAsync(msel, state.Draft, state.Prompt, state.Answers, competencies, warnings, ct);

            var created = new Dictionary<MselDraftInject, ScenarioEventEntity>();
            var readIn = MselDraftBuilder.AddInjects(msel, draft, competencies, created);
            state.Draft = draft;
            state.ReadInPageId = readIn?.Id;
            state.Articles = draft.Injects
                .Select((inject, index) => (inject, index))
                .Where(x => created.ContainsKey(x.inject) && ArticleFields.IsArticle(msel, x.inject.Values))
                .ToDictionary(x => created[x.inject].Id, x => x.index);
            // a row that is not an article still has its long columns to write, one row at a time
            var longFields = MselDraftService.LongFields(msel.DataFields);
            state.Rows = longFields.Any()
                ? draft.Injects
                    .Select((inject, index) => (inject, index))
                    .Where(x => created.ContainsKey(x.inject) && !state.Articles.ContainsKey(created[x.inject].Id))
                    .ToDictionary(x => created[x.inject].Id, x => x.index)
                : new Dictionary<Guid, int>();
            var drafted = new HashSet<string>(draft.Teams.Select(t => t.ShortName).Concat(draft.Organizations.Select(o => o.ShortName)), StringComparer.OrdinalIgnoreCase);
            state.Profiles = msel.Teams.Where(t => drafted.Contains(t.ShortName)).Select(t => t.Id)
                .Concat(msel.Organizations.Where(o => drafted.Contains(o.ShortName)).Select(o => o.Id))
                .ToList();
            msel.AiDraftStage = MselBuildStage.Articles;
            await SaveAsync(msel, state, ct);

            var status = await StatusAsync(msel, state, ct);
            status.Warnings.AddRange(warnings);
            return status;
        }

        public async Task<MselBuildContent> WriteRowAsync(Guid mselId, Guid scenarioEventId, bool hasSystemPermission, CancellationToken ct)
        {
            var (msel, state) = await LoadAsync(mselId, hasSystemPermission, MselBuildStage.Articles, ct);
            if (!state.Rows.TryGetValue(scenarioEventId, out var index))
                throw new BadRequestException("That row is not one of the drafted injects.");
            string first = null;
            foreach (var field in MselDraftService.LongFields(msel.DataFields))
            {
                var html = await _mselDraftService.WriteInjectTextAsync(msel, state.Draft, index, field, ct);
                SetArticle(msel, scenarioEventId, field.Name, html);
                first ??= html;
            }
            await SaveItemAsync(msel, state, ct);
            return new MselBuildContent { Html = first };
        }

        public async Task<ArticleDraftResult> WriteArticleAsync(Guid mselId, Guid scenarioEventId, bool hasSystemPermission, CancellationToken ct)
        {
            var (msel, state) = await LoadAsync(mselId, hasSystemPermission, MselBuildStage.Articles, ct);
            if (!state.Articles.TryGetValue(scenarioEventId, out var index))
                throw new BadRequestException("That row is not one of the drafted Gallery articles.");
            var result = await _articleDraftService.DraftAsync(new ArticleDraftRequest { TemplateMselId = state.TemplateMselId, Draft = state.Draft, InjectIndex = index }, ct);
            SetArticle(msel, scenarioEventId, result.FieldName, result.Html);
            await SaveItemAsync(msel, state, ct);
            return result;
        }

        public async Task SaveArticleAsync(Guid mselId, Guid scenarioEventId, string html, bool hasSystemPermission, CancellationToken ct)
        {
            var (msel, state) = await LoadAsync(mselId, hasSystemPermission, null, ct);
            if (!state.Articles.ContainsKey(scenarioEventId))
                throw new BadRequestException("That row is not one of the drafted Gallery articles.");
            var body = ArticleFields.Body(msel) ?? throw new BadRequestException("The MSEL has no column for a Gallery article's body.");
            SetArticle(msel, scenarioEventId, body.Name, GalleryArticleHtml.Prepare(html, null, GalleryArticleHtml.ArticleWidth));
            await SaveItemAsync(msel, state, ct);
        }

        public async Task<ProfileDraftResult> WriteProfileAsync(Guid mselId, Guid ownerId, bool hasSystemPermission, CancellationToken ct)
        {
            var (msel, state) = await LoadAsync(mselId, hasSystemPermission, MselBuildStage.Articles, ct);
            if (!state.Profiles.Contains(ownerId))
                throw new BadRequestException("That is not one of the drafted teams or organizations.");
            var team = msel.Teams.FirstOrDefault(t => t.Id == ownerId);
            var organization = msel.Organizations.FirstOrDefault(o => o.Id == ownerId);
            var result = await _profileDraftService.DraftAsync(new ProfileDraftRequest
            {
                MselId = mselId,
                Kind = team != null ? "team" : "organization",
                ShortName = team?.ShortName ?? organization?.ShortName,
                Name = team?.Name ?? organization?.Name,
                Summary = organization?.Summary,
                Description = team?.Description ?? organization?.Description
            }, new ProfileDraftPermissions(EditMsels: hasSystemPermission, CreateMsels: false), ct);
            SetProfile(team, organization, result.Html);
            await SaveItemAsync(msel, state, ct);
            return result;
        }

        public async Task SaveProfileAsync(Guid mselId, Guid ownerId, string html, bool hasSystemPermission, CancellationToken ct)
        {
            var (msel, state) = await LoadAsync(mselId, hasSystemPermission, null, ct);
            if (!state.Profiles.Contains(ownerId))
                throw new BadRequestException("That is not one of the drafted teams or organizations.");
            SetProfile(msel.Teams.FirstOrDefault(t => t.Id == ownerId), msel.Organizations.FirstOrDefault(o => o.Id == ownerId),
                GalleryArticleHtml.Prepare(html, null, GalleryArticleHtml.ProfileWidth));
            await SaveItemAsync(msel, state, ct);
        }

        public async Task<MselBuildStatus> FinishAsync(Guid mselId, bool hasSystemPermission, CancellationToken ct)
        {
            var (msel, state) = await LoadAsync(mselId, hasSystemPermission, null, ct);
            if (msel.AiDraftStage == MselBuildStage.Injects)
                throw new ConflictException("The MSEL's injects are not written yet.");
            var changes = new List<string>();
            var readIn = state.ReadInPageId is Guid pageId ? msel.Pages.FirstOrDefault(p => p.Id == pageId) : null;
            MselDraftBuilder.Finish(msel, state.Draft, readIn);
            // CITE team types and the scoring model the planner chose, made in CITE now so the push finds them
            await MselDraftCite.ApplyAsync(msel, state.Draft, _citeApiClient, (_, message) => changes.Add(message), ct);
            msel.AiDraftStage = null;
            await SaveAsync(msel, state, ct);

            var status = await StatusAsync(msel, state, ct);
            status.Warnings.AddRange(changes);
            return status;
        }

        private static void SetArticle(MselEntity msel, Guid scenarioEventId, string fieldName, string html)
        {
            var field = msel.DataFields.FirstOrDefault(f => f.Name.Equals(fieldName, StringComparison.OrdinalIgnoreCase))
                ?? throw new BadRequestException($"The MSEL has no column named {fieldName}.");
            var scenarioEvent = msel.ScenarioEvents.FirstOrDefault(e => e.Id == scenarioEventId)
                ?? throw new EntityNotFoundException<ScenarioEventEntity>("Scenario event not found with ID=" + scenarioEventId);
            var value = scenarioEvent.DataValues.FirstOrDefault(v => v.DataFieldId == field.Id);
            if (value == null)
                scenarioEvent.DataValues.Add(new DataValueEntity { Id = Guid.NewGuid(), DataFieldId = field.Id, Value = html });
            else
                value.Value = html;
        }

        private static void SetProfile(TeamEntity team, OrganizationEntity organization, string html)
        {
            if (team != null)
                team.Description = html;
            else if (organization != null)
            {
                // a written profile is a page; the one line the draft wrote stays the summary
                if (string.IsNullOrWhiteSpace(organization.Summary))
                    organization.Summary = DataValueDraftService.StripHtml(organization.Description);
                organization.Description = html;
            }
        }

        private async Task<(MselEntity, MselBuildState)> LoadAsync(Guid mselId, bool hasSystemPermission, string stage, CancellationToken ct)
        {
            var msel = await _context.Msels
                .Include(m => m.DataFields).ThenInclude(f => f.DataOptions)
                .Include(m => m.ScenarioEvents).ThenInclude(e => e.DataValues)
                .Include(m => m.Moves)
                .Include(m => m.Teams).ThenInclude(t => t.TeamCompetencies)
                .Include(m => m.Organizations)
                .Include(m => m.Cards).ThenInclude(c => c.CardTeams)
                .Include(m => m.CiteActions)
                .Include(m => m.Pages)
                .Include(m => m.PlayerApplications).ThenInclude(a => a.PlayerApplicationTeams)
                .Include(m => m.MselCompetencies)
                .AsSplitQuery()
                .SingleOrDefaultAsync(m => m.Id == mselId, ct)
                ?? throw new EntityNotFoundException<MselEntity>("MSEL not found with ID=" + mselId);
            await AuthorizeAsync(mselId, hasSystemPermission, ct);
            foreach (var entry in _context.ChangeTracker.Entries())
                _loaded.Add(entry.Entity);
            var state = MselBuildState.Deserialize(msel.AiDraft)
                ?? throw new ConflictException("The MSEL was not drafted with AI.");
            if (msel.AiDraftStage == null)
                throw new ConflictException("The MSEL is already built.");
            if (stage != null && msel.AiDraftStage != stage)
                throw new ConflictException($"The MSEL is at the {msel.AiDraftStage} stage of its build, not {stage}.");
            return (msel, state);
        }

        // the drafting user, or anyone who may edit every MSEL, builds it
        private async Task AuthorizeAsync(Guid mselId, bool hasSystemPermission, CancellationToken ct)
        {
            if (!hasSystemPermission && !await MselOwnerRequirement.IsMet(_user.GetId(), mselId, _context))
                throw new ForbiddenException();
        }

        // one row, article or profile, written beside others at once: its own rows are saved, and the build's shared state is left
        // alone, since each request holds its own copy of it; what is written is read from the rows themselves
        private Task SaveItemAsync(MselEntity msel, MselBuildState state, CancellationToken ct) => SaveAsync(msel, state, ct, withState: false);

        private async Task SaveAsync(MselEntity msel, MselBuildState state, CancellationToken ct, bool withState = true)
        {
            if (withState)
                msel.AiDraft = MselBuildState.Serialize(state);
            // rows the stage wrote come with their ids, which EF takes for rows already saved
            _context.ChangeTracker.DetectChanges();
            foreach (var entry in _context.ChangeTracker.Entries().Where(e => e.State == EntityState.Modified && !_loaded.Contains(e.Entity)).ToList())
                entry.State = EntityState.Added;
            var userId = _user.GetId();
            foreach (var entry in _context.ChangeTracker.Entries<BaseEntity>().Where(e => e.State == EntityState.Added && e.Entity.CreatedBy == Guid.Empty))
                entry.Entity.CreatedBy = userId;
            await _context.SaveChangesAsync(ct);
            // what each stage cost counts toward the MSEL, as the design's calls did
            if (state.Draft?.DraftId is Guid draftId && draftId != Guid.Empty)
                await _aiUsageService.LinkDraftAsync(draftId, msel.Id, ct);
        }

        private async Task<MselBuildStatus> StatusAsync(MselEntity msel, MselBuildState state, CancellationToken ct)
        {
            var status = new MselBuildStatus { MselId = msel.Id, Stage = msel.AiDraftStage, Parallel = Math.Max(1, _aiOptions.Parallel) };
            if (state == null || msel.AiDraftStage == null)
                return status;
            // written means its long text is saved: an article's body, a row's long columns, a profile in HTML
            var fields = await _context.DataFields.AsNoTracking().Where(f => f.MselId == msel.Id).ToListAsync(ct);
            var body = msel.UseGallery || fields.Any(f => f.GalleryArticleParameter == nameof(Data.Enumerations.GalleryArticleParameter.Description))
                ? fields.FirstOrDefault(f => f.GalleryArticleParameter == nameof(Data.Enumerations.GalleryArticleParameter.Description)) : null;
            var longFields = MselDraftService.LongFields(fields).Select(f => f.Id).ToList();
            var ids = state.Articles.Keys.Concat(state.Rows.Keys).ToList();
            var events = await _context.ScenarioEvents.AsNoTracking()
                .Where(e => ids.Contains(e.Id))
                .Select(e => new { e.Id, Values = e.DataValues.Select(v => new { v.DataFieldId, v.DataField.Name, v.DataField.GalleryArticleParameter, v.Value }) })
                .ToListAsync(ct);
            bool Has(Guid eventId, Guid fieldId) => events.FirstOrDefault(e => e.Id == eventId)?.Values
                .Any(v => v.DataFieldId == fieldId && !string.IsNullOrWhiteSpace(DataValueDraftService.StripHtml(v.Value))) == true;
            bool IsHtml(Guid eventId, Guid fieldId) => events.FirstOrDefault(e => e.Id == eventId)?.Values
                .Any(v => v.DataFieldId == fieldId && v.Value != null && v.Value.Contains('<')) == true;
            string Title(Guid eventId) => events.FirstOrDefault(e => e.Id == eventId)?.Values
                .FirstOrDefault(v => v.GalleryArticleParameter == "Name" || new[] { "title", "message", "name" }.Contains(v.Name.ToLower()))?.Value;

            status.Articles = state.Articles.OrderBy(a => a.Value).Select(a => a.Key)
                // a body column of plain text holds the outline's draft until the article, which is HTML, is written
                .Where(id => body == null || !IsHtml(id, body.Id))
                .Select(id => new MselBuildItem { Id = id, Kind = "article", Name = Title(id) })
                .ToList();
            status.Injects = state.Rows.OrderBy(r => r.Value).Select(r => r.Key)
                .Where(id => longFields.Any(f => !Has(id, f)))
                .Select(id => new MselBuildItem { Id = id, Kind = "inject", Name = Title(id) })
                .ToList();
            var owners = await _context.Teams.AsNoTracking().Where(t => state.Profiles.Contains(t.Id)).Select(t => new { t.Id, t.ShortName, t.Description, Kind = "team" })
                .Concat(_context.Organizations.AsNoTracking().Where(o => state.Profiles.Contains(o.Id)).Select(o => new { o.Id, o.ShortName, o.Description, Kind = "organization" }))
                .ToListAsync(ct);
            // the design's line is plain text; a written profile is an HTML page
            status.Profiles = owners.Where(o => o.Description == null || !o.Description.Contains('<'))
                .Select(o => new MselBuildItem { Id = o.Id, Kind = o.Kind, Name = o.ShortName })
                .ToList();
            return status;
        }
    }
}
