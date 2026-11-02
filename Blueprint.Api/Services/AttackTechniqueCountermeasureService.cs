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
    public interface IAttackTechniqueCountermeasureService
    {
        Task<AttackTechniqueCountermeasureImport> ImportAsync(Guid releaseId, FileForm form, CancellationToken ct);
        Task<IEnumerable<AttackTechniqueCountermeasure>> GetByReleaseAsync(Guid releaseId, string offensiveId, CancellationToken ct);
        Task<MselCountermeasures> GetByMselAsync(Guid mselId, bool hasSystemPermission, bool hasCreateMselsPermission, CancellationToken ct);
    }

    /// <summary>
    /// The D3FEND "countered by" mappings: which D3FEND techniques counter an offensive technique. They are imported
    /// onto a D3FEND release from its d3fend-full-mappings.json and keyed by the offensive technique's id, so one
    /// import serves every ATT&amp;CK release a MSEL might plan against.
    /// </summary>
    public class AttackTechniqueCountermeasureService : IAttackTechniqueCountermeasureService
    {
        private readonly BlueprintContext _context;
        private readonly ClaimsPrincipal _user;
        private readonly IMapper _mapper;
        private readonly ILogger<IAttackTechniqueCountermeasureService> _logger;

        public AttackTechniqueCountermeasureService(BlueprintContext context, IPrincipal user, ILogger<IAttackTechniqueCountermeasureService> logger, IMapper mapper)
        {
            _context = context;
            _user = user as ClaimsPrincipal;
            _mapper = mapper;
            _logger = logger;
        }

        /// <summary>
        /// Replaces the release's mappings with the file's. A pair whose D3FEND class is not a defensive technique
        /// of the release is skipped and counted, so a mappings file from a neighbouring version still imports.
        /// </summary>
        public async Task<AttackTechniqueCountermeasureImport> ImportAsync(Guid releaseId, FileForm form, CancellationToken ct)
        {
            var release = await _context.AttackReleases.SingleOrDefaultAsync(r => r.Id == releaseId, ct);
            if (release == null)
                throw new EntityNotFoundException<AttackRelease>();
            if (release.Domain != AttackReleaseEntity.D3fendDomain)
                throw new InvalidAttackBundleException($"{release.Name} {release.Version} is not a D3FEND release. The mappings are imported onto the D3FEND release they belong to.");

            D3fendMappingsParser.Result parsed;
            using (var stream = form.ToUpload.OpenReadStream())
            {
                parsed = await D3fendMappingsParser.ParseAsync(stream, ct);
            }

            // The mappings name a technique by its class IRI; the release knows it by the ontology id, d3f:<class>.
            var techniquesByClass = (await _context.AttackTechniques
                    .Where(t => t.AttackReleaseId == releaseId)
                    .Select(t => new { t.Id, t.StixId })
                    .ToListAsync(ct))
                .GroupBy(t => LocalName(t.StixId))
                .ToDictionary(g => g.Key, g => g.First().Id);

            var rows = new List<AttackTechniqueCountermeasureEntity>();
            var skipped = 0;
            foreach (var pair in parsed.Pairs)
            {
                if (!techniquesByClass.TryGetValue(pair.DefensiveClass, out var techniqueId))
                {
                    skipped++;
                    continue;
                }
                rows.Add(new AttackTechniqueCountermeasureEntity
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

            // One SaveChanges, so a re-import either replaces the old rows or leaves them.
            _context.AttackTechniqueCountermeasures.RemoveRange(_context.AttackTechniqueCountermeasures.Where(c => c.ReleaseId == releaseId));
            _context.AttackTechniqueCountermeasures.AddRange(rows);
            await _context.SaveChangesAsync(ct);

            var result = new AttackTechniqueCountermeasureImport
            {
                AttackReleaseId = releaseId,
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
        /// A release's mappings: all of them, or the countermeasures of one offensive technique ranked by weight. A
        /// sub-technique with no mappings of its own is answered with its parent's, flagged as inherited, since
        /// D3FEND maps many techniques only at the parent.
        /// </summary>
        public async Task<IEnumerable<AttackTechniqueCountermeasure>> GetByReleaseAsync(Guid releaseId, string offensiveId, CancellationToken ct)
        {
            if (!await _context.AttackReleases.AnyAsync(r => r.Id == releaseId, ct))
                throw new EntityNotFoundException<AttackRelease>();

            offensiveId = offensiveId?.Trim();
            if (string.IsNullOrEmpty(offensiveId))
            {
                var all = await Mappings(releaseId)
                    .OrderBy(c => c.OffensiveExternalId)
                    .ThenByDescending(c => c.Weight)
                    .ThenBy(c => c.DefensiveTechnique.ExternalId)
                    .ToListAsync(ct);
                return _mapper.Map<IEnumerable<AttackTechniqueCountermeasure>>(all);
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
        /// The countermeasures of every attack technique in a MSEL's pool, from the D3FEND release the MSEL's
        /// defensive techniques are pinned to, else the newest D3FEND release with mappings. Every pooled attack
        /// technique gets a group, empty when nothing counters it, so the client can say so per technique.
        /// </summary>
        public async Task<MselCountermeasures> GetByMselAsync(Guid mselId, bool hasSystemPermission, bool hasCreateMselsPermission, CancellationToken ct)
        {
            var msel = await _context.Msels.SingleOrDefaultAsync(v => v.Id == mselId, ct);
            if (msel == null)
                throw new EntityNotFoundException<MselEntity>();

            if (!hasSystemPermission && !(await MselViewRequirement.IsMet(_user.GetId(), msel.Id, hasCreateMselsPermission, _context)))
                throw new ForbiddenException();

            var pool = await _context.MselAttackTechniques
                .Where(m => m.MselId == mselId)
                .Select(m => new { m.AttackTechniqueId, m.AttackTechnique.ExternalId, m.AttackTechnique.AttackReleaseId, m.AttackTechnique.AttackRelease.Domain })
                .ToListAsync(ct);
            var attackTechniques = pool
                .Where(t => t.Domain != AttackReleaseEntity.D3fendDomain)
                .OrderBy(t => t.ExternalId)
                .ToList();

            var result = new MselCountermeasures { MselId = mselId };
            var pinned = pool.FirstOrDefault(t => t.Domain == AttackReleaseEntity.D3fendDomain)?.AttackReleaseId;
            if (pinned != null)
            {
                result.AttackReleaseId = pinned;
                result.HasMappings = await _context.AttackTechniqueCountermeasures.AnyAsync(c => c.ReleaseId == pinned, ct);
            }
            else
            {
                var newest = await _context.AttackReleases
                    .Where(r => r.Domain == AttackReleaseEntity.D3fendDomain && r.Countermeasures.Any())
                    .OrderByDescending(r => r.ReleaseDate)
                    .ThenByDescending(r => r.Version)
                    .Select(r => (Guid?)r.Id)
                    .FirstOrDefaultAsync(ct);
                result.AttackReleaseId = newest;
                result.HasMappings = newest != null;
            }

            var byOffensiveId = new Dictionary<string, List<AttackTechniqueCountermeasureEntity>>();
            if (result.HasMappings && attackTechniques.Count > 0)
            {
                // the techniques' own ids and their parents', for the ones with no mappings of their own
                var ids = attackTechniques
                    .SelectMany(t => new[] { t.ExternalId, ParentExternalId(t.ExternalId) })
                    .Where(id => id != null)
                    .Distinct()
                    .ToList();
                var rows = await Mappings(result.AttackReleaseId.Value)
                    .Where(c => ids.Contains(c.OffensiveExternalId))
                    .ToListAsync(ct);
                byOffensiveId = rows.GroupBy(c => c.OffensiveExternalId).ToDictionary(g => g.Key, g => g.ToList());
            }

            foreach (var technique in attackTechniques)
            {
                var group = new MselTechniqueCountermeasures { AttackTechniqueId = technique.AttackTechniqueId, OffensiveExternalId = technique.ExternalId };
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
        /// The parent technique's id of a sub-technique: T1003 for T1003.005, AML.T0000 for AML.T0000.000, EX-0001 for
        /// SPARTA's EX-0001.01. Null for a technique (T1003, AML.T0000, T0800), whose last dotted part is not a number.
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

        private IQueryable<AttackTechniqueCountermeasureEntity> Mappings(Guid releaseId) =>
            _context.AttackTechniqueCountermeasures
                .Where(c => c.ReleaseId == releaseId)
                .Include(c => c.DefensiveTechnique)
                .ThenInclude(t => t.AttackRelease);

        /// <summary>Strongest first; ties by the D3FEND id, so the order is stable.</summary>
        private IEnumerable<AttackTechniqueCountermeasure> Rank(IEnumerable<AttackTechniqueCountermeasureEntity> rows, bool inherited)
        {
            var ranked = rows.OrderByDescending(c => c.Weight).ThenBy(c => c.DefensiveTechnique.ExternalId).ToList();
            var mapped = _mapper.Map<List<AttackTechniqueCountermeasure>>(ranked);
            foreach (var item in mapped)
                item.InheritedFromParent = inherited;
            return mapped;
        }

        /// <summary>"CredentialHardening" from "d3f:CredentialHardening" or a full IRI.</summary>
        private static string LocalName(string id)
        {
            if (string.IsNullOrEmpty(id))
                return string.Empty;
            var cut = Math.Max(id.LastIndexOf('#'), Math.Max(id.LastIndexOf('/'), id.LastIndexOf(':')));
            return cut >= 0 ? id[(cut + 1)..] : id;
        }
    }
}
