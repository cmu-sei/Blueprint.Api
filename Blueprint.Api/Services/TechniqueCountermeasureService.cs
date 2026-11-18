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
    public interface ITechniqueCountermeasureService
    {
        Task<TechniqueCountermeasureImport> ImportAsync(Guid releaseId, FileForm form, CancellationToken ct);
        Task<IEnumerable<TechniqueCountermeasure>> GetByReleaseAsync(Guid releaseId, string offensiveId, CancellationToken ct);
        Task<MselCountermeasures> GetByMselAsync(Guid mselId, bool hasSystemPermission, bool hasCreateMselsPermission, CancellationToken ct);
    }

    /// <summary>
    /// The "countered by" mappings: which defensive techniques counter an offensive technique. They are keyed by
    /// the offensive technique's id, so one import serves every attack release.
    /// </summary>
    public class TechniqueCountermeasureService : ITechniqueCountermeasureService
    {
        private readonly BlueprintContext _context;
        private readonly ClaimsPrincipal _user;
        private readonly IMapper _mapper;
        private readonly ILogger<ITechniqueCountermeasureService> _logger;

        public TechniqueCountermeasureService(BlueprintContext context, IPrincipal user, ILogger<ITechniqueCountermeasureService> logger, IMapper mapper)
        {
            _context = context;
            _user = user as ClaimsPrincipal;
            _mapper = mapper;
            _logger = logger;
        }

        /// <summary>
        /// Replaces the release's mappings with the file's. A pair whose defensive class is not a technique of the
        /// release is skipped and counted, so a file from a neighbouring version still imports.
        /// </summary>
        public async Task<TechniqueCountermeasureImport> ImportAsync(Guid releaseId, FileForm form, CancellationToken ct)
        {
            var release = await _context.TechniqueReleases.SingleOrDefaultAsync(r => r.Id == releaseId, ct);
            if (release == null)
                throw new EntityNotFoundException<TechniqueRelease>();
            if (!release.IsDefensive)
                throw new InvalidTechniqueBundleException($"{release.Name} {release.Version} is not a defensive release. The mappings are imported onto the D3FEND release they belong to.");

            D3fendMappingsParser.Result parsed;
            using (var stream = form.ToUpload.OpenReadStream())
            {
                parsed = await D3fendMappingsParser.ParseAsync(stream, ct);
            }

            // the mappings name a technique by class IRI; the release knows it as d3f:<class>
            var techniquesByClass = (await _context.Techniques
                    .Where(t => t.TechniqueReleaseId == releaseId)
                    .Select(t => new { t.Id, t.StixId })
                    .ToListAsync(ct))
                .GroupBy(t => LocalName(t.StixId))
                .ToDictionary(g => g.Key, g => g.First().Id);

            var rows = new List<TechniqueCountermeasureEntity>();
            var skipped = 0;
            foreach (var pair in parsed.Pairs)
            {
                if (!techniquesByClass.TryGetValue(pair.DefensiveClass, out var techniqueId))
                {
                    skipped++;
                    continue;
                }
                rows.Add(new TechniqueCountermeasureEntity
                {
                    Id = Guid.NewGuid(),
                    ReleaseId = releaseId,
                    OffensiveExternalId = pair.OffensiveId,
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
            _logger.LogWarning("D3FEND mappings imported onto {Domain} {Version} by {UserId}: {Rows} rows, {Pairs} pairs stored, {Skipped} skipped, {Offensive} offensive techniques",
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
                return _mapper.Map<IEnumerable<TechniqueCountermeasure>>(all);
            }

            var own = await Mappings(releaseId).Where(c => c.OffensiveExternalId == offensiveId).ToListAsync(ct);
            if (own.Count > 0)
                return Rank(own, inherited: false);

            var parentId = ParentExternalId(offensiveId);
            if (parentId == null)
                return [];

            var parents = await Mappings(releaseId).Where(c => c.OffensiveExternalId == parentId).ToListAsync(ct);
            return Rank(parents, inherited: true);
        }

        /// <summary>
        /// The countermeasures of every attack technique in a MSEL's pool, from the defensive release it is pinned
        /// to, else the newest with mappings. Each pooled technique gets a group, empty when nothing counters it.
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
                .Select(m => new { m.TechniqueId, m.Technique.ExternalId, m.Technique.TechniqueReleaseId, m.Technique.TechniqueRelease.IsDefensive })
                .ToListAsync(ct);
            var techniques = pool
                .Where(t => !t.IsDefensive)
                .OrderBy(t => t.ExternalId)
                .ToList();

            var result = new MselCountermeasures { MselId = mselId };
            var pinned = pool.FirstOrDefault(t => t.IsDefensive)?.TechniqueReleaseId;
            if (pinned != null)
            {
                result.TechniqueReleaseId = pinned;
                result.HasMappings = await _context.TechniqueCountermeasures.AnyAsync(c => c.ReleaseId == pinned, ct);
            }
            else
            {
                var newest = await _context.TechniqueReleases
                    .Where(r => r.IsDefensive && r.Countermeasures.Any())
                    .OrderByDescending(r => r.ReleaseDate)
                    .ThenByDescending(r => r.Version)
                    .Select(r => (Guid?)r.Id)
                    .FirstOrDefaultAsync(ct);
                result.TechniqueReleaseId = newest;
                result.HasMappings = newest != null;
            }

            var byOffensiveId = new Dictionary<string, List<TechniqueCountermeasureEntity>>();
            if (result.HasMappings && techniques.Count > 0)
            {
                // the techniques' ids and their parents', for those with no mappings of their own
                var ids = techniques
                    .SelectMany(t => new[] { t.ExternalId, ParentExternalId(t.ExternalId) })
                    .Where(id => id != null)
                    .Distinct()
                    .ToList();
                var rows = await Mappings(result.TechniqueReleaseId.Value)
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
                    var parentId = ParentExternalId(technique.ExternalId);
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

        /// <summary>
        /// The parent id of a sub-technique id such as T1003.005 (T1003); null when the last dotted part is not a number.
        /// </summary>
        public static string ParentExternalId(string externalId)
        {
            if (string.IsNullOrEmpty(externalId))
                return null;
            var dot = externalId.LastIndexOf('.');
            if (dot <= 0 || dot == externalId.Length - 1)
                return null;
            return externalId[(dot + 1)..].All(char.IsAsciiDigit) ? externalId[..dot] : null;
        }

        private IQueryable<TechniqueCountermeasureEntity> Mappings(Guid releaseId) =>
            _context.TechniqueCountermeasures
                .Where(c => c.ReleaseId == releaseId)
                .Include(c => c.DefensiveTechnique)
                .ThenInclude(t => t.TechniqueRelease);

        /// <summary>Strongest first; ties by the defensive technique's id.</summary>
        private IEnumerable<TechniqueCountermeasure> Rank(IEnumerable<TechniqueCountermeasureEntity> rows, bool inherited)
        {
            var ranked = rows.OrderByDescending(c => c.Weight).ThenBy(c => c.DefensiveTechnique.ExternalId).ToList();
            var mapped = _mapper.Map<List<TechniqueCountermeasure>>(ranked);
            foreach (var item in mapped)
                item.InheritedFromParent = inherited;
            return mapped;
        }

        /// <summary>The class's local name from a prefixed or full IRI.</summary>
        private static string LocalName(string id)
        {
            if (string.IsNullOrEmpty(id))
                return string.Empty;
            var cut = Math.Max(id.LastIndexOf('#'), Math.Max(id.LastIndexOf('/'), id.LastIndexOf(':')));
            return cut >= 0 ? id[(cut + 1)..] : id;
        }
    }
}
