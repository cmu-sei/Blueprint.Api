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
    /// Technique framework releases. Reads are open to any authenticated user; import, notice edits and delete
    /// are managed with the competency frameworks.
    /// </summary>
    public class TechniqueReleaseController : BaseController
    {
        // a large release file is tens of megabytes and grows with each release
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
        /// Gets the mappings imported onto a release
        /// </summary>
        /// <remarks>
        /// With <c>offensiveId</c>, the countermeasures of that technique ranked by weight; a sub-technique with none
        /// of its own gets its parent's, each flagged <c>inheritedFromParent</c>.
        /// Without it, every mapping of the release.
        /// </remarks>
        /// <param name="id">The id of the release</param>
        /// <param name="offensiveId">The source technique's id</param>
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
        /// Imports a release from a STIX 2.x bundle or a JSON-LD ontology
        /// </summary>
        /// <remarks>
        /// The file is read as the technique framework whose match conditions it meets; a 400 when none does or more
        /// than one does. Give the framework to choose one. The version is read from the file; give one only for a
        /// file that does not name it.
        /// </remarks>
        /// <param name="form">The bundle or ontology</param>
        /// <param name="version">The release version, if the file does not say</param>
        /// <param name="techniqueFrameworkId">The framework to read the file as, if more than one matches</param>
        /// <param name="ct"></param>
        [HttpPost("techniquereleases/import")]
        [RequestSizeLimit(MaxBundleBytes)]
        [RequestFormLimits(MultipartBodyLengthLimit = MaxBundleBytes)]
        [ProducesResponseType(typeof(TechniqueRelease), (int)HttpStatusCode.Created)]
        [SwaggerOperation(OperationId = "importTechniqueRelease")]
        public async Task<IActionResult> Import([FromForm] FileForm form, [FromQuery] string version, [FromQuery] Guid? techniqueFrameworkId, CancellationToken ct)
        {
            if (!await _authorizationService.AuthorizeAsync([SystemPermission.ManageCompetencyFrameworks], ct))
                throw new ForbiddenException();

            var created = await _techniqueReleaseService.ImportAsync(form, version, techniqueFrameworkId, ct);
            return CreatedAtAction(nameof(Get), new { id = created.Id }, created);
        }

        /// <summary>
        /// Edits a release's framework and notice fields
        /// </summary>
        /// <remarks>
        /// Changes only the framework, source, source URL, notice and terms URL, so no MSEL's pool is affected. A
        /// blank field is cleared. The framework must be of the release's domain and family. The URLs must be http or
        /// https addresses.
        /// </remarks>
        /// <param name="id">The id of the release</param>
        /// <param name="notice">The notice fields</param>
        /// <param name="ct"></param>
        [HttpPut("techniquereleases/{id}")]
        [ProducesResponseType(typeof(TechniqueRelease), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "updateTechniqueRelease")]
        public async Task<IActionResult> Update(Guid id, [FromBody] TechniqueReleaseNotice notice, CancellationToken ct)
        {
            if (!await _authorizationService.AuthorizeAsync([SystemPermission.ManageCompetencyFrameworks], ct))
                throw new ForbiddenException();

            var updated = await _techniqueReleaseService.UpdateNoticeAsync(id, notice, ct);
            return Ok(updated);
        }

        /// <summary>
        /// Imports a mappings file onto a release
        /// </summary>
        /// <remarks>
        /// The file is read as the release's framework defines its mappings. Rows are aggregated to one per (source
        /// id, target technique), weighted by distinct paths; a pair whose target is not a technique of this release
        /// is skipped and counted. Importing again replaces the release's mappings. Refused with a 400 when the
        /// release's framework defines no mappings or the file is not of their format.
        /// </remarks>
        /// <param name="id">The id of the release</param>
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
