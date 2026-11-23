// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using Blueprint.Api.Data;
using Blueprint.Api.Data.Models;
using Blueprint.Api.Infrastructure.Mappings;
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
        Task<TechniqueRelease> ImportAsync(FileForm form, string version, Guid? techniqueFrameworkId, CancellationToken ct);
        Task<TechniqueRelease> UpdateNoticeAsync(Guid id, TechniqueReleaseNotice notice, CancellationToken ct);
        Task<bool> DeleteAsync(Guid id, CancellationToken ct);
    }

    /// <summary>
    /// The releases an administrator has uploaded. Techniques are never edited: a newer version is imported
    /// alongside, and MSELs move to it one at a time. Only the framework and the notice fields can be changed.
    /// </summary>
    public class TechniqueReleaseService : ITechniqueReleaseService
    {
        private readonly BlueprintContext _context;
        private readonly ClaimsPrincipal _user;
        private readonly ILogger<ITechniqueReleaseService> _logger;

        public TechniqueReleaseService(BlueprintContext context, IPrincipal user, ILogger<ITechniqueReleaseService> logger)
        {
            _context = context;
            _user = user as ClaimsPrincipal;
            _logger = logger;
        }

        public async Task<IEnumerable<TechniqueRelease>> GetAsync(CancellationToken ct)
        {
            var releases = await _context.TechniqueReleases
                .OrderBy(r => r.Domain)
                // newest first; an undated release after the dated ones, as Postgres would otherwise put nulls first
                .ThenByDescending(r => r.ReleaseDate.HasValue)
                .ThenByDescending(r => r.ReleaseDate)
                // versions do not sort as text (v9 would beat v16), so a tie goes to the one imported last
                .ThenByDescending(r => r.DateCreated)
                .ProjectToTechniqueRelease()
                .ToListAsync(ct);
            await AddFrameworkSettingsAsync(releases, ct);
            return releases;
        }

        public async Task<TechniqueRelease> GetAsync(Guid id, CancellationToken ct)
        {
            var release = await _context.TechniqueReleases
                .Where(r => r.Id == id)
                .ProjectToTechniqueRelease()
                .SingleOrDefaultAsync(ct);
            if (release == null)
                throw new EntityNotFoundException<TechniqueRelease>();

            await AddFrameworkSettingsAsync([release], ct);
            return release;
        }

        /// <summary>What the releases show from their framework's settings, which the query does not reach.</summary>
        private async Task AddFrameworkSettingsAsync(List<TechniqueRelease> releases, CancellationToken ct)
        {
            var ids = releases.Where(r => r.TechniqueFrameworkId.HasValue).Select(r => r.TechniqueFrameworkId.Value).Distinct().ToList();
            if (ids.Count == 0)
                return;

            var settings = await _context.TechniqueFrameworks
                .Where(f => ids.Contains(f.Id))
                .ToDictionaryAsync(f => f.Id, f => f.Settings ?? new TechniqueFrameworkSettings(), ct);
            foreach (var release in releases)
            {
                if (!release.TechniqueFrameworkId.HasValue || !settings.TryGetValue(release.TechniqueFrameworkId.Value, out var s))
                    continue;

                release.TacticOrder = s.TacticOrder ?? [];
                release.WeaknessUrlTemplate = Blank(s.WeaknessUrlTemplate);
                if (s.Mappings != null)
                {
                    release.MappingRelation = Blank(s.Mappings.Relation) ?? "mapped to";
                    release.MappingsDownloadUrl = Blank(s.Mappings.DownloadUrlTemplate)?.Replace("{version}", Uri.EscapeDataString(release.Version ?? ""), StringComparison.Ordinal);
                    release.MappingsDownloadHint = Blank(s.Mappings.DownloadHint);
                }
            }
        }

        public async Task<IEnumerable<TechniqueTactic>> GetTacticsAsync(Guid id, CancellationToken ct)
        {
            await ThrowIfMissingAsync(id, ct);

            var tactics = await _context.TechniqueTactics
                .Where(t => t.TechniqueReleaseId == id)
                .OrderBy(t => t.DisplayOrder)
                .ToListAsync(ct);
            return tactics.Select(t => t.ToTechniqueTactic()).ToList();
        }

        public async Task<IEnumerable<Technique>> GetTechniquesAsync(Guid id, CancellationToken ct)
        {
            await ThrowIfMissingAsync(id, ct);

            var techniques = await _context.Techniques
                .Where(t => t.TechniqueReleaseId == id)
                .Include(t => t.TechniqueRelease)
                .OrderBy(t => t.ExternalId)
                .ToListAsync(ct);
            return techniques.Select(t => t.ToTechnique()).ToList();
        }

        public async Task<TechniqueRelease> ImportAsync(FileForm form, string version, Guid? techniqueFrameworkId, CancellationToken ct)
        {
            var frameworks = await _context.TechniqueFrameworks.ToListAsync(ct);

            TechniqueReleaseEntity release;
            using (var stream = form.ToUpload.OpenReadStream())
            {
                release = await ReleaseFileReader.ReadAsync(stream, version, frameworks, techniqueFrameworkId, ct);
            }

            if (await _context.TechniqueReleases.AnyAsync(r => r.Domain == release.Domain && r.Version == release.Version, ct))
                throw new ConflictException(AlreadyImported(release));
            // one domain is attack or defensive, never both, as MSELs pin and upgrade by domain
            if (await _context.TechniqueReleases.AnyAsync(r => r.Domain == release.Domain && r.IsDefensive != release.IsDefensive, ct))
                throw new ConflictException($"The {release.Domain} domain already has {(release.IsDefensive ? "attack" : "defensive")} releases, and this one is {(release.IsDefensive ? "defensive" : "attack")}.");

            release.CreatedBy = _user.GetId();
            _context.TechniqueReleases.Add(release);
            try
            {
                await _context.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
            {
                // another import of the same release saved between the check above and this one
                throw new ConflictException(AlreadyImported(release));
            }
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

            if (notice.TechniqueFrameworkId.HasValue && notice.TechniqueFrameworkId != release.TechniqueFrameworkId)
            {
                var framework = await _context.TechniqueFrameworks.SingleOrDefaultAsync(f => f.Id == notice.TechniqueFrameworkId.Value, ct)
                    ?? throw new EntityNotFoundException<TechniqueFramework>();
                // the domain and family are what MSELs pin and pool by, so the framework must agree with them
                if (framework.Domain != release.Domain || framework.IsDefensive != release.IsDefensive)
                    throw new BadRequestException($"{framework.Name} is for {framework.Domain} {(framework.IsDefensive ? "defensive" : "attack")} techniques; this release is {release.Domain} {(release.IsDefensive ? "defensive" : "attack")}.");
            }
            release.TechniqueFrameworkId = notice.TechniqueFrameworkId;

            // only the framework and notice fields; the rest is what MSELs are pinned to
            release.Source = Blank(notice.Source);
            release.SourceUrl = sourceUrl;
            release.Notice = Blank(notice.Notice);
            release.TermsUrl = termsUrl;
            release.ModifiedBy = _user.GetId();
            await _context.SaveChangesAsync(ct);
            _logger.LogWarning("Release {Domain} {Version} framework and notice edited by {UserId}", release.Domain, release.Version, _user.GetId());

            return await GetAsync(id, ct);
        }

        private static string AlreadyImported(TechniqueReleaseEntity release) => $"{release.Domain} {release.Version} has already been imported.";

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
