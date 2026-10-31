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
    /// MITRE ATT&amp;CK releases. Reads are open to any authenticated user, as competency frameworks are: every MSEL
    /// with an ATT&amp;CK column loads them. Import and delete are managed with the competency frameworks.
    /// </summary>
    public class AttackReleaseController : BaseController
    {
        // enterprise-attack.json is about 50 MB and grows with each release.
        private const long MaxBundleBytes = 256L * 1024 * 1024;

        private readonly IAttackReleaseService _attackReleaseService;
        private readonly IBlueprintAuthorizationService _authorizationService;

        public AttackReleaseController(IAttackReleaseService attackReleaseService, IBlueprintAuthorizationService authorizationService)
        {
            _attackReleaseService = attackReleaseService;
            _authorizationService = authorizationService;
        }

        /// <summary>
        /// Gets all imported ATT&amp;CK releases
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
        /// Gets a specific ATT&amp;CK release by id
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
        /// Gets the tactics of an ATT&amp;CK release, in matrix order
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
        /// Gets the techniques and sub-techniques of an ATT&amp;CK release
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
        /// Imports an ATT&amp;CK release from its STIX 2.x bundle
        /// </summary>
        /// <remarks>
        /// Accepts enterprise-attack.json, mobile-attack.json or ics-attack.json from the mitre-attack/attack-stix-data
        /// repository, or stix-atlas.json from the mitre-atlas/atlas-data releases, which becomes a release of the
        /// ATLAS domain. Revoked and deprecated objects are skipped. The version is read from the bundle; give one
        /// only for an older bundle that does not name its version.
        /// </remarks>
        /// <param name="form">The bundle</param>
        /// <param name="version">The ATT&amp;CK version (e.g. "17.1"), if the bundle does not say</param>
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
        /// Deletes an ATT&amp;CK release
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
