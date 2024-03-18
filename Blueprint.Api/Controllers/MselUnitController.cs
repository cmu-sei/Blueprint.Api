// Copyright 2024 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Blueprint.Api.Infrastructure.Exceptions;
using Blueprint.Api.Services;
using Blueprint.Api.ViewModels;
using Swashbuckle.AspNetCore.Annotations;

namespace Blueprint.Api.Controllers
{
    public class MselUnitController : BaseController
    {
        private readonly IMselUnitService _mselUnitService;
        private readonly IAuthorizationService _authorizationService;

        public MselUnitController(IMselUnitService mselUnitService, IAuthorizationService authorizationService)
        {
            _mselUnitService = mselUnitService;
            _authorizationService = authorizationService;
        }

        /// <summary>
        /// Gets all MselUnits for a MSEL
        /// </summary>
        /// <remarks>
        /// Returns a list of all of the MselUnits for the msel.
        /// </remarks>
        /// <param name="mselId">The id of the Msel</param>
        /// <param name="ct"></param>
        /// <returns></returns>
        [HttpGet("msels/{mselId}/mselunits")]
        [ProducesResponseType(typeof(IEnumerable<MselUnit>), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "getMselUnits")]
        public async Task<IActionResult> GetByMsel(Guid mselId, CancellationToken ct)
        {
            var list = await _mselUnitService.GetByMselAsync(mselId, ct);
            return Ok(list);
        }

        /// <summary>
        /// Gets a specific MselUnit by id
        /// </summary>
        /// <remarks>
        /// Returns the MselUnit with the id specified
        /// <para />
        /// </remarks>
        /// <param name="id">The id of the MselUnit</param>
        /// <param name="ct"></param>
        /// <returns></returns>
        [HttpGet("mselunits/{id}")]
        [ProducesResponseType(typeof(MselUnit), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "getMselUnit")]
        public async Task<IActionResult> Get(Guid id, CancellationToken ct)
        {
            var unit = await _mselUnitService.GetAsync(id, ct);

            if (unit == null)
                throw new EntityNotFoundException<MselUnit>();

            return Ok(unit);
        }

        /// <summary>
        /// Creates a new MselUnit
        /// </summary>
        /// <remarks>
        /// Creates a new MselUnit with the attributes specified
        /// <para />
        /// </remarks>
        /// <param name="mselUnit">The data to create the MselUnit with</param>
        /// <param name="ct"></param>
        [HttpPost("mselunits")]
        [ProducesResponseType(typeof(MselUnit), (int)HttpStatusCode.Created)]
        [SwaggerOperation(OperationId = "createMselUnit")]
        public async Task<IActionResult> Create([FromBody] MselUnit mselUnit, CancellationToken ct)
        {
            var createdMselUnit = await _mselUnitService.CreateAsync(mselUnit, ct);
            return CreatedAtAction(nameof(this.Get), new { id = createdMselUnit.Id }, createdMselUnit);
        }

        /// <summary>
        /// Updates a MselUnit
        /// </summary>
        /// <remarks>
        /// Updates a MselUnit with the attributes specified
        /// <para />
        /// </remarks>
        /// <param name="id">The Id of the MselUnit to update</param>
        /// <param name="mselUnit">The updated MselUnit values</param>
        /// <param name="ct"></param>
        [HttpPut("mselunits/{id}")]
        [ProducesResponseType(typeof(Unit), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "updateMselUnit")]
        public async Task<IActionResult> Update([FromRoute] Guid id, [FromBody] MselUnit mselUnit, CancellationToken ct)
        {
            var updatedUnit = await _mselUnitService.UpdateAsync(id, mselUnit, ct);
            return Ok(updatedUnit);
        }

        /// <summary>
        /// Deletes a MselUnit
        /// </summary>
        /// <remarks>
        /// Deletes a MselUnit with the specified id
        /// <para />
        /// </remarks>
        /// <param name="id">The id of the MselUnit to delete</param>
        /// <param name="ct"></param>
        [HttpDelete("mselunits/{id}")]
        [ProducesResponseType((int)HttpStatusCode.NoContent)]
        [SwaggerOperation(OperationId = "deleteMselUnit")]
        public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
        {
            await _mselUnitService.DeleteAsync(id, ct);
            return NoContent();
        }

        /// <summary>
        /// Deletes a MselUnit by msel ID and unit ID
        /// </summary>
        /// <remarks>
        /// Deletes a MselUnit with the specified msel ID and unit ID
        /// <para />
        /// </remarks>
        /// <param name="mselId">ID of a msel.</param>
        /// <param name="unitId">ID of a unit.</param>
        /// <param name="ct"></param>
        [HttpDelete("msels/{mselId}/units/{unitId}")]
        [ProducesResponseType((int)HttpStatusCode.NoContent)]
        [SwaggerOperation(OperationId = "deleteMselUnitByIds")]
        public async Task<IActionResult> Delete(Guid mselId, Guid unitId, CancellationToken ct)
        {
            await _mselUnitService.DeleteByIdsAsync(mselId, unitId, ct);
            return NoContent();
        }

    }
}

