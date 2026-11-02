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
    public class MselAttackTechniqueController : BaseController
    {
        private readonly IMselAttackTechniqueService _mselAttackTechniqueService;
        private readonly IAttackTechniqueCountermeasureService _countermeasureService;
        private readonly IBlueprintAuthorizationService _authorizationService;

        public MselAttackTechniqueController(
            IMselAttackTechniqueService mselAttackTechniqueService,
            IAttackTechniqueCountermeasureService countermeasureService,
            IBlueprintAuthorizationService authorizationService)
        {
            _mselAttackTechniqueService = mselAttackTechniqueService;
            _countermeasureService = countermeasureService;
            _authorizationService = authorizationService;
        }

        /// <summary>
        /// Gets the techniques in a MSEL's pool
        /// </summary>
        /// <param name="mselId">The id of the Msel</param>
        /// <param name="ct"></param>
        [HttpGet("msels/{mselId}/mselattacktechniques")]
        [ProducesResponseType(typeof(IEnumerable<MselAttackTechnique>), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "getMselAttackTechniques")]
        public async Task<IActionResult> GetByMsel(Guid mselId, CancellationToken ct)
        {
            var hasSystemPermission = await _authorizationService.AuthorizeAsync([SystemPermission.ViewMsels], ct);
            var hasCreateMselsPermission = await _authorizationService.AuthorizeAsync([SystemPermission.CreateMsels], ct);
            var list = await _mselAttackTechniqueService.GetByMselAsync(mselId, hasSystemPermission, hasCreateMselsPermission, ct);
            return Ok(list);
        }

        /// <summary>
        /// Gets the D3FEND countermeasures of every attack technique in a MSEL's pool
        /// </summary>
        /// <remarks>
        /// From the D3FEND release the MSEL's defensive techniques are pinned to, else the newest D3FEND release with
        /// mappings imported; <c>attackReleaseId</c> names it and <c>hasMappings</c> says whether it has any. Each
        /// pooled attack technique gets a group ranked by weight, empty when nothing counters it; a sub-technique with
        /// no mappings of its own gets its parent's, with <c>inheritedFrom</c> naming the parent.
        /// </remarks>
        /// <param name="mselId">The id of the Msel</param>
        /// <param name="ct"></param>
        [HttpGet("msels/{mselId}/attacktechniques/countermeasures")]
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
        /// Gets a specific MselAttackTechnique by id
        /// </summary>
        /// <param name="id">The id of the MselAttackTechnique</param>
        /// <param name="ct"></param>
        [HttpGet("mselattacktechniques/{id}")]
        [ProducesResponseType(typeof(MselAttackTechnique), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "getMselAttackTechnique")]
        public async Task<IActionResult> Get(Guid id, CancellationToken ct)
        {
            var hasSystemPermission = await _authorizationService.AuthorizeAsync([SystemPermission.ViewMsels], ct);
            var hasCreateMselsPermission = await _authorizationService.AuthorizeAsync([SystemPermission.CreateMsels], ct);
            var item = await _mselAttackTechniqueService.GetAsync(id, hasSystemPermission, hasCreateMselsPermission, ct);
            return Ok(item);
        }

        /// <summary>
        /// Adds a technique to a MSEL's pool
        /// </summary>
        /// <remarks>
        /// Refused with a 409 when the MSEL's pool already holds techniques of the same family (attack: ATT&amp;CK and
        /// ATLAS; defensive: D3FEND) from another release.
        /// </remarks>
        /// <param name="mselAttackTechnique">The MSEL and the technique</param>
        /// <param name="ct"></param>
        [HttpPost("mselattacktechniques")]
        [ProducesResponseType(typeof(MselAttackTechnique), (int)HttpStatusCode.Created)]
        [SwaggerOperation(OperationId = "createMselAttackTechnique")]
        public async Task<IActionResult> Create([FromBody] MselAttackTechnique mselAttackTechnique, CancellationToken ct)
        {
            var hasSystemPermission = await _authorizationService.AuthorizeAsync([SystemPermission.ManageMsels], ct);
            var createdItem = await _mselAttackTechniqueService.CreateAsync(mselAttackTechnique, hasSystemPermission, ct);
            return CreatedAtAction(nameof(this.Get), new { id = createdItem.Id }, createdItem);
        }

        /// <summary>
        /// Removes a technique from a MSEL's pool
        /// </summary>
        /// <param name="id">The id of the MselAttackTechnique to delete</param>
        /// <param name="ct"></param>
        [HttpDelete("mselattacktechniques/{id}")]
        [ProducesResponseType((int)HttpStatusCode.NoContent)]
        [SwaggerOperation(OperationId = "deleteMselAttackTechnique")]
        public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
        {
            var hasSystemPermission = await _authorizationService.AuthorizeAsync([SystemPermission.ManageMsels], ct);
            await _mselAttackTechniqueService.DeleteAsync(id, hasSystemPermission, ct);
            return NoContent();
        }

        /// <summary>
        /// Removes a technique from a MSEL's pool by msel ID and technique ID
        /// </summary>
        /// <param name="mselId">ID of a msel.</param>
        /// <param name="attackTechniqueId">ID of a technique.</param>
        /// <param name="ct"></param>
        [HttpDelete("msels/{mselId}/attacktechniques/{attackTechniqueId}")]
        [ProducesResponseType((int)HttpStatusCode.NoContent)]
        [SwaggerOperation(OperationId = "deleteMselAttackTechniqueByIds")]
        public async Task<IActionResult> Delete(Guid mselId, Guid attackTechniqueId, CancellationToken ct)
        {
            var hasSystemPermission = await _authorizationService.AuthorizeAsync([SystemPermission.ManageMsels], ct);
            await _mselAttackTechniqueService.DeleteByIdsAsync(mselId, attackTechniqueId, hasSystemPermission, ct);
            return NoContent();
        }

        /// <summary>
        /// Moves one family of a MSEL's technique pool to another release
        /// </summary>
        /// <remarks>
        /// The release's family (attack: ATT&amp;CK and ATLAS; defensive: D3FEND) says which techniques move; the other
        /// family stays. Techniques are matched by their external id (T1059, AML.T0051, D3-CH). Ones the new release no
        /// longer has are dropped and listed in the response; inject cells that name them keep their text.
        /// </remarks>
        /// <param name="mselId">ID of a msel.</param>
        /// <param name="attackReleaseId">ID of the release to move to.</param>
        /// <param name="ct"></param>
        [HttpPost("msels/{mselId}/attacktechniques/upgrade/{attackReleaseId}")]
        [ProducesResponseType(typeof(MselAttackTechniqueUpgrade), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "upgradeMselAttackTechniques")]
        public async Task<IActionResult> Upgrade(Guid mselId, Guid attackReleaseId, CancellationToken ct)
        {
            var hasSystemPermission = await _authorizationService.AuthorizeAsync([SystemPermission.ManageMsels], ct);
            var result = await _mselAttackTechniqueService.UpgradeAsync(mselId, attackReleaseId, hasSystemPermission, ct);
            return Ok(result);
        }
    }
}
