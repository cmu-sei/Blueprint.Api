// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Linq;
using System.Security.Claims;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Blueprint.Api.Data;
using Blueprint.Api.Data.Models;
using Blueprint.Api.Infrastructure.Authorization;
using Blueprint.Api.Infrastructure.Exceptions;
using Blueprint.Api.Infrastructure.Extensions;
using Blueprint.Api.Infrastructure.Mappings;
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
        Task LinkAsync(Guid mselId, Guid? citeScoringModelId, bool hasSystemPermission, CancellationToken ct);
        Task DeleteAsync(Guid mselId, bool hasSystemPermission, CancellationToken ct);
    }

    /// <summary>
    /// The CITE scoring model a MSEL authors itself, and the CITE model it links instead. A MSEL either links a CITE
    /// model (CiteScoringModelId) or authors one here, never both: storing one here clears the link.
    /// </summary>
    public class MselScoringModelService : IMselScoringModelService
    {
        private readonly BlueprintContext _context;
        private readonly ClaimsPrincipal _user;
        private readonly ICiteApiClient _citeApiClient;

        public MselScoringModelService(BlueprintContext context, IPrincipal user, ICiteApiClient citeApiClient)
        {
            _context = context;
            _user = user as ClaimsPrincipal;
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
            return model?.ToMselScoringModel();
        }

        public async Task<MselScoringModel> ReplaceAsync(Guid mselId, MselScoringModel scoringModel, bool hasSystemPermission, CancellationToken ct)
        {
            var msel = await GetEditableMselAsync(mselId, hasSystemPermission, ct);
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
            var citeModel = await CiteScoringModelAccess.GetViewableAsync(_citeApiClient, citeScoringModelId, ct);
            return await StoreAsync(msel, CiteScoringModelConverter.FromCite(citeModel), ct);
        }

        public async Task LinkAsync(Guid mselId, Guid? citeScoringModelId, bool hasSystemPermission, CancellationToken ct)
        {
            var msel = await GetEditableMselAsync(mselId, hasSystemPermission, ct);
            if (citeScoringModelId == null)
            {
                msel.CiteScoringModelId = null;
                msel.CiteScoringModelName = null;
            }
            else
            {
                // a MSEL that authors its own scoring model pushes that one, so it cannot link a CITE model as well
                if (await _context.MselScoringModels.AnyAsync(sm => sm.MselId == mselId, ct))
                    throw new ConflictException("The MSEL has its own scoring model. Remove it before linking a CITE scoring model.");
                var citeModel = await CiteScoringModelAccess.GetViewableAsync(_citeApiClient, (Guid)citeScoringModelId, ct);
                msel.CiteScoringModelId = citeModel.Id;
                msel.CiteScoringModelName = citeModel.Description;
            }
            msel.ModifiedBy = _user.GetId();
            await _context.SaveChangesAsync(ct);
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

        // EditMsels, the MSEL's owner or an editor of it, as for its other CITE content (its actions and duties),
        // under the integration settings' lock: what was pushed was built from it, and only a pull and a new push
        // rebuild it
        private async Task<MselEntity> GetEditableMselAsync(Guid mselId, bool hasSystemPermission, CancellationToken ct)
        {
            var msel = await _context.Msels.SingleOrDefaultAsync(m => m.Id == mselId, ct);
            if (msel == null)
                throw new EntityNotFoundException<MselEntity>();

            var userId = _user.GetId();
            if (!hasSystemPermission &&
                !await MselOwnerRequirement.IsMet(userId, mselId, _context) &&
                !await MselEditorRequirement.IsMet(userId, mselId, _context))
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
            Validate(model);
            var userId = _user.GetId();
            AssignIds(model, msel.Id, userId);

            // the model is keyed by its MSEL, so a replace keeps the row and swaps its contents
            var stored = await LoadAsync(msel.Id, ct);
            if (stored == null)
            {
                _context.MselScoringModels.Add(model);
            }
            else
            {
                model.CopyFieldsTo(stored);
                stored.RightSideDisplay = model.RightSideDisplay;
                stored.ModifiedBy = userId;
                _context.MselScoringCategories.RemoveRange(stored.Categories);
                _context.MselScoringCategories.AddRange(model.Categories);
            }
            msel.CiteScoringModelId = null;
            msel.CiteScoringModelName = null;
            msel.ModifiedBy = userId;
            await _context.SaveChangesAsync(ct);

            return (await LoadAsync(msel.Id, ct))?.ToMselScoringModel();
        }

        /// <summary>
        /// Refuses a model the push could not send to CITE as it is: an enum value CITE does not have, a category whose
        /// moves run backwards while the model shows its categories by move (otherwise CITE ignores them, and the editor
        /// hides them), or an embedded URL that is not http(s), which CITE would frame on every scoresheet. Every way in
        /// runs it: the editor's save, a preset, a copy from CITE and a MSEL file's import.
        /// </summary>
        internal static void Validate(MselScoringModelEntity model)
        {
            if (!Enum.IsDefined(model.RightSideDisplay))
                throw new BadRequestException($"{(int)model.RightSideDisplay} is not a right-side display.");
            if (!string.IsNullOrWhiteSpace(model.RightSideEmbeddedUrl) &&
                !(Uri.TryCreate(model.RightSideEmbeddedUrl.Trim(), UriKind.Absolute, out var url) && (url.Scheme == Uri.UriSchemeHttp || url.Scheme == Uri.UriSchemeHttps)))
                throw new BadRequestException("The embedded URL must be an absolute http or https URL.");
            foreach (var category in model.Categories)
            {
                if (!Enum.IsDefined(category.ScoringOptionSelection))
                    throw new BadRequestException($"The category \"{category.Description}\" has {(int)category.ScoringOptionSelection}, which is not a scoring option selection.");
                if (model.DisplayScoringModelByMoveNumber && category.MoveNumberFirstDisplay > category.MoveNumberLastDisplay)
                    throw new BadRequestException($"The category \"{category.Description}\" ends at move {category.MoveNumberLastDisplay}, before it starts at move {category.MoveNumberFirstDisplay}.");
            }
        }

        /// <summary>
        /// Makes <paramref name="model"/> the MSEL's: new ids for its categories and options, with their parents, and
        /// display orders numbered from 1 in the order they had. A store, a MSEL copy and an import each use it.
        /// </summary>
        internal static void AssignIds(MselScoringModelEntity model, Guid mselId, Guid userId)
        {
            model.MselId = mselId;
            model.CreatedBy = userId;
            var categories = model.Categories.OrderBy(c => c.DisplayOrder).ToList();
            foreach (var (category, i) in categories.Select((c, i) => (c, i)))
            {
                category.Id = Guid.NewGuid();
                category.MselScoringModelId = mselId;
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
            model.Categories = categories;
        }

        // the request's ids and display orders are not kept: the lists are numbered in the order they arrive
        private static MselScoringModelEntity ToEntity(MselScoringModel scoringModel)
        {
            var model = scoringModel.CopyFieldsTo(new MselScoringModelEntity { RightSideDisplay = scoringModel.RightSideDisplay });
            model.Description = model.Description?.Trim();
            model.RightSideEmbeddedUrl = string.IsNullOrWhiteSpace(model.RightSideEmbeddedUrl) ? null : model.RightSideEmbeddedUrl.Trim();
            foreach (var (c, i) in (scoringModel.Categories ?? []).Select((c, i) => (c, i)))
            {
                var category = c.CopyFieldsTo(new MselScoringCategoryEntity { ScoringOptionSelection = c.ScoringOptionSelection });
                category.DisplayOrder = i + 1;
                foreach (var (o, j) in (c.Options ?? []).Select((o, j) => (o, j)))
                {
                    var option = o.CopyFieldsTo(new MselScoringOptionEntity());
                    option.DisplayOrder = j + 1;
                    category.Options.Add(option);
                }
                model.Categories.Add(category);
            }
            return model;
        }
    }
}
