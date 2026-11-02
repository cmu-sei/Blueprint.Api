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
using Blueprint.Api.Infrastructure.Exceptions;
using Blueprint.Api.Infrastructure.Extensions;
using Blueprint.Api.ViewModels;

namespace Blueprint.Api.Services
{
    public interface IAttackTechniqueCountermeasureService
    {
        Task<AttackTechniqueCountermeasureImport> ImportAsync(Guid releaseId, FileForm form, CancellationToken ct);
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
