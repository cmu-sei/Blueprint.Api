// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Blueprint.Api.Data.Enumerations;
using Blueprint.Api.Infrastructure.Authorization;
using Blueprint.Api.Infrastructure.Exceptions;
using Blueprint.Api.Services;
using Blueprint.Api.ViewModels;
using Swashbuckle.AspNetCore.Annotations;

namespace Blueprint.Api.Controllers
{
    /// <summary>
    /// Technique framework releases (MITRE ATT&amp;CK, ATLAS, CAPEC, D3FEND). Reads are open to any authenticated user, as
    /// competency frameworks are: every MSEL with a technique column loads them. Import and delete are managed with
    /// the competency frameworks.
    /// </summary>
    public class TechniqueReleaseController : BaseController
    {
        // enterprise-attack.json is about 50 MB and grows with each release.
        private const long MaxBundleBytes = 256L * 1024 * 1024;

        private readonly ITechniqueReleaseService _techniqueReleaseService;
        private readonly ITechniqueCountermeasureService _countermeasureService;
        private readonly IBlueprintAuthorizationService _authorizationService;

        public TechniqueReleaseController(
            ITechniqueReleaseService techniqueReleaseService,
            ITechniqueCountermeasureService countermeasureService,
            IBlueprintAuthorizationService authorizationService)
        {
            _techniqueReleaseService = techniqueReleaseService;
            _countermeasureService = countermeasureService;
            _authorizationService = authorizationService;
        }

        /// <summary>
        /// Gets all imported technique framework releases
        /// </summary>
        /// <param name="ct"></param>
        [HttpGet("techniquereleases")]
        [ProducesResponseType(typeof(IEnumerable<TechniqueRelease>), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "getTechniqueReleases")]
        public async Task<IActionResult> Get(CancellationToken ct)
        {
            var list = await _techniqueReleaseService.GetAsync(ct);
            return Ok(list);
        }

        /// <summary>
        /// Gets a specific release by id
        /// </summary>
        /// <param name="id">The id of the release</param>
        /// <param name="ct"></param>
        [HttpGet("techniquereleases/{id}")]
        [ProducesResponseType(typeof(TechniqueRelease), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "getTechniqueRelease")]
        public async Task<IActionResult> Get(Guid id, CancellationToken ct)
        {
            var release = await _techniqueReleaseService.GetAsync(id, ct);
            return Ok(release);
        }

        /// <summary>
        /// Gets the tactics of a release, in matrix order
        /// </summary>
        /// <param name="id">The id of the release</param>
        /// <param name="ct"></param>
        [HttpGet("techniquereleases/{id}/tactics")]
        [ProducesResponseType(typeof(IEnumerable<TechniqueTactic>), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "getTechniqueTactics")]
        public async Task<IActionResult> GetTactics(Guid id, CancellationToken ct)
        {
            var list = await _techniqueReleaseService.GetTacticsAsync(id, ct);
            return Ok(list);
        }

        /// <summary>
        /// Gets the techniques and sub-techniques of a release
        /// </summary>
        /// <param name="id">The id of the release</param>
        /// <param name="ct"></param>
        [HttpGet("techniquereleases/{id}/techniques")]
        [ProducesResponseType(typeof(IEnumerable<Technique>), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "getTechniques")]
        public async Task<IActionResult> GetTechniques(Guid id, CancellationToken ct)
        {
            var list = await _techniqueReleaseService.GetTechniquesAsync(id, ct);
            return Ok(list);
        }

        /// <summary>
        /// Gets the D3FEND "countered by" mappings of a D3FEND release
        /// </summary>
        /// <remarks>
        /// With <c>offensiveId</c>, the countermeasures of that technique (T1003.005) ranked by weight; a sub-technique
        /// with none of its own is answered with its parent technique's, each flagged <c>inheritedFromParent</c>.
        /// Without it, every mapping of the release.
        /// </remarks>
        /// <param name="id">The id of the D3FEND release</param>
        /// <param name="offensiveId">The offensive technique's id, e.g. T1003.005</param>
        /// <param name="ct"></param>
        [HttpGet("techniquereleases/{id}/countermeasures")]
        [ProducesResponseType(typeof(IEnumerable<TechniqueCountermeasure>), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "getTechniqueCountermeasures")]
        public async Task<IActionResult> GetCountermeasures(Guid id, [FromQuery] string offensiveId, CancellationToken ct)
        {
            var list = await _countermeasureService.GetByReleaseAsync(id, offensiveId, ct);
            return Ok(list);
        }

        /// <summary>
        /// Imports a release from its STIX 2.x bundle or the D3FEND ontology
        /// </summary>
        /// <remarks>
        /// Accepts enterprise-attack.json, mobile-attack.json or ics-attack.json from the mitre-attack/attack-stix-data
        /// repository, or stix-atlas.json from the mitre-atlas/atlas-data releases, which becomes a release of the
        /// ATLAS domain, or stix-capec.json from the mitre/cti repository (capec/2.1), which becomes a release of the
        /// CAPEC domain, its Meta patterns standing as the groups tactics are in the others. Revoked and deprecated
        /// objects (and, for CAPEC, deprecated and obsolete ones) are skipped. Also accepts d3fend.json (JSON-LD) from
        /// the D3FEND Ontology Resources page, which becomes a defensive release of the d3fend domain. The version is read
        /// from the file; give one only for an older file that does not name it.
        /// </remarks>
        /// <param name="form">The bundle or ontology</param>
        /// <param name="version">The release version (e.g. "17.1"), if the file does not say</param>
        /// <param name="ct"></param>
        [HttpPost("techniquereleases/import")]
        [RequestSizeLimit(MaxBundleBytes)]
        [RequestFormLimits(MultipartBodyLengthLimit = MaxBundleBytes)]
        [ProducesResponseType(typeof(TechniqueRelease), (int)HttpStatusCode.Created)]
        [SwaggerOperation(OperationId = "importTechniqueRelease")]
        public async Task<IActionResult> Import([FromForm] FileForm form, [FromQuery] string version, CancellationToken ct)
        {
            if (!await _authorizationService.AuthorizeAsync([SystemPermission.ManageCompetencyFrameworks], ct))
                throw new ForbiddenException();

            var created = await _techniqueReleaseService.ImportAsync(form, version, ct);
            return CreatedAtAction(nameof(Get), new { id = created.Id }, created);
        }

        /// <summary>
        /// Imports the D3FEND "countered by" mappings onto a D3FEND release
        /// </summary>
        /// <remarks>
        /// Accepts d3fend-full-mappings.json for the release's version, from
        /// https://d3fend.mitre.org/ontologies/d3fend/&lt;version&gt;/d3fend-full-mappings.json. The file's rows are
        /// aggregated to one row per (offensive technique id, D3FEND technique), weighted by the number of distinct
        /// artifact paths between them; a pair whose D3FEND class is not a technique of this release is skipped and
        /// counted. Importing again replaces the release's mappings. Refused with a 400 on a release that is not
        /// defensive or a file that is not SPARQL results JSON.
        /// </remarks>
        /// <param name="id">The id of the D3FEND release</param>
        /// <param name="form">The mappings file</param>
        /// <param name="ct"></param>
        [HttpPost("techniquereleases/{id}/countermeasures")]
        [RequestSizeLimit(MaxBundleBytes)]
        [RequestFormLimits(MultipartBodyLengthLimit = MaxBundleBytes)]
        [ProducesResponseType(typeof(TechniqueCountermeasureImport), (int)HttpStatusCode.Created)]
        [SwaggerOperation(OperationId = "importTechniqueCountermeasures")]
        public async Task<IActionResult> ImportCountermeasures(Guid id, [FromForm] FileForm form, CancellationToken ct)
        {
            if (!await _authorizationService.AuthorizeAsync([SystemPermission.ManageCompetencyFrameworks], ct))
                throw new ForbiddenException();

            var result = await _countermeasureService.ImportAsync(id, form, ct);
            return CreatedAtAction(nameof(Get), new { id }, result);
        }

        /// <summary>
        /// Deletes a release
        /// </summary>
        /// <remarks>
        /// Refused with a 409 while any MSEL's technique pool uses the release.
        /// </remarks>
        /// <param name="id">The id of the release</param>
        /// <param name="ct"></param>
        [HttpDelete("techniquereleases/{id}")]
        [ProducesResponseType((int)HttpStatusCode.NoContent)]
        [SwaggerOperation(OperationId = "deleteTechniqueRelease")]
        public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
        {
            if (!await _authorizationService.AuthorizeAsync([SystemPermission.ManageCompetencyFrameworks], ct))
                throw new ForbiddenException();

            await _techniqueReleaseService.DeleteAsync(id, ct);
            return NoContent();
        }
    }
}
