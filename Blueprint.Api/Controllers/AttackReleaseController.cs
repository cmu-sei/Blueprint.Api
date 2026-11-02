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
    /// Technique framework releases (MITRE ATT&amp;CK, ATLAS, D3FEND). Reads are open to any authenticated user, as
    /// competency frameworks are: every MSEL with a technique column loads them. Import and delete are managed with
    /// the competency frameworks.
    /// </summary>
    public class AttackReleaseController : BaseController
    {
        // enterprise-attack.json is about 50 MB and grows with each release.
        private const long MaxBundleBytes = 256L * 1024 * 1024;

        private readonly IAttackReleaseService _attackReleaseService;
        private readonly IAttackTechniqueCountermeasureService _countermeasureService;
        private readonly IBlueprintAuthorizationService _authorizationService;

        public AttackReleaseController(
            IAttackReleaseService attackReleaseService,
            IAttackTechniqueCountermeasureService countermeasureService,
            IBlueprintAuthorizationService authorizationService)
        {
            _attackReleaseService = attackReleaseService;
            _countermeasureService = countermeasureService;
            _authorizationService = authorizationService;
        }

        /// <summary>
        /// Gets all imported technique framework releases
        /// </summary>
        /// <param name="ct"></param>
        [HttpGet("attackreleases")]
        [ProducesResponseType(typeof(IEnumerable<AttackRelease>), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "getAttackReleases")]
        public async Task<IActionResult> Get(CancellationToken ct)
        {
            var list = await _attackReleaseService.GetAsync(ct);
            return Ok(list);
        }

        /// <summary>
        /// Gets a specific release by id
        /// </summary>
        /// <param name="id">The id of the release</param>
        /// <param name="ct"></param>
        [HttpGet("attackreleases/{id}")]
        [ProducesResponseType(typeof(AttackRelease), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "getAttackRelease")]
        public async Task<IActionResult> Get(Guid id, CancellationToken ct)
        {
            var release = await _attackReleaseService.GetAsync(id, ct);
            return Ok(release);
        }

        /// <summary>
        /// Gets the tactics of a release, in matrix order
        /// </summary>
        /// <param name="id">The id of the release</param>
        /// <param name="ct"></param>
        [HttpGet("attackreleases/{id}/tactics")]
        [ProducesResponseType(typeof(IEnumerable<AttackTactic>), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "getAttackTactics")]
        public async Task<IActionResult> GetTactics(Guid id, CancellationToken ct)
        {
            var list = await _attackReleaseService.GetTacticsAsync(id, ct);
            return Ok(list);
        }

        /// <summary>
        /// Gets the techniques and sub-techniques of a release
        /// </summary>
        /// <param name="id">The id of the release</param>
        /// <param name="ct"></param>
        [HttpGet("attackreleases/{id}/techniques")]
        [ProducesResponseType(typeof(IEnumerable<AttackTechnique>), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "getAttackTechniques")]
        public async Task<IActionResult> GetTechniques(Guid id, CancellationToken ct)
        {
            var list = await _attackReleaseService.GetTechniquesAsync(id, ct);
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
        [HttpGet("attackreleases/{id}/countermeasures")]
        [ProducesResponseType(typeof(IEnumerable<AttackTechniqueCountermeasure>), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "getAttackTechniqueCountermeasures")]
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
        /// ATLAS domain. Revoked and deprecated objects are skipped. Also accepts d3fend.json (JSON-LD) from the
        /// D3FEND Ontology Resources page, which becomes a release of the d3fend domain whose techniques are
        /// defensive. The version is read from the file; give one only for an older file that does not name it.
        /// </remarks>
        /// <param name="form">The bundle or ontology</param>
        /// <param name="version">The release version (e.g. "17.1"), if the file does not say</param>
        /// <param name="ct"></param>
        [HttpPost("attackreleases/import")]
        [RequestSizeLimit(MaxBundleBytes)]
        [RequestFormLimits(MultipartBodyLengthLimit = MaxBundleBytes)]
        [ProducesResponseType(typeof(AttackRelease), (int)HttpStatusCode.Created)]
        [SwaggerOperation(OperationId = "importAttackRelease")]
        public async Task<IActionResult> Import([FromForm] FileForm form, [FromQuery] string version, CancellationToken ct)
        {
            if (!await _authorizationService.AuthorizeAsync([SystemPermission.ManageCompetencyFrameworks], ct))
                throw new ForbiddenException();

            var created = await _attackReleaseService.ImportAsync(form, version, ct);
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
        /// counted. Importing again replaces the release's mappings. Refused with a 400 on a release of any other
        /// domain or a file that is not SPARQL results JSON.
        /// </remarks>
        /// <param name="id">The id of the D3FEND release</param>
        /// <param name="form">The mappings file</param>
        /// <param name="ct"></param>
        [HttpPost("attackreleases/{id}/countermeasures")]
        [RequestSizeLimit(MaxBundleBytes)]
        [RequestFormLimits(MultipartBodyLengthLimit = MaxBundleBytes)]
        [ProducesResponseType(typeof(AttackTechniqueCountermeasureImport), (int)HttpStatusCode.Created)]
        [SwaggerOperation(OperationId = "importAttackTechniqueCountermeasures")]
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
        [HttpDelete("attackreleases/{id}")]
        [ProducesResponseType((int)HttpStatusCode.NoContent)]
        [SwaggerOperation(OperationId = "deleteAttackRelease")]
        public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
        {
            if (!await _authorizationService.AuthorizeAsync([SystemPermission.ManageCompetencyFrameworks], ct))
                throw new ForbiddenException();

            await _attackReleaseService.DeleteAsync(id, ct);
            return NoContent();
        }
    }
}
