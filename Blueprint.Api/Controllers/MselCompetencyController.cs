// Copyright 2024 Carnegie Mellon University. All Rights Reserved.
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
    public class MselCompetencyController : BaseController
    {
        private readonly IMselCompetencyService _mselCompetencyService;
        private readonly IBlueprintAuthorizationService _authorizationService;

        public MselCompetencyController(IMselCompetencyService mselCompetencyService, IBlueprintAuthorizationService authorizationService)
        {
            _mselCompetencyService = mselCompetencyService;
            _authorizationService = authorizationService;
        }

        /// <summary>
        /// Gets all MselCompetencies for a MSEL
        /// </summary>
        /// <remarks>
        /// Returns a list of all of the MselCompetencies for the msel.
        /// </remarks>
        /// <param name="mselId">The id of the Msel</param>
        /// <param name="ct"></param>
        /// <returns></returns>
        [HttpGet("msels/{mselId}/mselcompetencies")]
        [ProducesResponseType(typeof(IEnumerable<MselCompetency>), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "getMselCompetencies")]
        public async Task<IActionResult> GetByMsel(Guid mselId, CancellationToken ct)
        {
            var hasSystemPermission = await _authorizationService.AuthorizeAsync([SystemPermission.ViewMsels], ct);
            var hasCreateMselsPermission = await _authorizationService.AuthorizeAsync([SystemPermission.CreateMsels], ct);
            var list = await _mselCompetencyService.GetByMselAsync(mselId, hasSystemPermission, hasCreateMselsPermission, ct);
            return Ok(list);
        }

        /// <summary>
        /// Gets a specific MselCompetency by id
        /// </summary>
        /// <remarks>
        /// Returns the MselCompetency with the id specified
        /// <para />
        /// </remarks>
        /// <param name="id">The id of the MselCompetency</param>
        /// <param name="ct"></param>
        /// <returns></returns>
        [HttpGet("mselcompetencies/{id}")]
        [ProducesResponseType(typeof(MselCompetency), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "getMselCompetency")]
        public async Task<IActionResult> Get(Guid id, CancellationToken ct)
        {
            var hasSystemPermission = await _authorizationService.AuthorizeAsync([SystemPermission.ViewMsels], ct);
            var hasCreateMselsPermission = await _authorizationService.AuthorizeAsync([SystemPermission.CreateMsels], ct);
            var item = await _mselCompetencyService.GetAsync(id, hasSystemPermission, hasCreateMselsPermission, ct);

            if (item == null)
                throw new EntityNotFoundException<MselCompetency>();

            return Ok(item);
        }

        /// <summary>
        /// Creates a new MselCompetency
        /// </summary>
        /// <remarks>
        /// Creates a new MselCompetency with the attributes specified
        /// <para />
        /// </remarks>
        /// <param name="mselCompetency">The data to create the MselCompetency with</param>
        /// <param name="ct"></param>
        [HttpPost("mselcompetencies")]
        [ProducesResponseType(typeof(MselCompetency), (int)HttpStatusCode.Created)]
        [SwaggerOperation(OperationId = "createMselCompetency")]
        public async Task<IActionResult> Create([FromBody] MselCompetency mselCompetency, CancellationToken ct)
        {
            var hasSystemPermission = await _authorizationService.AuthorizeAsync([SystemPermission.ManageMsels], ct);
            var createdItem = await _mselCompetencyService.CreateAsync(mselCompetency, hasSystemPermission, ct);
            return CreatedAtAction(nameof(this.Get), new { id = createdItem.Id }, createdItem);
        }

        /// <summary>
        /// Deletes a MselCompetency
        /// </summary>
        /// <remarks>
        /// Deletes a MselCompetency with the specified id
        /// <para />
        /// </remarks>
        /// <param name="id">The id of the MselCompetency to delete</param>
        /// <param name="ct"></param>
        [HttpDelete("mselcompetencies/{id}")]
        [ProducesResponseType((int)HttpStatusCode.NoContent)]
        [SwaggerOperation(OperationId = "deleteMselCompetency")]
        public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
        {
            var hasSystemPermission = await _authorizationService.AuthorizeAsync([SystemPermission.ManageMsels], ct);
            await _mselCompetencyService.DeleteAsync(id, hasSystemPermission, ct);
            return NoContent();
        }

        /// <summary>
        /// Deletes a MselCompetency by msel ID and competency ID
        /// </summary>
        /// <remarks>
        /// Deletes a MselCompetency with the specified msel ID and competency ID
        /// <para />
        /// </remarks>
        /// <param name="mselId">ID of a msel.</param>
        /// <param name="competencyId">ID of a competency.</param>
        /// <param name="ct"></param>
        [HttpDelete("msels/{mselId}/competencies/{competencyId}")]
        [ProducesResponseType((int)HttpStatusCode.NoContent)]
        [SwaggerOperation(OperationId = "deleteMselCompetencyByIds")]
        public async Task<IActionResult> Delete(Guid mselId, Guid competencyId, CancellationToken ct)
        {
            var hasSystemPermission = await _authorizationService.AuthorizeAsync([SystemPermission.ManageMsels], ct);
            await _mselCompetencyService.DeleteByIdsAsync(mselId, competencyId, hasSystemPermission, ct);
            return NoContent();
        }
    }
}
