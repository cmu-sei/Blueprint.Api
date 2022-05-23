// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
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
    public class PermissionController : BaseController
    {
        private readonly IPermissionService _permissionService;
        private readonly IAuthorizationService _authorizationService;

        public PermissionController(IPermissionService permissionService, IAuthorizationService authorizationService)
        {
            _permissionService = permissionService;
            _authorizationService = authorizationService;
        }

        /// <summary>
        /// Gets all Permission in the system
        /// </summary>
        /// <remarks>
        /// Returns a list of all of the Permissions in the system.
        /// <para />
        /// Only accessible to a SuperUser
        /// </remarks>
        /// <returns></returns>
        [HttpGet("permissions")]
        [ProducesResponseType(typeof(IEnumerable<Permission>), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "getPermissions")]
        public async Task<IActionResult> Get(CancellationToken ct)
        {
            var list = await _permissionService.GetAsync(ct);
            return Ok(list);
        }

        /// <summary>
        /// Gets Permissions for the current user
        /// </summary>
        /// <remarks>
        /// Returns a list of the current user's Permissions.
        /// </remarks>
        /// <returns></returns>
        [HttpGet("permissions/mine")]
        [ProducesResponseType(typeof(IEnumerable<Permission>), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "getMyPermissions")]
        public async Task<IActionResult> GetMine(CancellationToken ct)
        {
            var list = await _permissionService.GetMineAsync(ct);
            return Ok(list);
        }

        /// <summary>
        /// Gets Permissions for the specified user
        /// </summary>
        /// <remarks>
        /// Returns a list of the specified user's Permissions.
        /// <para />
        /// Only accessible to a SuperUser
        /// </remarks>
        /// <returns></returns>
        [HttpGet("users/{userId}/permissions")]
        [ProducesResponseType(typeof(IEnumerable<Permission>), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "getPermissionsByUser")]
        public async Task<IActionResult> GetByUser([FromRoute] Guid userId, CancellationToken ct)
        {
            var list = await _permissionService.GetByUserAsync(userId, ct);
            return Ok(list);
        }

        /// <summary>
        /// Gets a specific Permission by id
        /// </summary>
        /// <remarks>
        /// Returns the Permission with the id specified
        /// <para />
        /// Accessible to a SuperUser or a User that is a member of a Team within the specified Permission
        /// </remarks>
        /// <param name="id">The id of the Permission</param>
        /// <param name="ct"></param>
        /// <returns></returns>
        [HttpGet("permissions/{id}")]
        [ProducesResponseType(typeof(Permission), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "getPermission")]
        public async Task<IActionResult> Get(Guid id, CancellationToken ct)
        {
            var permission = await _permissionService.GetAsync(id, ct);

            if (permission == null)
                throw new EntityNotFoundException<Permission>();

            return Ok(permission);
        }

        /// <summary>
        /// Creates a new Permission
        /// </summary>
        /// <remarks>
        /// Creates a new Permission with the attributes specified
        /// <para />
        /// Accessible only to a SuperUser or an Administrator
        /// </remarks>
        /// <param name="permission">The data to create the Permission with</param>
        /// <param name="ct"></param>
        [HttpPost("permissions")]
        [ProducesResponseType(typeof(Permission), (int)HttpStatusCode.Created)]
        [SwaggerOperation(OperationId = "createPermission")]
        public async Task<IActionResult> Create([FromBody] Permission permission, CancellationToken ct)
        {
            permission.CreatedBy = User.GetId();
            var createdPermission = await _permissionService.CreateAsync(permission, ct);
            return CreatedAtAction(nameof(this.Get), new { id = createdPermission.Id }, createdPermission);
        }

        /// <summary>
        /// Updates a Permission
        /// </summary>
        /// <remarks>
        /// Updates a Permission with the attributes specified
        /// <para />
        /// Accessible only to a SuperUser or a User on an Admin Team within the specified Permission
        /// </remarks>
        /// <param name="id">The Id of the Exericse to update</param>
        /// <param name="permission">The updated Permission values</param>
        /// <param name="ct"></param>
        [HttpPut("permissions/{id}")]
        [ProducesResponseType(typeof(Permission), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "updatePermission")]
        public async Task<IActionResult> Update([FromRoute] Guid id, [FromBody] Permission permission, CancellationToken ct)
        {
            permission.ModifiedBy = User.GetId();
            var updatedPermission = await _permissionService.UpdateAsync(id, permission, ct);
            return Ok(updatedPermission);
        }

        /// <summary>
        /// Deletes a Permission
        /// </summary>
        /// <remarks>
        /// Deletes a Permission with the specified id
        /// <para />
        /// Accessible only to a SuperUser or a User on an Admin Team within the specified Permission
        /// </remarks>
        /// <param name="id">The id of the Permission to delete</param>
        /// <param name="ct"></param>
        [HttpDelete("permissions/{id}")]
        [ProducesResponseType((int)HttpStatusCode.NoContent)]
        [SwaggerOperation(OperationId = "deletePermission")]
        public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
        {
            await _permissionService.DeleteAsync(id, ct);
            return NoContent();
        }

    }
}
