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
using AutoMapper.QueryableExtensions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Blueprint.Api.Data;
using Blueprint.Api.Data.Models;
using Blueprint.Api.Infrastructure.Exceptions;
using Blueprint.Api.Infrastructure.Extensions;
using Blueprint.Api.ViewModels;

namespace Blueprint.Api.Services
{
    public interface IAttackReleaseService
    {
        Task<IEnumerable<AttackRelease>> GetAsync(CancellationToken ct);
        Task<AttackRelease> GetAsync(Guid id, CancellationToken ct);
        Task<IEnumerable<AttackTactic>> GetTacticsAsync(Guid id, CancellationToken ct);
        Task<IEnumerable<AttackTechnique>> GetTechniquesAsync(Guid id, CancellationToken ct);
        Task<AttackRelease> ImportAsync(FileForm form, string version, CancellationToken ct);
        Task<bool> DeleteAsync(Guid id, CancellationToken ct);
    }

    /// <summary>
    /// The ATT&amp;CK releases an administrator has uploaded. A release is never edited: a newer ATT&amp;CK version
    /// is imported alongside it, and MSELs move to it one at a time.
    /// </summary>
    public class AttackReleaseService : IAttackReleaseService
    {
        private readonly BlueprintContext _context;
        private readonly ClaimsPrincipal _user;
        private readonly IMapper _mapper;
        private readonly ILogger<IAttackReleaseService> _logger;

        public AttackReleaseService(BlueprintContext context, IPrincipal user, ILogger<IAttackReleaseService> logger, IMapper mapper)
        {
            _context = context;
            _user = user as ClaimsPrincipal;
            _mapper = mapper;
            _logger = logger;
        }

        public async Task<IEnumerable<AttackRelease>> GetAsync(CancellationToken ct)
        {
            return await _context.AttackReleases
                .OrderBy(r => r.Domain)
                .ThenByDescending(r => r.ReleaseDate)
                .ProjectTo<AttackRelease>(_mapper.ConfigurationProvider)
                .ToListAsync(ct);
        }

        public async Task<AttackRelease> GetAsync(Guid id, CancellationToken ct)
        {
            var release = await _context.AttackReleases
                .Where(r => r.Id == id)
                .ProjectTo<AttackRelease>(_mapper.ConfigurationProvider)
                .SingleOrDefaultAsync(ct);
            if (release == null)
                throw new EntityNotFoundException<AttackRelease>();

            return release;
        }

        public async Task<IEnumerable<AttackTactic>> GetTacticsAsync(Guid id, CancellationToken ct)
        {
            await ThrowIfMissingAsync(id, ct);

            var tactics = await _context.AttackTactics
                .Where(t => t.AttackReleaseId == id)
                .OrderBy(t => t.DisplayOrder)
                .ToListAsync(ct);
            return _mapper.Map<IEnumerable<AttackTactic>>(tactics);
        }

        public async Task<IEnumerable<AttackTechnique>> GetTechniquesAsync(Guid id, CancellationToken ct)
        {
            await ThrowIfMissingAsync(id, ct);

            var techniques = await _context.AttackTechniques
                .Where(t => t.AttackReleaseId == id)
                .OrderBy(t => t.ExternalId)
                .ToListAsync(ct);
            return _mapper.Map<IEnumerable<AttackTechnique>>(techniques);
        }

        public async Task<AttackRelease> ImportAsync(FileForm form, string version, CancellationToken ct)
        {
            AttackReleaseEntity release;
            using (var stream = form.ToUpload.OpenReadStream())
            {
                release = await AttackStixParser.ParseAsync(stream, version, ct);
            }

            if (await _context.AttackReleases.AnyAsync(r => r.Domain == release.Domain && r.Version == release.Version, ct))
                throw new ConflictException($"ATT&CK {release.Domain} {release.Version} has already been imported.");

            release.CreatedBy = _user.GetId();
            _context.AttackReleases.Add(release);
            await _context.SaveChangesAsync(ct);
            _logger.LogWarning("ATT&CK {Domain} {Version} imported by {UserId}: {Tactics} tactics, {Techniques} techniques",
                release.Domain, release.Version, _user.GetId(), release.Tactics.Count, release.Techniques.Count);

            return await GetAsync(release.Id, ct);
        }

        public async Task<bool> DeleteAsync(Guid id, CancellationToken ct)
        {
            var release = await _context.AttackReleases.SingleOrDefaultAsync(r => r.Id == id, ct);
            if (release == null)
                throw new EntityNotFoundException<AttackRelease>();

            var mselsUsingIt = await _context.MselAttackTechniques
                .Where(m => m.AttackTechnique.AttackReleaseId == id)
                .Select(m => m.MselId)
                .Distinct()
                .CountAsync(ct);
            if (mselsUsingIt > 0)
                throw new ConflictException($"ATT&CK {release.Domain} {release.Version} is used by {mselsUsingIt} MSEL(s). Upgrade or clear their techniques before deleting it.");

            _context.AttackReleases.Remove(release);
            await _context.SaveChangesAsync(ct);
            _logger.LogWarning("ATT&CK {Domain} {Version} deleted by {UserId}", release.Domain, release.Version, _user.GetId());
            return true;
        }

        private async Task ThrowIfMissingAsync(Guid id, CancellationToken ct)
        {
            if (!await _context.AttackReleases.AnyAsync(r => r.Id == id, ct))
                throw new EntityNotFoundException<AttackRelease>();
        }
    }
}
