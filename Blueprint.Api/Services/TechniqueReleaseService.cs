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
    public interface ITechniqueReleaseService
    {
        Task<IEnumerable<TechniqueRelease>> GetAsync(CancellationToken ct);
        Task<TechniqueRelease> GetAsync(Guid id, CancellationToken ct);
        Task<IEnumerable<TechniqueTactic>> GetTacticsAsync(Guid id, CancellationToken ct);
        Task<IEnumerable<Technique>> GetTechniquesAsync(Guid id, CancellationToken ct);
        Task<TechniqueRelease> ImportAsync(FileForm form, string version, CancellationToken ct);
        Task<TechniqueRelease> UpdateNoticeAsync(Guid id, TechniqueReleaseNotice notice, CancellationToken ct);
        Task<bool> DeleteAsync(Guid id, CancellationToken ct);
    }

    /// <summary>
    /// The technique framework releases (ATT&amp;CK, ATLAS, CAPEC, D3FEND) an administrator has uploaded. A release's
    /// techniques are never edited: a newer version is imported alongside it, and MSELs move to it one at a time. Only
    /// its publisher and notice fields can be changed, as a custom release has none until an administrator gives it some.
    /// </summary>
    public class TechniqueReleaseService : ITechniqueReleaseService
    {
        private readonly BlueprintContext _context;
        private readonly ClaimsPrincipal _user;
        private readonly IMapper _mapper;
        private readonly ILogger<ITechniqueReleaseService> _logger;

        public TechniqueReleaseService(BlueprintContext context, IPrincipal user, ILogger<ITechniqueReleaseService> logger, IMapper mapper)
        {
            _context = context;
            _user = user as ClaimsPrincipal;
            _mapper = mapper;
            _logger = logger;
        }

        public async Task<IEnumerable<TechniqueRelease>> GetAsync(CancellationToken ct)
        {
            return await _context.TechniqueReleases
                .OrderBy(r => r.Domain)
                .ThenByDescending(r => r.ReleaseDate)
                .ProjectTo<TechniqueRelease>(_mapper.ConfigurationProvider)
                .ToListAsync(ct);
        }

        public async Task<TechniqueRelease> GetAsync(Guid id, CancellationToken ct)
        {
            var release = await _context.TechniqueReleases
                .Where(r => r.Id == id)
                .ProjectTo<TechniqueRelease>(_mapper.ConfigurationProvider)
                .SingleOrDefaultAsync(ct);
            if (release == null)
                throw new EntityNotFoundException<TechniqueRelease>();

            return release;
        }

        public async Task<IEnumerable<TechniqueTactic>> GetTacticsAsync(Guid id, CancellationToken ct)
        {
            await ThrowIfMissingAsync(id, ct);

            var tactics = await _context.TechniqueTactics
                .Where(t => t.TechniqueReleaseId == id)
                .OrderBy(t => t.DisplayOrder)
                .ToListAsync(ct);
            return _mapper.Map<IEnumerable<TechniqueTactic>>(tactics);
        }

        public async Task<IEnumerable<Technique>> GetTechniquesAsync(Guid id, CancellationToken ct)
        {
            await ThrowIfMissingAsync(id, ct);

            var techniques = await _context.Techniques
                .Where(t => t.TechniqueReleaseId == id)
                .Include(t => t.TechniqueRelease)
                .OrderBy(t => t.ExternalId)
                .ToListAsync(ct);
            return _mapper.Map<IEnumerable<Technique>>(techniques);
        }

        public async Task<TechniqueRelease> ImportAsync(FileForm form, string version, CancellationToken ct)
        {
            TechniqueReleaseEntity release;
            using (var stream = form.ToUpload.OpenReadStream())
            {
                release = await ReleaseFileParser.ParseAsync(stream, version, ct);
            }

            if (await _context.TechniqueReleases.AnyAsync(r => r.Domain == release.Domain && r.Version == release.Version, ct))
                throw new ConflictException($"{release.Domain} {release.Version} has already been imported.");

            release.CreatedBy = _user.GetId();
            _context.TechniqueReleases.Add(release);
            await _context.SaveChangesAsync(ct);
            _logger.LogWarning("Release {Domain} {Version} imported by {UserId}: {Tactics} tactics, {Techniques} techniques",
                release.Domain, release.Version, _user.GetId(), release.Tactics.Count, release.Techniques.Count);

            return await GetAsync(release.Id, ct);
        }

        public async Task<TechniqueRelease> UpdateNoticeAsync(Guid id, TechniqueReleaseNotice notice, CancellationToken ct)
        {
            if (notice == null)
                throw new BadRequestException("A notice is required.");

            var release = await _context.TechniqueReleases.SingleOrDefaultAsync(r => r.Id == id, ct);
            if (release == null)
                throw new EntityNotFoundException<TechniqueRelease>();

            var sourceUrl = Blank(notice.SourceUrl);
            var termsUrl = Blank(notice.TermsUrl);
            ThrowIfNotWebAddress(sourceUrl, "The source URL");
            ThrowIfNotWebAddress(termsUrl, "The terms URL");

            // Only the four notice fields: the id, domain, version, tactics and techniques are what MSELs are pinned to.
            release.Source = Blank(notice.Source);
            release.SourceUrl = sourceUrl;
            release.Notice = Blank(notice.Notice);
            release.TermsUrl = termsUrl;
            release.ModifiedBy = _user.GetId();
            await _context.SaveChangesAsync(ct);
            _logger.LogWarning("Release {Domain} {Version} notice edited by {UserId}", release.Domain, release.Version, _user.GetId());

            return await GetAsync(id, ct);
        }

        private static string Blank(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

        /// <summary>A link a client puts in an href: only http and https, never a script or data address.</summary>
        private static void ThrowIfNotWebAddress(string url, string what)
        {
            if (url != null && !(Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)))
                throw new BadRequestException($"{what} must be an http or https address.");
        }

        public async Task<bool> DeleteAsync(Guid id, CancellationToken ct)
        {
            var release = await _context.TechniqueReleases.SingleOrDefaultAsync(r => r.Id == id, ct);
            if (release == null)
                throw new EntityNotFoundException<TechniqueRelease>();

            var mselsUsingIt = await _context.MselTechniques
                .Where(m => m.Technique.TechniqueReleaseId == id)
                .Select(m => m.MselId)
                .Distinct()
                .CountAsync(ct);
            if (mselsUsingIt > 0)
                throw new ConflictException($"{release.Domain} {release.Version} is used by {mselsUsingIt} MSEL(s). Upgrade or clear their techniques before deleting it.");

            _context.TechniqueReleases.Remove(release);
            await _context.SaveChangesAsync(ct);
            _logger.LogWarning("Release {Domain} {Version} deleted by {UserId}", release.Domain, release.Version, _user.GetId());
            return true;
        }

        private async Task ThrowIfMissingAsync(Guid id, CancellationToken ct)
        {
            if (!await _context.TechniqueReleases.AnyAsync(r => r.Id == id, ct))
                throw new EntityNotFoundException<TechniqueRelease>();
        }
    }
}
