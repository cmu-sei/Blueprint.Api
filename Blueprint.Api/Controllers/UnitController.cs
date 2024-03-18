// Copyright 2024 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Blueprint.Api.Infrastructure.Extensions;
using Blueprint.Api.Infrastructure.Exceptions;
using Blueprint.Api.Services;
using Blueprint.Api.ViewModels;
using Swashbuckle.AspNetCore.Annotations;

namespace Blueprint.Api.Controllers
{
    public class UnitController : BaseController
    {
        private readonly IUnitService _unitService;
        private readonly IAuthorizationService _authorizationService;

        public UnitController(IUnitService unitService, IAuthorizationService authorizationService)
        {
            _unitService = unitService;
            _authorizationService = authorizationService;
        }

        /// <summary>
        /// Gets all Unit in the system
        /// </summary>
        /// <remarks>
        /// Returns a list of all of the Units in the system.
        /// <para />
        /// Only accessible to a SuperUser
        /// </remarks>
        /// <returns></returns>
        [HttpGet("units")]
        [ProducesResponseType(typeof(IEnumerable<Unit>), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "getUnits")]
        public async Task<IActionResult> Get(CancellationToken ct)
        {
            var list = await _unitService.GetAsync(ct);
            return Ok(list);
        }

        /// <summary>
        /// Gets Units for the current user
        /// </summary>
        /// <remarks>
        /// Returns a list of the current user's Units.
        /// </remarks>
        /// <returns></returns>
        [HttpGet("my-units")]
        [ProducesResponseType(typeof(IEnumerable<Unit>), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "getMyUnits")]
        public async Task<IActionResult> GetMine(CancellationToken ct)
        {
            var list = await _unitService.GetMineAsync(ct);
            return Ok(list);
        }

        /// <summary>
        /// Gets Units for the specified user
        /// </summary>
        /// <remarks>
        /// Returns a list of the specified user's Units.
        /// <para />
        /// Only accessible to a SuperUser
        /// </remarks>
        /// <returns></returns>
        [HttpGet("users/{userId}/units")]
        [ProducesResponseType(typeof(IEnumerable<Unit>), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "getUnitsByUser")]
        public async Task<IActionResult> GetByUser([FromRoute] Guid userId, CancellationToken ct)
        {
            var list = await _unitService.GetByUserAsync(userId, ct);
            return Ok(list);
        }

        /// <summary>
        /// Gets a specific Unit by id
        /// </summary>
        /// <remarks>
        /// Returns the Unit with the id specified
        /// <para />
        /// Accessible to a SuperUser or a User that is a member of a Unit within the specified Unit
        /// </remarks>
        /// <param name="id">The id of the Unit</param>
        /// <param name="ct"></param>
        /// <returns></returns>
        [HttpGet("units/{id}")]
        [ProducesResponseType(typeof(Unit), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "getUnit")]
        public async Task<IActionResult> Get(Guid id, CancellationToken ct)
        {
            var unit = await _unitService.GetAsync(id, ct);

            if (unit == null)
                throw new EntityNotFoundException<Unit>();

            return Ok(unit);
        }

        /// <summary>
        /// Creates a new Unit
        /// </summary>
        /// <remarks>
        /// Creates a new Unit with the attributes specified
        /// <para />
        /// Accessible only to a SuperUser or an Administrator
        /// </remarks>
        /// <param name="unit">The data to create the Unit with</param>
        /// <param name="ct"></param>
        [HttpPost("units")]
        [ProducesResponseType(typeof(Unit), (int)HttpStatusCode.Created)]
        [SwaggerOperation(OperationId = "createUnit")]
        public async Task<IActionResult> Create([FromBody] Unit unit, CancellationToken ct)
        {
            unit.CreatedBy = User.GetId();
            var createdUnit = await _unitService.CreateAsync(unit, ct);
            return CreatedAtAction(nameof(this.Get), new { id = createdUnit.Id }, createdUnit);
        }

        /// <summary>
        /// Updates a Unit
        /// </summary>
        /// <remarks>
        /// Updates a Unit with the attributes specified
        /// <para />
        /// Accessible only to a SuperUser or a User on an Admin Unit within the specified Unit
        /// </remarks>
        /// <param name="id">The Id of the Exericse to update</param>
        /// <param name="unit">The updated Unit values</param>
        /// <param name="ct"></param>
        [HttpPut("units/{id}")]
        [ProducesResponseType(typeof(Unit), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "updateUnit")]
        public async Task<IActionResult> Update([FromRoute] Guid id, [FromBody] Unit unit, CancellationToken ct)
        {
            unit.ModifiedBy = User.GetId();
            var updatedUnit = await _unitService.UpdateAsync(id, unit, ct);
            return Ok(updatedUnit);
        }

        /// <summary>
        /// Deletes a Unit
        /// </summary>
        /// <remarks>
        /// Deletes a Unit with the specified id
        /// <para />
        /// Accessible only to a SuperUser or a User on an Admin Unit within the specified Unit
        /// </remarks>
        /// <param name="id">The id of the Unit to delete</param>
        /// <param name="ct"></param>
        [HttpDelete("units/{id}")]
        [ProducesResponseType((int)HttpStatusCode.NoContent)]
        [SwaggerOperation(OperationId = "deleteUnit")]
        public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
        {
            await _unitService.DeleteAsync(id, ct);
            return NoContent();
        }

    }
}
