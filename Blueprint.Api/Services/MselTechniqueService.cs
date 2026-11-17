// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using AutoMapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Blueprint.Api.Data;
using Blueprint.Api.Data.Models;
using Blueprint.Api.Infrastructure.Authorization;
using Blueprint.Api.Infrastructure.Exceptions;
using Blueprint.Api.Infrastructure.Extensions;
using Blueprint.Api.ViewModels;

namespace Blueprint.Api.Services
{
    public interface IMselTechniqueService
    {
        Task<IEnumerable<MselTechnique>> GetByMselAsync(Guid mselId, bool hasSystemPermission, bool hasCreateMselsPermission, CancellationToken ct);
        Task<MselTechnique> GetAsync(Guid id, bool hasSystemPermission, bool hasCreateMselsPermission, CancellationToken ct);
        Task<MselTechnique> CreateAsync(MselTechnique mselTechnique, bool hasSystemPermission, CancellationToken ct);
        Task<bool> DeleteAsync(Guid id, bool hasSystemPermission, CancellationToken ct);
        Task<bool> DeleteByIdsAsync(Guid mselId, Guid techniqueId, bool hasSystemPermission, CancellationToken ct);
        Task<MselTechniqueUpgrade> UpgradeAsync(Guid mselId, Guid techniqueReleaseId, bool hasSystemPermission, CancellationToken ct);
    }

    /// <summary>
    /// A MSEL's technique pool - the techniques its Technique and DefensiveTechnique columns offer. The pool
    /// has two families, told apart by the release's IsDefensive: the attack techniques (ATT&amp;CK, ATLAS) and the
    /// defensive ones (D3FEND). Each family is pinned to one release: techniques from another release of the same
    /// family are refused until that family is upgraded to it, while the other family is left alone.
    /// </summary>
    /// <remarks>Reads follow the MSEL's view rule and writes its Owner role, as the competency pool does.</remarks>
    public class MselTechniqueService : IMselTechniqueService
    {
        private readonly BlueprintContext _context;
        private readonly ClaimsPrincipal _user;
        private readonly IMapper _mapper;
        private readonly ILogger<IMselTechniqueService> _logger;

        public MselTechniqueService(BlueprintContext context, IPrincipal user, ILogger<IMselTechniqueService> logger, IMapper mapper)
        {
            _context = context;
            _user = user as ClaimsPrincipal;
            _mapper = mapper;
            _logger = logger;
        }

        public async Task<IEnumerable<MselTechnique>> GetByMselAsync(Guid mselId, bool hasSystemPermission, bool hasCreateMselsPermission, CancellationToken ct)
        {
            var msel = await _context.Msels.SingleOrDefaultAsync(v => v.Id == mselId, ct);
            if (msel == null)
                throw new EntityNotFoundException<MselEntity>();

            if (!hasSystemPermission && !(await MselViewRequirement.IsMet(_user.GetId(), msel.Id, hasCreateMselsPermission, _context)))
                throw new ForbiddenException();

            var items = await _context.MselTechniques
                .Where(m => m.MselId == mselId)
                .Include(m => m.Technique)
                .ThenInclude(t => t.TechniqueRelease)
                .OrderBy(m => m.Technique.ExternalId)
                .ToListAsync(ct);

            return _mapper.Map<IEnumerable<MselTechnique>>(items);
        }

        public async Task<MselTechnique> GetAsync(Guid id, bool hasSystemPermission, bool hasCreateMselsPermission, CancellationToken ct)
        {
            var item = await _context.MselTechniques
                .Include(m => m.Technique)
                .ThenInclude(t => t.TechniqueRelease)
                .SingleOrDefaultAsync(m => m.Id == id, ct);
            if (item == null)
                throw new EntityNotFoundException<MselTechnique>();

            if (!hasSystemPermission && !(await MselViewRequirement.IsMet(_user.GetId(), item.MselId, hasCreateMselsPermission, _context)))
                throw new ForbiddenException();

            return _mapper.Map<MselTechnique>(item);
        }

        public async Task<MselTechnique> CreateAsync(MselTechnique mselTechnique, bool hasSystemPermission, CancellationToken ct)
        {
            var msel = await _context.Msels.SingleOrDefaultAsync(v => v.Id == mselTechnique.MselId, ct);
            if (msel == null)
                throw new EntityNotFoundException<MselEntity>();

            var technique = await _context.Techniques
                .Include(t => t.TechniqueRelease)
                .SingleOrDefaultAsync(t => t.Id == mselTechnique.TechniqueId, ct);
            if (technique == null)
                throw new EntityNotFoundException<TechniqueEntity>();

            if (!hasSystemPermission && !(await MselOwnerRequirement.IsMet(_user.GetId(), msel.Id, _context)))
                throw new ForbiddenException();

            if (await _context.MselTechniques.AnyAsync(m => m.MselId == msel.Id && m.TechniqueId == technique.Id, ct))
                throw new ConflictException($"{technique.ExternalId} is already in this MSEL's techniques.");

            var isDefensive = IsDefensive(technique.TechniqueRelease);
            var pinned = await InFamily(_context.MselTechniques.Where(m => m.MselId == msel.Id), isDefensive)
                .Where(m => m.Technique.TechniqueReleaseId != technique.TechniqueReleaseId)
                .Select(m => m.Technique.TechniqueRelease)
                .FirstOrDefaultAsync(ct);
            if (pinned != null)
                throw new ConflictException(
                    $"This MSEL's {FamilyName(isDefensive)} techniques are from {pinned.Domain} {pinned.Version}. " +
                    $"Upgrade them to {technique.TechniqueRelease.Version} before adding techniques from it.");

            var entity = new MselTechniqueEntity(msel.Id, technique.Id)
            {
                Id = mselTechnique.Id != Guid.Empty ? mselTechnique.Id : Guid.NewGuid()
            };
            _context.MselTechniques.Add(entity);
            await _context.SaveChangesAsync(ct);
            _logger.LogWarning("Technique {ExternalId} added to MSEL {MselId} by {UserId}", technique.ExternalId, msel.Id, _user.GetId());
            return await GetAsync(entity.Id, true, false, ct);
        }

        public async Task<bool> DeleteAsync(Guid id, bool hasSystemPermission, CancellationToken ct)
        {
            var item = await _context.MselTechniques.SingleOrDefaultAsync(v => v.Id == id, ct);
            if (item == null)
                throw new EntityNotFoundException<MselTechnique>();

            return await RemoveAsync(item, hasSystemPermission, ct);
        }

        public async Task<bool> DeleteByIdsAsync(Guid mselId, Guid techniqueId, bool hasSystemPermission, CancellationToken ct)
        {
            var item = await _context.MselTechniques
                .SingleOrDefaultAsync(v => v.MselId == mselId && v.TechniqueId == techniqueId, ct);
            if (item == null)
                throw new EntityNotFoundException<MselTechnique>();

            return await RemoveAsync(item, hasSystemPermission, ct);
        }

        /// <summary>
        /// Moves one family of the MSEL's pool to another release of that family, matching techniques by their
        /// external id. A technique the new release no longer has is dropped and reported; cells that name it keep
        /// their text. The other family is not touched.
        /// </summary>
        public async Task<MselTechniqueUpgrade> UpgradeAsync(Guid mselId, Guid techniqueReleaseId, bool hasSystemPermission, CancellationToken ct)
        {
            var msel = await _context.Msels.SingleOrDefaultAsync(v => v.Id == mselId, ct);
            if (msel == null)
                throw new EntityNotFoundException<MselEntity>();

            var release = await _context.TechniqueReleases.SingleOrDefaultAsync(r => r.Id == techniqueReleaseId, ct);
            if (release == null)
                throw new EntityNotFoundException<TechniqueRelease>();

            if (!hasSystemPermission && !(await MselOwnerRequirement.IsMet(_user.GetId(), msel.Id, _context)))
                throw new ForbiddenException();

            var current = await InFamily(_context.MselTechniques.Where(m => m.MselId == mselId), IsDefensive(release))
                .Include(m => m.Technique)
                .ToListAsync(ct);
            var result = new MselTechniqueUpgrade { MselId = mselId, TechniqueReleaseId = techniqueReleaseId };

            var moving = current.Where(m => m.Technique.TechniqueReleaseId != techniqueReleaseId).ToList();
            result.Kept = current.Count - moving.Count;
            if (moving.Count == 0)
                return result;

            var externalIds = moving.Select(m => m.Technique.ExternalId).ToList();
            var replacements = await _context.Techniques
                .Where(t => t.TechniqueReleaseId == techniqueReleaseId && externalIds.Contains(t.ExternalId))
                .ToDictionaryAsync(t => t.ExternalId, t => t.Id, StringComparer.OrdinalIgnoreCase, ct);
            var alreadyThere = current
                .Where(m => m.Technique.TechniqueReleaseId == techniqueReleaseId)
                .Select(m => m.TechniqueId)
                .ToHashSet();

            // One SaveChanges, so the removes and adds land together.
            _context.MselTechniques.RemoveRange(moving);
            foreach (var item in moving.OrderBy(m => m.Technique.ExternalId))
            {
                if (!replacements.TryGetValue(item.Technique.ExternalId, out var replacementId))
                {
                    result.Dropped.Add(item.Technique.ExternalId);
                    continue;
                }

                result.Kept++;
                if (alreadyThere.Add(replacementId))
                    _context.MselTechniques.Add(new MselTechniqueEntity(mselId, replacementId) { Id = Guid.NewGuid() });
            }
            await _context.SaveChangesAsync(ct);

            _logger.LogWarning("MSEL {MselId} {Family} techniques upgraded to release {ReleaseId} by {UserId}: {Kept} kept, {Dropped} dropped",
                mselId, FamilyName(IsDefensive(release)), techniqueReleaseId, _user.GetId(), result.Kept, result.Dropped.Count);
            return result;
        }

        private async Task<bool> RemoveAsync(MselTechniqueEntity item, bool hasSystemPermission, CancellationToken ct)
        {
            if (!hasSystemPermission && !(await MselOwnerRequirement.IsMet(_user.GetId(), item.MselId, _context)))
                throw new ForbiddenException();

            _context.MselTechniques.Remove(item);
            await _context.SaveChangesAsync(ct);
            _logger.LogWarning("Technique {TechniqueId} removed from MSEL {MselId} by {UserId}", item.TechniqueId, item.MselId, _user.GetId());
            return true;
        }

        private static bool IsDefensive(TechniqueReleaseEntity release) => release.IsDefensive;

        private static string FamilyName(bool isDefensive) => isDefensive ? "defensive" : "attack";

        /// <summary>The pool rows of one family, by the release's flag: defensive (D3FEND) or attack (everything else).</summary>
        private static IQueryable<MselTechniqueEntity> InFamily(IQueryable<MselTechniqueEntity> pool, bool isDefensive) =>
            pool.Where(m => m.Technique.TechniqueRelease.IsDefensive == isDefensive);
    }
}
