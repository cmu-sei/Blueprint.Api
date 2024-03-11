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
    public class UnitUserController : BaseController
    {
        private readonly IUnitUserService _unitUserService;
        private readonly IAuthorizationService _authorizationService;

        public UnitUserController(IUnitUserService unitUserService, IAuthorizationService authorizationService)
        {
            _unitUserService = unitUserService;
            _authorizationService = authorizationService;
        }

        /// <summary>
        /// Gets all UnitUsers in the system
        /// </summary>
        /// <remarks>
        /// Returns a list of all of the UnitUsers in the system.
        /// <para />
        /// Only accessible to a SuperUser
        /// </remarks>
        /// <returns></returns>
        [HttpGet("unitusers")]
        [ProducesResponseType(typeof(IEnumerable<UnitUser>), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "getUnitUsers")]
        public async Task<IActionResult> Get(CancellationToken ct)
        {
            var list = await _unitUserService.GetAsync(ct);
            return Ok(list);
        }

        /// <summary>
        /// Gets a specific UnitUser by id
        /// </summary>
        /// <remarks>
        /// Returns the UnitUser with the id specified
        /// <para />
        /// Only accessible to a SuperUser
        /// </remarks>
        /// <param name="id">The id of the UnitUser</param>
        /// <param name="ct"></param>
        /// <returns></returns>
        [HttpGet("unitusers/{id}")]
        [ProducesResponseType(typeof(UnitUser), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "getUnitUser")]
        public async Task<IActionResult> Get(Guid id, CancellationToken ct)
        {
            var unit = await _unitUserService.GetAsync(id, ct);

            if (unit == null)
                throw new EntityNotFoundException<UnitUser>();

            return Ok(unit);
        }

        /// <summary>
        /// Creates a new UnitUser
        /// </summary>
        /// <remarks>
        /// Creates a new UnitUser with the attributes specified
        /// <para />
        /// Accessible only to a SuperUser
        /// </remarks>
        /// <param name="unit">The data to create the UnitUser with</param>
        /// <param name="ct"></param>
        [HttpPost("unitusers")]
        [ProducesResponseType(typeof(UnitUser), (int)HttpStatusCode.Created)]
        [SwaggerOperation(OperationId = "createUnitUser")]
        public async Task<IActionResult> Create([FromBody] UnitUser unit, CancellationToken ct)
        {
            unit.CreatedBy = User.GetId();
            var createdUnitUser = await _unitUserService.CreateAsync(unit, ct);
            return CreatedAtAction(nameof(this.Get), new { id = createdUnitUser.Id }, createdUnitUser);
        }

        /// <summary>
        /// Deletes a UnitUser
        /// </summary>
        /// <remarks>
        /// Deletes a UnitUser with the specified id
        /// <para />
        /// Accessible only to a SuperUser
        /// </remarks>
        /// <param name="id">The id of the UnitUser to delete</param>
        /// <param name="ct"></param>
        [HttpDelete("unitusers/{id}")]
        [ProducesResponseType((int)HttpStatusCode.NoContent)]
        [SwaggerOperation(OperationId = "deleteUnitUser")]
        public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
        {
            await _unitUserService.DeleteAsync(id, ct);
            return NoContent();
        }

        /// <summary>
        /// Deletes a UnitUser by user ID and unit ID
        /// </summary>
        /// <remarks>
        /// Deletes a UnitUser with the specified user ID and unit ID
        /// <para />
        /// Accessible only to a SuperUser
        /// </remarks>
        /// <param name="userId">ID of a user.</param>
        /// <param name="unitId">ID of a unit.</param>
        /// <param name="ct"></param>
        [HttpDelete("units/{unitId}/users/{userId}")]
        [ProducesResponseType((int)HttpStatusCode.NoContent)]
        [SwaggerOperation(OperationId = "deleteUnitUserByIds")]
        public async Task<IActionResult> Delete(Guid unitId, Guid userId, CancellationToken ct)
        {
            await _unitUserService.DeleteByIdsAsync(unitId, userId, ct);
            return NoContent();
        }

    }
}

