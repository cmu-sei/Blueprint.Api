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
    public interface IMselAttackTechniqueService
    {
        Task<IEnumerable<MselAttackTechnique>> GetByMselAsync(Guid mselId, bool hasSystemPermission, bool hasCreateMselsPermission, CancellationToken ct);
        Task<MselAttackTechnique> GetAsync(Guid id, bool hasSystemPermission, bool hasCreateMselsPermission, CancellationToken ct);
        Task<MselAttackTechnique> CreateAsync(MselAttackTechnique mselAttackTechnique, bool hasSystemPermission, CancellationToken ct);
        Task<bool> DeleteAsync(Guid id, bool hasSystemPermission, CancellationToken ct);
        Task<bool> DeleteByIdsAsync(Guid mselId, Guid attackTechniqueId, bool hasSystemPermission, CancellationToken ct);
        Task<MselAttackTechniqueUpgrade> UpgradeAsync(Guid mselId, Guid attackReleaseId, bool hasSystemPermission, CancellationToken ct);
    }

    /// <summary>
    /// A MSEL's ATT&amp;CK technique pool - the techniques its AttackTechnique columns offer. The pool is pinned to
    /// one release: techniques from another release are refused until the pool is upgraded to it.
    /// </summary>
    /// <remarks>Reads follow the MSEL's view rule and writes its Owner role, as the competency pool does.</remarks>
    public class MselAttackTechniqueService : IMselAttackTechniqueService
    {
        private readonly BlueprintContext _context;
        private readonly ClaimsPrincipal _user;
        private readonly IMapper _mapper;
        private readonly ILogger<IMselAttackTechniqueService> _logger;

        public MselAttackTechniqueService(BlueprintContext context, IPrincipal user, ILogger<IMselAttackTechniqueService> logger, IMapper mapper)
        {
            _context = context;
            _user = user as ClaimsPrincipal;
            _mapper = mapper;
            _logger = logger;
        }

        public async Task<IEnumerable<MselAttackTechnique>> GetByMselAsync(Guid mselId, bool hasSystemPermission, bool hasCreateMselsPermission, CancellationToken ct)
        {
            var msel = await _context.Msels.SingleOrDefaultAsync(v => v.Id == mselId, ct);
            if (msel == null)
                throw new EntityNotFoundException<MselEntity>();

            if (!hasSystemPermission && !(await MselViewRequirement.IsMet(_user.GetId(), msel.Id, hasCreateMselsPermission, _context)))
                throw new ForbiddenException();

            var items = await _context.MselAttackTechniques
                .Where(m => m.MselId == mselId)
                .Include(m => m.AttackTechnique)
                .OrderBy(m => m.AttackTechnique.ExternalId)
                .ToListAsync(ct);

            return _mapper.Map<IEnumerable<MselAttackTechnique>>(items);
        }

        public async Task<MselAttackTechnique> GetAsync(Guid id, bool hasSystemPermission, bool hasCreateMselsPermission, CancellationToken ct)
        {
            var item = await _context.MselAttackTechniques
                .Include(m => m.AttackTechnique)
                .SingleOrDefaultAsync(m => m.Id == id, ct);
            if (item == null)
                throw new EntityNotFoundException<MselAttackTechnique>();

            if (!hasSystemPermission && !(await MselViewRequirement.IsMet(_user.GetId(), item.MselId, hasCreateMselsPermission, _context)))
                throw new ForbiddenException();

            return _mapper.Map<MselAttackTechnique>(item);
        }

        public async Task<MselAttackTechnique> CreateAsync(MselAttackTechnique mselAttackTechnique, bool hasSystemPermission, CancellationToken ct)
        {
            var msel = await _context.Msels.SingleOrDefaultAsync(v => v.Id == mselAttackTechnique.MselId, ct);
            if (msel == null)
                throw new EntityNotFoundException<MselEntity>();

            var technique = await _context.AttackTechniques
                .Include(t => t.AttackRelease)
                .SingleOrDefaultAsync(t => t.Id == mselAttackTechnique.AttackTechniqueId, ct);
            if (technique == null)
                throw new EntityNotFoundException<AttackTechniqueEntity>();

            if (!hasSystemPermission && !(await MselOwnerRequirement.IsMet(_user.GetId(), msel.Id, _context)))
                throw new ForbiddenException();

            if (await _context.MselAttackTechniques.AnyAsync(m => m.MselId == msel.Id && m.AttackTechniqueId == technique.Id, ct))
                throw new ConflictException($"{technique.ExternalId} is already in this MSEL's techniques.");

            var pinned = await _context.MselAttackTechniques
                .Where(m => m.MselId == msel.Id && m.AttackTechnique.AttackReleaseId != technique.AttackReleaseId)
                .Select(m => m.AttackTechnique.AttackRelease)
                .FirstOrDefaultAsync(ct);
            if (pinned != null)
                throw new ConflictException(
                    $"This MSEL's techniques are from {pinned.Domain} {pinned.Version}. " +
                    $"Upgrade them to {technique.AttackRelease.Version} before adding techniques from it.");

            var entity = new MselAttackTechniqueEntity(msel.Id, technique.Id)
            {
                Id = mselAttackTechnique.Id != Guid.Empty ? mselAttackTechnique.Id : Guid.NewGuid()
            };
            _context.MselAttackTechniques.Add(entity);
            await _context.SaveChangesAsync(ct);
            _logger.LogWarning("ATT&CK technique {ExternalId} added to MSEL {MselId} by {UserId}", technique.ExternalId, msel.Id, _user.GetId());
            return await GetAsync(entity.Id, true, false, ct);
        }

        public async Task<bool> DeleteAsync(Guid id, bool hasSystemPermission, CancellationToken ct)
        {
            var item = await _context.MselAttackTechniques.SingleOrDefaultAsync(v => v.Id == id, ct);
            if (item == null)
                throw new EntityNotFoundException<MselAttackTechnique>();

            return await RemoveAsync(item, hasSystemPermission, ct);
        }

        public async Task<bool> DeleteByIdsAsync(Guid mselId, Guid attackTechniqueId, bool hasSystemPermission, CancellationToken ct)
        {
            var item = await _context.MselAttackTechniques
                .SingleOrDefaultAsync(v => v.MselId == mselId && v.AttackTechniqueId == attackTechniqueId, ct);
            if (item == null)
                throw new EntityNotFoundException<MselAttackTechnique>();

            return await RemoveAsync(item, hasSystemPermission, ct);
        }

        /// <summary>
        /// Moves the MSEL's pool to another release, matching techniques by their ATT&amp;CK id. A technique the new
        /// release no longer has is dropped and reported; cells that name it keep their text.
        /// </summary>
        public async Task<MselAttackTechniqueUpgrade> UpgradeAsync(Guid mselId, Guid attackReleaseId, bool hasSystemPermission, CancellationToken ct)
        {
            var msel = await _context.Msels.SingleOrDefaultAsync(v => v.Id == mselId, ct);
            if (msel == null)
                throw new EntityNotFoundException<MselEntity>();

            if (!await _context.AttackReleases.AnyAsync(r => r.Id == attackReleaseId, ct))
                throw new EntityNotFoundException<AttackRelease>();

            if (!hasSystemPermission && !(await MselOwnerRequirement.IsMet(_user.GetId(), msel.Id, _context)))
                throw new ForbiddenException();

            var current = await _context.MselAttackTechniques
                .Where(m => m.MselId == mselId)
                .Include(m => m.AttackTechnique)
                .ToListAsync(ct);
            var result = new MselAttackTechniqueUpgrade { MselId = mselId, AttackReleaseId = attackReleaseId };

            var moving = current.Where(m => m.AttackTechnique.AttackReleaseId != attackReleaseId).ToList();
            result.Kept = current.Count - moving.Count;
            if (moving.Count == 0)
                return result;

            var externalIds = moving.Select(m => m.AttackTechnique.ExternalId).ToList();
            var replacements = await _context.AttackTechniques
                .Where(t => t.AttackReleaseId == attackReleaseId && externalIds.Contains(t.ExternalId))
                .ToDictionaryAsync(t => t.ExternalId, t => t.Id, StringComparer.OrdinalIgnoreCase, ct);
            var alreadyThere = current
                .Where(m => m.AttackTechnique.AttackReleaseId == attackReleaseId)
                .Select(m => m.AttackTechniqueId)
                .ToHashSet();

            // One SaveChanges, so the removes and adds land together.
            _context.MselAttackTechniques.RemoveRange(moving);
            foreach (var item in moving.OrderBy(m => m.AttackTechnique.ExternalId))
            {
                if (!replacements.TryGetValue(item.AttackTechnique.ExternalId, out var replacementId))
                {
                    result.Dropped.Add(item.AttackTechnique.ExternalId);
                    continue;
                }

                result.Kept++;
                if (alreadyThere.Add(replacementId))
                    _context.MselAttackTechniques.Add(new MselAttackTechniqueEntity(mselId, replacementId) { Id = Guid.NewGuid() });
            }
            await _context.SaveChangesAsync(ct);

            _logger.LogWarning("MSEL {MselId} ATT&CK techniques upgraded to release {ReleaseId} by {UserId}: {Kept} kept, {Dropped} dropped",
                mselId, attackReleaseId, _user.GetId(), result.Kept, result.Dropped.Count);
            return result;
        }

        private async Task<bool> RemoveAsync(MselAttackTechniqueEntity item, bool hasSystemPermission, CancellationToken ct)
        {
            if (!hasSystemPermission && !(await MselOwnerRequirement.IsMet(_user.GetId(), item.MselId, _context)))
                throw new ForbiddenException();

            _context.MselAttackTechniques.Remove(item);
            await _context.SaveChangesAsync(ct);
            _logger.LogWarning("ATT&CK technique {AttackTechniqueId} removed from MSEL {MselId} by {UserId}", item.AttackTechniqueId, item.MselId, _user.GetId());
            return true;
        }
    }
}
