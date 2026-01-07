// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Blueprint.Api.Infrastructure.Exceptions;
using Blueprint.Api.Infrastructure.Authorization;
using Blueprint.Api.Services;
using Blueprint.Api.ViewModels;
using Blueprint.Api.Data.Enumerations;
using Swashbuckle.AspNetCore.Annotations;

namespace Blueprint.Api.Controllers
{
    public class SystemRolesController : BaseController
    {
        private readonly ISystemRoleService _systemRoleService;
        private readonly IBlueprintAuthorizationService _authorizationService;

        public SystemRolesController(ISystemRoleService systemRoleService, IBlueprintAuthorizationService authorizationService)
        {
            _systemRoleService = systemRoleService;
            _authorizationService = authorizationService;
        }

        /// <summary>
        /// Gets a specific SystemRole by id
        /// </summary>
        /// <remarks>
        /// Returns the SystemRole with the id specified
        /// </remarks>
        /// <param name="id">The id of the SystemRole</param>
        /// <param name="ct"></param>
        /// <returns></returns>
        [HttpGet("system-roles/{id}")]
        [ProducesResponseType(typeof(SystemRole), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "getSystemRole")]
        public async Task<IActionResult> Get([FromRoute] Guid id, CancellationToken ct)
        {
            if (!await _authorizationService.AuthorizeAsync([SystemPermission.ViewRoles], ct))
                throw new ForbiddenException();

            var systemRole = await _systemRoleService.GetAsync(id, ct);

            if (systemRole == null)
                throw new EntityNotFoundException<SystemRole>();

            return Ok(systemRole);
        }

        /// <summary>
        /// Gets all SystemRoles
        /// </summary>
        /// <remarks>
        /// Returns a list of all SystemRoles
        /// </remarks>
        /// <returns></returns>
        [HttpGet("system-roles")]
        [ProducesResponseType(typeof(IEnumerable<SystemRole>), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "getSystemRoles")]
        public async Task<IActionResult> GetAll(CancellationToken ct)
        {
            if (!await _authorizationService.AuthorizeAsync([SystemPermission.ViewRoles], ct))
                throw new ForbiddenException();

            var list = await _systemRoleService.GetAsync(ct);
            return Ok(list);
        }

        /// <summary>
        /// Creates a new SystemRole
        /// </summary>
        /// <remarks>
        /// Creates a new SystemRole with the attributes specified
        /// </remarks>
        /// <param name="systemRole">The data to create the SystemRole with</param>
        /// <param name="ct"></param>
        [HttpPost("system-roles")]
        [ProducesResponseType(typeof(SystemRole), (int)HttpStatusCode.Created)]
        [SwaggerOperation(OperationId = "createSystemRole")]
        public async Task<IActionResult> Create([FromBody] SystemRole systemRole, CancellationToken ct)
        {
            if (!await _authorizationService.AuthorizeAsync([SystemPermission.ManageRoles], ct))
                throw new ForbiddenException();

            var createdSystemRole = await _systemRoleService.CreateAsync(systemRole, ct);
            return CreatedAtAction(nameof(this.Get), new { id = createdSystemRole.Id }, createdSystemRole);
        }

        /// <summary>
        /// Updates a SystemRole
        /// </summary>
        /// <remarks>
        /// Updates a SystemRole with the attributes specified
        /// </remarks>
        /// <param name="id">The Id of the SystemRole to update</param>
        /// <param name="systemRole">The updated SystemRole values</param>
        /// <param name="ct"></param>
        [HttpPut("system-roles/{id}")]
        [ProducesResponseType(typeof(SystemRole), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "updateSystemRole")]
        public async Task<IActionResult> Update([FromRoute] Guid id, [FromBody] SystemRole systemRole, CancellationToken ct)
        {
            if (!await _authorizationService.AuthorizeAsync([SystemPermission.ManageRoles], ct))
                throw new ForbiddenException();

            var updatedSystemRole = await _systemRoleService.UpdateAsync(id, systemRole, ct);
            return Ok(updatedSystemRole);
        }

        /// <summary>
        /// Deletes a SystemRole
        /// </summary>
        /// <remarks>
        /// Deletes a SystemRole with the specified id
        /// </remarks>
        /// <param name="id">The id of the SystemRole to delete</param>
        /// <param name="ct"></param>
        [HttpDelete("system-roles/{id}")]
        [ProducesResponseType((int)HttpStatusCode.NoContent)]
        [SwaggerOperation(OperationId = "deleteSystemRole")]
        public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
        {
            if (!await _authorizationService.AuthorizeAsync([SystemPermission.ManageRoles], ct))
                throw new ForbiddenException();

            await _systemRoleService.DeleteAsync(id, ct);
            return NoContent();
        }
    }
}
