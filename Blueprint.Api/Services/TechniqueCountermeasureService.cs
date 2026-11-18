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
using Blueprint.Api.Data;
using Blueprint.Api.Data.Models;
using Blueprint.Api.Infrastructure.Mappings;
using Blueprint.Api.Infrastructure.Authorization;
using Blueprint.Api.Infrastructure.Exceptions;
using Blueprint.Api.Infrastructure.Extensions;
using Blueprint.Api.ViewModels;

namespace Blueprint.Api.Services
{
    public interface ITechniqueCountermeasureService
    {
        Task<TechniqueCountermeasureImport> ImportAsync(Guid releaseId, FileForm form, CancellationToken ct);
        Task<IEnumerable<TechniqueCountermeasure>> GetByReleaseAsync(Guid releaseId, string offensiveId, CancellationToken ct);
        Task<MselCountermeasures> GetByMselAsync(Guid mselId, bool hasSystemPermission, bool hasCreateMselsPermission, CancellationToken ct);
    }

    /// <summary>
    /// Technique mappings, such as which defensive techniques counter an attack technique. A mapping joins a
    /// source technique, by id so one import serves every release of its framework, to a target technique in the
    /// release the mappings were imported onto.
    /// </summary>
    public class TechniqueCountermeasureService : ITechniqueCountermeasureService
    {
        private readonly BlueprintContext _context;
        private readonly ClaimsPrincipal _user;
        private readonly ILogger<ITechniqueCountermeasureService> _logger;

        public TechniqueCountermeasureService(BlueprintContext context, IPrincipal user, ILogger<ITechniqueCountermeasureService> logger)
        {
            _context = context;
            _user = user as ClaimsPrincipal;
            _logger = logger;
        }

        /// <summary>
        /// Replaces the release's mappings with the file's, read as the release's framework defines its mappings. A
        /// pair whose target is not a technique of the release is skipped and counted, so a file from a neighbouring
        /// version still imports.
        /// </summary>
        public async Task<TechniqueCountermeasureImport> ImportAsync(Guid releaseId, FileForm form, CancellationToken ct)
        {
            var release = await _context.TechniqueReleases
                .Include(r => r.TechniqueFramework)
                .SingleOrDefaultAsync(r => r.Id == releaseId, ct);
            if (release == null)
                throw new EntityNotFoundException<TechniqueRelease>();
            var definition = release.TechniqueFramework?.Settings?.Mappings;
            if (definition == null)
                throw new InvalidTechniqueBundleException(release.TechniqueFramework == null
                    ? $"{release.Name} {release.Version} has no technique framework, so there is no mapping definition to read the file with. Set its framework first."
                    : $"The {release.TechniqueFramework.Name} framework defines no mappings to import.");

            SparqlMappingReader.Result parsed;
            using (var stream = form.ToUpload.OpenReadStream())
            {
                parsed = await SparqlMappingReader.ReadAsync(stream, definition, ct);
            }

            var techniques = await _context.Techniques
                .Where(t => t.TechniqueReleaseId == releaseId)
                .Select(t => new { t.Id, t.StixId, t.ExternalId })
                .ToListAsync(ct);
            var techniquesByTarget = techniques
                .GroupBy(t => definition.TargetMatch == MappingTargetMatch.SourceIdLocalName ? SparqlMappingReader.LocalName(t.StixId) : t.ExternalId)
                .Where(g => !string.IsNullOrEmpty(g.Key))
                .ToDictionary(g => g.Key, g => g.First().Id, StringComparer.OrdinalIgnoreCase);

            var rows = new List<TechniqueCountermeasureEntity>();
            var skipped = 0;
            foreach (var pair in parsed.Pairs)
            {
                if (!techniquesByTarget.TryGetValue(pair.Target, out var techniqueId))
                {
                    skipped++;
                    continue;
                }
                rows.Add(new TechniqueCountermeasureEntity
                {
                    Id = Guid.NewGuid(),
                    ReleaseId = releaseId,
                    OffensiveExternalId = pair.SourceId,
                    DefensiveTechniqueId = techniqueId,
                    DefensiveTactic = pair.Tactic,
                    Rationale = pair.Rationale,
                    Weight = pair.Weight
                });
            }

            // one SaveChanges, so a re-import either replaces the old rows or leaves them
            _context.TechniqueCountermeasures.RemoveRange(_context.TechniqueCountermeasures.Where(c => c.ReleaseId == releaseId));
            _context.TechniqueCountermeasures.AddRange(rows);
            await _context.SaveChangesAsync(ct);

            var result = new TechniqueCountermeasureImport
            {
                TechniqueReleaseId = releaseId,
                RowsRead = parsed.RowsRead,
                PairsStored = rows.Count,
                PairsSkipped = skipped,
                OffensiveTechniqueCount = rows.Select(r => r.OffensiveExternalId).Distinct().Count()
            };
            _logger.LogWarning("Mappings imported onto {Domain} {Version} by {UserId}: {Rows} rows, {Pairs} pairs stored, {Skipped} skipped, {Sources} source techniques",
                release.Domain, release.Version, _user.GetId(), result.RowsRead, result.PairsStored, result.PairsSkipped, result.OffensiveTechniqueCount);
            return result;
        }

        /// <summary>
        /// A release's mappings: all of them, or one offensive technique's ranked by weight. A sub-technique with
        /// none of its own gets its parent's, flagged as inherited.
        /// </summary>
        public async Task<IEnumerable<TechniqueCountermeasure>> GetByReleaseAsync(Guid releaseId, string offensiveId, CancellationToken ct)
        {
            if (!await _context.TechniqueReleases.AnyAsync(r => r.Id == releaseId, ct))
                throw new EntityNotFoundException<TechniqueRelease>();

            offensiveId = offensiveId?.Trim();
            if (string.IsNullOrEmpty(offensiveId))
            {
                var all = await Mappings(releaseId)
                    .OrderBy(c => c.OffensiveExternalId)
                    .ThenByDescending(c => c.Weight)
                    .ThenBy(c => c.DefensiveTechnique.ExternalId)
                    .ToListAsync(ct);
                return all.Select(c => c.ToTechniqueCountermeasure()).ToList();
            }

            var own = await Mappings(releaseId).Where(c => c.OffensiveExternalId == offensiveId).ToListAsync(ct);
            if (own.Count > 0)
                return Rank(own, inherited: false);

            // the parent an attack release imported the technique under
            var parentId = await _context.Techniques
                .Where(t => t.ExternalId == offensiveId && !t.TechniqueRelease.IsDefensive && t.ParentId != null)
                .OrderBy(t => t.TechniqueReleaseId)
                .Select(t => t.Parent.ExternalId)
                .FirstOrDefaultAsync(ct);
            if (parentId == null)
                return [];

            var parents = await Mappings(releaseId).Where(c => c.OffensiveExternalId == parentId).ToListAsync(ct);
            return Rank(parents, inherited: true);
        }

        /// <summary>
        /// The countermeasures of every attack technique in a MSEL's pool, from each defensive release it pools that
        /// has mappings, else the newest release with mappings. Each pooled technique gets a group, empty when nothing
        /// counters it. The release named is the newest one read, or the first pooled one when none has mappings.
        /// </summary>
        public async Task<MselCountermeasures> GetByMselAsync(Guid mselId, bool hasSystemPermission, bool hasCreateMselsPermission, CancellationToken ct)
        {
            var msel = await _context.Msels.SingleOrDefaultAsync(v => v.Id == mselId, ct);
            if (msel == null)
                throw new EntityNotFoundException<MselEntity>();

            if (!hasSystemPermission && !(await MselViewRequirement.IsMet(_user.GetId(), msel.Id, hasCreateMselsPermission, _context)))
                throw new ForbiddenException();

            var pool = await _context.MselTechniques
                .Where(m => m.MselId == mselId)
                .Select(m => new
                {
                    m.TechniqueId,
                    m.Technique.ExternalId,
                    ParentExternalId = m.Technique.Parent.ExternalId,
                    m.Technique.TechniqueReleaseId,
                    m.Technique.TechniqueRelease.IsDefensive
                })
                .ToListAsync(ct);
            var techniques = pool
                .Where(t => !t.IsDefensive)
                .OrderBy(t => t.ExternalId)
                .ToList();

            var result = new MselCountermeasures { MselId = mselId };
            var pooled = pool.Where(t => t.IsDefensive).Select(t => t.TechniqueReleaseId).Distinct().ToList();
            // newest first; an undated release after the dated ones, as Postgres would otherwise put nulls first
            var withMappings = _context.TechniqueReleases
                .Where(r => r.IsDefensive && r.Countermeasures.Any() && (pooled.Count == 0 || pooled.Contains(r.Id)))
                .OrderByDescending(r => r.ReleaseDate.HasValue)
                .ThenByDescending(r => r.ReleaseDate)
                .ThenByDescending(r => r.Version)
                .Select(r => r.Id);
            var releases = await (pooled.Count == 0 ? withMappings.Take(1) : withMappings).ToListAsync(ct);
            result.HasMappings = releases.Count > 0;
            result.TechniqueReleaseId = result.HasMappings ? releases[0] : pooled.OrderBy(id => id).Cast<Guid?>().FirstOrDefault();

            var byOffensiveId = new Dictionary<string, List<TechniqueCountermeasureEntity>>();
            if (result.HasMappings && techniques.Count > 0)
            {
                // the techniques' ids and their parents', for those with no mappings of their own
                var ids = techniques
                    .SelectMany(t => new[] { t.ExternalId, t.ParentExternalId })
                    .Where(id => id != null)
                    .Distinct()
                    .ToList();
                var rows = await Mappings(releases)
                    .Where(c => ids.Contains(c.OffensiveExternalId))
                    .ToListAsync(ct);
                byOffensiveId = rows.GroupBy(c => c.OffensiveExternalId).ToDictionary(g => g.Key, g => g.ToList());
            }

            foreach (var technique in techniques)
            {
                var group = new MselTechniqueCountermeasures { TechniqueId = technique.TechniqueId, OffensiveExternalId = technique.ExternalId };
                if (byOffensiveId.TryGetValue(technique.ExternalId, out var own))
                {
                    group.Countermeasures = Rank(own, inherited: false).ToList();
                }
                else
                {
                    var parentId = technique.ParentExternalId;
                    if (parentId != null && byOffensiveId.TryGetValue(parentId, out var parents))
                    {
                        group.InheritedFrom = parentId;
                        group.Countermeasures = Rank(parents, inherited: true).ToList();
                    }
                }
                result.Techniques.Add(group);
            }

            return result;
        }

        private IQueryable<TechniqueCountermeasureEntity> Mappings(Guid releaseId) => Mappings([releaseId]);

        private IQueryable<TechniqueCountermeasureEntity> Mappings(List<Guid> releaseIds) =>
            _context.TechniqueCountermeasures
                .Where(c => releaseIds.Contains(c.ReleaseId))
                .Include(c => c.DefensiveTechnique)
                .ThenInclude(t => t.TechniqueRelease);

        /// <summary>Strongest first; ties by the defensive technique's id.</summary>
        private IEnumerable<TechniqueCountermeasure> Rank(IEnumerable<TechniqueCountermeasureEntity> rows, bool inherited)
        {
            var ranked = rows.OrderByDescending(c => c.Weight).ThenBy(c => c.DefensiveTechnique.ExternalId).ToList();
            var mapped = ranked.Select(c => c.ToTechniqueCountermeasure()).ToList();
            foreach (var item in mapped)
                item.InheritedFromParent = inherited;
            return mapped;
        }
    }
}
