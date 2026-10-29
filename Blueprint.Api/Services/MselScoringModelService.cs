// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Linq;
using System.Net;
using System.Security.Claims;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using AutoMapper;
using Microsoft.EntityFrameworkCore;
using Blueprint.Api.Data;
using Blueprint.Api.Data.Models;
using Blueprint.Api.Infrastructure.Authorization;
using Blueprint.Api.Infrastructure.Exceptions;
using Blueprint.Api.Infrastructure.Extensions;
using Blueprint.Api.ViewModels;
using Cite.Api.Client;

namespace Blueprint.Api.Services
{
    public interface IMselScoringModelService
    {
        Task<MselScoringModel> GetAsync(Guid mselId, bool hasSystemPermission, bool hasCreateMselsPermission, CancellationToken ct);
        Task<MselScoringModel> ReplaceAsync(Guid mselId, MselScoringModel scoringModel, bool hasSystemPermission, CancellationToken ct);
        Task<MselScoringModel> CreateFromPresetAsync(Guid mselId, ScoringModelPreset preset, bool hasSystemPermission, CancellationToken ct);
        Task<MselScoringModel> CopyFromCiteAsync(Guid mselId, Guid citeScoringModelId, bool hasSystemPermission, CancellationToken ct);
        Task DeleteAsync(Guid mselId, bool hasSystemPermission, CancellationToken ct);
    }

    /// <summary>
    /// The CITE scoring model a MSEL authors itself. A MSEL either links a CITE model (CiteScoringModelId) or authors
    /// one here, never both: storing one here clears the link.
    /// </summary>
    public class MselScoringModelService : IMselScoringModelService
    {
        private readonly BlueprintContext _context;
        private readonly ClaimsPrincipal _user;
        private readonly IMapper _mapper;
        private readonly ICiteApiClient _citeApiClient;

        public MselScoringModelService(BlueprintContext context, IPrincipal user, IMapper mapper, ICiteApiClient citeApiClient)
        {
            _context = context;
            _user = user as ClaimsPrincipal;
            _mapper = mapper;
            _citeApiClient = citeApiClient;
        }

        public async Task<MselScoringModel> GetAsync(Guid mselId, bool hasSystemPermission, bool hasCreateMselsPermission, CancellationToken ct)
        {
            if (!await _context.Msels.AnyAsync(m => m.Id == mselId, ct))
                throw new EntityNotFoundException<MselEntity>();

            // the planners' material, as a contributors-only MSEL page is: being on one of the MSEL's teams
            // is not enough to read the equations the teams are scored by
            if (!hasSystemPermission && !(await MselContributorRequirement.IsMet(_user.GetId(), mselId, hasCreateMselsPermission, _context)))
                throw new ForbiddenException();

            var model = await LoadAsync(mselId, ct);
            return model == null ? null : _mapper.Map<MselScoringModel>(model);
        }

        public async Task<MselScoringModel> ReplaceAsync(Guid mselId, MselScoringModel scoringModel, bool hasSystemPermission, CancellationToken ct)
        {
            var msel = await GetEditableMselAsync(mselId, hasSystemPermission, ct);
            if (!string.IsNullOrWhiteSpace(scoringModel.RightSideEmbeddedUrl) &&
                !(Uri.TryCreate(scoringModel.RightSideEmbeddedUrl, UriKind.Absolute, out var url) && (url.Scheme == Uri.UriSchemeHttp || url.Scheme == Uri.UriSchemeHttps)))
                throw new BadRequestException("The embedded URL must be an absolute http or https URL.");
            var backwards = (scoringModel.Categories ?? []).FirstOrDefault(c => c.MoveNumberFirstDisplay > c.MoveNumberLastDisplay);
            if (backwards != null)
                throw new BadRequestException($"The category \"{backwards.Description}\" ends at move {backwards.MoveNumberLastDisplay}, before it starts at move {backwards.MoveNumberFirstDisplay}.");

            return await StoreAsync(msel, ToEntity(scoringModel), ct);
        }

        public async Task<MselScoringModel> CreateFromPresetAsync(Guid mselId, ScoringModelPreset preset, bool hasSystemPermission, CancellationToken ct)
        {
            var msel = await GetEditableMselAsync(mselId, hasSystemPermission, ct);
            MselScoringModelEntity model;
            if (preset == ScoringModelPreset.QuestionsByMove)
            {
                var moveNumbers = await _context.Moves.Where(m => m.MselId == mselId).Select(m => m.MoveNumber).ToListAsync(ct);
                if (!moveNumbers.Any())
                    throw new BadRequestException("The MSEL has no moves. Add its moves first: the questions are asked one move at a time.");
                // the planner writes the questions; the after-action review waits for them on the last move
                model = ScoringModelPresets.QuestionsByMove($"{msel.Name} Questions by Move",
                    moveNumbers.Select(n => (n, Enumerable.Empty<string>())), Enumerable.Empty<string>());
            }
            else
            {
                model = preset switch
                {
                    ScoringModelPreset.MissionImpact => ScoringModelPresets.MissionImpact(),
                    ScoringModelPreset.Likelihood => ScoringModelPresets.Likelihood(),
                    _ => ScoringModelPresets.Nciss()
                };
            }

            return await StoreAsync(msel, model, ct);
        }

        public async Task<MselScoringModel> CopyFromCiteAsync(Guid mselId, Guid citeScoringModelId, bool hasSystemPermission, CancellationToken ct)
        {
            var msel = await GetEditableMselAsync(mselId, hasSystemPermission, ct);
            ScoringModel citeModel;
            try
            {
                // with the caller's own token, so CITE decides whether they may read the model and its equations
                citeModel = await _citeApiClient.GetScoringModelAsync(citeScoringModelId, ct);
            }
            catch (ApiException ex) when (
                ex.StatusCode == (int)HttpStatusCode.Unauthorized ||
                ex.StatusCode == (int)HttpStatusCode.Forbidden ||
                ex.StatusCode == (int)HttpStatusCode.NotFound)
            {
                citeModel = null;
            }
            if (citeModel == null)
                throw new ForbiddenException($"CITE scoring model {citeScoringModelId} does not exist or you cannot view it in CITE.");

            return await StoreAsync(msel, CiteScoringModelConverter.FromCite(citeModel), ct);
        }

        public async Task DeleteAsync(Guid mselId, bool hasSystemPermission, CancellationToken ct)
        {
            var msel = await GetEditableMselAsync(mselId, hasSystemPermission, ct);
            var model = await _context.MselScoringModels.SingleOrDefaultAsync(sm => sm.MselId == mselId, ct);
            if (model == null)
                throw new EntityNotFoundException<MselScoringModelEntity>();

            // the categories and options go with it, by cascade
            _context.MselScoringModels.Remove(model);
            msel.ModifiedBy = _user.GetId();
            await _context.SaveChangesAsync(ct);
        }

        // the same people who can change the MSEL's scoring model link in MselService.UpdateAsync, under the
        // same lock: what was pushed was built from it, and only a pull and a new push rebuild it
        private async Task<MselEntity> GetEditableMselAsync(Guid mselId, bool hasSystemPermission, CancellationToken ct)
        {
            var msel = await _context.Msels.SingleOrDefaultAsync(m => m.Id == mselId, ct);
            if (msel == null)
                throw new EntityNotFoundException<MselEntity>();

            if (!hasSystemPermission && !(await MselOwnerRequirement.IsMet(_user.GetId(), mselId, _context)))
                throw new ForbiddenException();

            if (MselService.HasIntegrations(msel))
                throw new ConflictException("The scoring model cannot be changed while the MSEL is pushed, or is being pushed or pulled. Remove Integrations first.");

            return msel;
        }

        private async Task<MselScoringModelEntity> LoadAsync(Guid mselId, CancellationToken ct) =>
            await _context.MselScoringModels
                .Include(sm => sm.Categories)
                .ThenInclude(c => c.Options)
                .AsSplitQuery()
                .SingleOrDefaultAsync(sm => sm.MselId == mselId, ct);

        /// <summary>
        /// Stores <paramref name="model"/> as the MSEL's own, in place of any it had, numbering its categories and
        /// options in the order given. The MSEL stops linking a CITE model.
        /// </summary>
        private async Task<MselScoringModel> StoreAsync(MselEntity msel, MselScoringModelEntity model, CancellationToken ct)
        {
            var userId = _user.GetId();
            var categories = model.Categories.OrderBy(c => c.DisplayOrder).ToList();
            foreach (var (category, i) in categories.Select((c, i) => (c, i)))
            {
                category.Id = Guid.NewGuid();
                category.MselScoringModelId = msel.Id;
                category.DisplayOrder = i + 1;
                category.CreatedBy = userId;
                foreach (var (option, j) in category.Options.OrderBy(o => o.DisplayOrder).Select((o, j) => (o, j)))
                {
                    option.Id = Guid.NewGuid();
                    option.MselScoringCategoryId = category.Id;
                    option.DisplayOrder = j + 1;
                    option.CreatedBy = userId;
                }
            }

            // the model is keyed by its MSEL, so a replace keeps the row and swaps its contents
            var stored = await LoadAsync(msel.Id, ct);
            if (stored == null)
            {
                model.MselId = msel.Id;
                model.CreatedBy = userId;
                model.Categories = categories;
                _context.MselScoringModels.Add(model);
            }
            else
            {
                CopySettings(model, stored);
                stored.ModifiedBy = userId;
                _context.MselScoringCategories.RemoveRange(stored.Categories);
                _context.MselScoringCategories.AddRange(categories);
            }
            msel.CiteScoringModelId = null;
            msel.CiteScoringModelName = null;
            msel.ModifiedBy = userId;
            await _context.SaveChangesAsync(ct);

            return _mapper.Map<MselScoringModel>(await LoadAsync(msel.Id, ct));
        }

        private static void CopySettings(MselScoringModelEntity from, MselScoringModelEntity to)
        {
            to.Description = from.Description;
            to.CalculationEquation = from.CalculationEquation;
            to.HideScoresOnScoreSheet = from.HideScoresOnScoreSheet;
            to.DisplayCommentTextBoxes = from.DisplayCommentTextBoxes;
            to.DisplayScoringModelByMoveNumber = from.DisplayScoringModelByMoveNumber;
            to.ShowPastSituationDescriptions = from.ShowPastSituationDescriptions;
            to.UseSubmit = from.UseSubmit;
            to.UseUserScore = from.UseUserScore;
            to.UseTeamScore = from.UseTeamScore;
            to.UseTeamAverageScore = from.UseTeamAverageScore;
            to.UseTypeAverageScore = from.UseTypeAverageScore;
            to.UseOfficialScore = from.UseOfficialScore;
            to.RightSideDisplay = from.RightSideDisplay;
            to.RightSideHtmlBlock = from.RightSideHtmlBlock;
            to.RightSideEmbeddedUrl = from.RightSideEmbeddedUrl;
        }

        // the request's ids and display orders are not kept: StoreAsync numbers the lists as they arrive
        private static MselScoringModelEntity ToEntity(MselScoringModel scoringModel) => new()
        {
            Description = scoringModel.Description?.Trim(),
            CalculationEquation = scoringModel.CalculationEquation,
            HideScoresOnScoreSheet = scoringModel.HideScoresOnScoreSheet,
            DisplayCommentTextBoxes = scoringModel.DisplayCommentTextBoxes,
            DisplayScoringModelByMoveNumber = scoringModel.DisplayScoringModelByMoveNumber,
            ShowPastSituationDescriptions = scoringModel.ShowPastSituationDescriptions,
            UseSubmit = scoringModel.UseSubmit,
            UseUserScore = scoringModel.UseUserScore,
            UseTeamScore = scoringModel.UseTeamScore,
            UseTeamAverageScore = scoringModel.UseTeamAverageScore,
            UseTypeAverageScore = scoringModel.UseTypeAverageScore,
            UseOfficialScore = scoringModel.UseOfficialScore,
            RightSideDisplay = scoringModel.RightSideDisplay,
            RightSideHtmlBlock = scoringModel.RightSideHtmlBlock,
            RightSideEmbeddedUrl = string.IsNullOrWhiteSpace(scoringModel.RightSideEmbeddedUrl) ? null : scoringModel.RightSideEmbeddedUrl.Trim(),
            Categories = (scoringModel.Categories ?? []).Select((c, i) => new MselScoringCategoryEntity
            {
                DisplayOrder = i + 1,
                Description = c.Description,
                CalculationEquation = c.CalculationEquation,
                IsModifierRequired = c.IsModifierRequired,
                ScoringWeight = c.ScoringWeight,
                MoveNumberFirstDisplay = c.MoveNumberFirstDisplay,
                MoveNumberLastDisplay = c.MoveNumberLastDisplay,
                ScoringOptionSelection = c.ScoringOptionSelection,
                Options = (c.Options ?? []).Select((o, j) => new MselScoringOptionEntity
                {
                    DisplayOrder = j + 1,
                    Description = o.Description,
                    IsModifier = o.IsModifier,
                    Value = o.Value
                }).ToList()
            }).ToList()
        };
    }
}
