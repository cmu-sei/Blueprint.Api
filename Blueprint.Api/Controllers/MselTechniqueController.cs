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
using Blueprint.Api.Services;
using Blueprint.Api.ViewModels;
using Swashbuckle.AspNetCore.Annotations;

namespace Blueprint.Api.Controllers
{
    public class MselTechniqueController : BaseController
    {
        private readonly IMselTechniqueService _mselTechniqueService;
        private readonly ITechniqueCountermeasureService _countermeasureService;
        private readonly IBlueprintAuthorizationService _authorizationService;

        public MselTechniqueController(
            IMselTechniqueService mselTechniqueService,
            ITechniqueCountermeasureService countermeasureService,
            IBlueprintAuthorizationService authorizationService)
        {
            _mselTechniqueService = mselTechniqueService;
            _countermeasureService = countermeasureService;
            _authorizationService = authorizationService;
        }

        /// <summary>
        /// Gets the techniques in a MSEL's pool
        /// </summary>
        /// <param name="mselId">The id of the Msel</param>
        /// <param name="ct"></param>
        [HttpGet("msels/{mselId}/mseltechniques")]
        [ProducesResponseType(typeof(IEnumerable<MselTechnique>), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "getMselTechniques")]
        public async Task<IActionResult> GetByMsel(Guid mselId, CancellationToken ct)
        {
            var hasSystemPermission = await _authorizationService.AuthorizeAsync([SystemPermission.ViewMsels], ct);
            var hasCreateMselsPermission = await _authorizationService.AuthorizeAsync([SystemPermission.CreateMsels], ct);
            var list = await _mselTechniqueService.GetByMselAsync(mselId, hasSystemPermission, hasCreateMselsPermission, ct);
            return Ok(list);
        }

        /// <summary>
        /// Gets the D3FEND countermeasures of every attack technique in a MSEL's pool
        /// </summary>
        /// <remarks>
        /// From the D3FEND release the MSEL's defensive techniques are pinned to, else the newest D3FEND release with
        /// mappings imported; <c>techniqueReleaseId</c> names it and <c>hasMappings</c> says whether it has any. Each
        /// pooled attack technique gets a group ranked by weight, empty when nothing counters it; a sub-technique with
        /// no mappings of its own gets its parent's, with <c>inheritedFrom</c> naming the parent.
        /// </remarks>
        /// <param name="mselId">The id of the Msel</param>
        /// <param name="ct"></param>
        [HttpGet("msels/{mselId}/techniques/countermeasures")]
        [ProducesResponseType(typeof(MselCountermeasures), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "getMselCountermeasures")]
        public async Task<IActionResult> GetCountermeasures(Guid mselId, CancellationToken ct)
        {
            var hasSystemPermission = await _authorizationService.AuthorizeAsync([SystemPermission.ViewMsels], ct);
            var hasCreateMselsPermission = await _authorizationService.AuthorizeAsync([SystemPermission.CreateMsels], ct);
            var result = await _countermeasureService.GetByMselAsync(mselId, hasSystemPermission, hasCreateMselsPermission, ct);
            return Ok(result);
        }

        /// <summary>
        /// Gets a specific MselTechnique by id
        /// </summary>
        /// <param name="id">The id of the MselTechnique</param>
        /// <param name="ct"></param>
        [HttpGet("mseltechniques/{id}")]
        [ProducesResponseType(typeof(MselTechnique), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "getMselTechnique")]
        public async Task<IActionResult> Get(Guid id, CancellationToken ct)
        {
            var hasSystemPermission = await _authorizationService.AuthorizeAsync([SystemPermission.ViewMsels], ct);
            var hasCreateMselsPermission = await _authorizationService.AuthorizeAsync([SystemPermission.CreateMsels], ct);
            var item = await _mselTechniqueService.GetAsync(id, hasSystemPermission, hasCreateMselsPermission, ct);
            return Ok(item);
        }

        /// <summary>
        /// Adds a technique to a MSEL's pool
        /// </summary>
        /// <remarks>
        /// Refused with a 409 when the MSEL's pool already holds techniques of the same domain (enterprise-attack,
        /// ATLAS, capec, d3fend, ...) from another release. Techniques of different domains sit side by side.
        /// </remarks>
        /// <param name="mselTechnique">The MSEL and the technique</param>
        /// <param name="ct"></param>
        [HttpPost("mseltechniques")]
        [ProducesResponseType(typeof(MselTechnique), (int)HttpStatusCode.Created)]
        [SwaggerOperation(OperationId = "createMselTechnique")]
        public async Task<IActionResult> Create([FromBody] MselTechnique mselTechnique, CancellationToken ct)
        {
            var hasSystemPermission = await _authorizationService.AuthorizeAsync([SystemPermission.ManageMsels], ct);
            var createdItem = await _mselTechniqueService.CreateAsync(mselTechnique, hasSystemPermission, ct);
            return CreatedAtAction(nameof(this.Get), new { id = createdItem.Id }, createdItem);
        }

        /// <summary>
        /// Removes a technique from a MSEL's pool
        /// </summary>
        /// <param name="id">The id of the MselTechnique to delete</param>
        /// <param name="ct"></param>
        [HttpDelete("mseltechniques/{id}")]
        [ProducesResponseType((int)HttpStatusCode.NoContent)]
        [SwaggerOperation(OperationId = "deleteMselTechnique")]
        public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
        {
            var hasSystemPermission = await _authorizationService.AuthorizeAsync([SystemPermission.ManageMsels], ct);
            await _mselTechniqueService.DeleteAsync(id, hasSystemPermission, ct);
            return NoContent();
        }

        /// <summary>
        /// Removes a technique from a MSEL's pool by msel ID and technique ID
        /// </summary>
        /// <param name="mselId">ID of a msel.</param>
        /// <param name="techniqueId">ID of a technique.</param>
        /// <param name="ct"></param>
        [HttpDelete("msels/{mselId}/techniques/{techniqueId}")]
        [ProducesResponseType((int)HttpStatusCode.NoContent)]
        [SwaggerOperation(OperationId = "deleteMselTechniqueByIds")]
        public async Task<IActionResult> Delete(Guid mselId, Guid techniqueId, CancellationToken ct)
        {
            var hasSystemPermission = await _authorizationService.AuthorizeAsync([SystemPermission.ManageMsels], ct);
            await _mselTechniqueService.DeleteByIdsAsync(mselId, techniqueId, hasSystemPermission, ct);
            return NoContent();
        }

        /// <summary>
        /// Moves one domain of a MSEL's technique pool to another release
        /// </summary>
        /// <remarks>
        /// The release's domain (enterprise-attack, ATLAS, capec, d3fend, ...) says which techniques move; the other
        /// domains stay. Techniques are matched by their external id (T1059, AML.T0051, D3-CH). Ones the new release no
        /// longer has are dropped and listed in the response; inject cells that name them keep their text.
        /// </remarks>
        /// <param name="mselId">ID of a msel.</param>
        /// <param name="techniqueReleaseId">ID of the release to move to.</param>
        /// <param name="ct"></param>
        [HttpPost("msels/{mselId}/techniques/upgrade/{techniqueReleaseId}")]
        [ProducesResponseType(typeof(MselTechniqueUpgrade), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "upgradeMselTechniques")]
        public async Task<IActionResult> Upgrade(Guid mselId, Guid techniqueReleaseId, CancellationToken ct)
        {
            var hasSystemPermission = await _authorizationService.AuthorizeAsync([SystemPermission.ManageMsels], ct);
            var result = await _mselTechniqueService.UpgradeAsync(mselId, techniqueReleaseId, hasSystemPermission, ct);
            return Ok(result);
        }
    }
}
