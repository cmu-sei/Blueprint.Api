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
    public class UserMselRoleController : BaseController
    {
        private readonly IUserMselRoleService _userUserMselRoleService;
        private readonly IAuthorizationService _authorizationService;

        public UserMselRoleController(IUserMselRoleService userUserMselRoleService, IAuthorizationService authorizationService)
        {
            _userUserMselRoleService = userUserMselRoleService;
            _authorizationService = authorizationService;
        }

        /// <summary>
        /// Gets all UserMselRoles in the system
        /// </summary>
        /// <remarks>
        /// Returns a list of all of the UserMselRoles in the system.
        /// <para />
        /// Only accessible to a SuperUser
        /// </remarks>
        /// <returns></returns>
        [HttpGet("usermselroles")]
        [ProducesResponseType(typeof(IEnumerable<UserMselRole>), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "getUserMselRoles")]
        public async Task<IActionResult> Get(CancellationToken ct)
        {
            var list = await _userUserMselRoleService.GetAsync(ct);
            return Ok(list);
        }

        /// <summary>
        /// Gets a specific UserMselRole by id
        /// </summary>
        /// <remarks>
        /// Returns the UserMselRole with the id specified
        /// <para />
        /// Only accessible to a SuperUser
        /// </remarks>
        /// <param name="id">The id of the UserMselRole</param>
        /// <param name="ct"></param>
        /// <returns></returns>
        [HttpGet("usermselroles/{id}")]
        [ProducesResponseType(typeof(UserMselRole), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "getUserMselRole")]
        public async Task<IActionResult> Get(Guid id, CancellationToken ct)
        {
            var role = await _userUserMselRoleService.GetAsync(id, ct);

            if (role == null)
                throw new EntityNotFoundException<UserMselRole>();

            return Ok(role);
        }

        /// <summary>
        /// Creates a new UserMselRole
        /// </summary>
        /// <remarks>
        /// Creates a new UserMselRole with the attributes specified
        /// <para />
        /// Accessible only to a SuperUser
        /// </remarks>
        /// <param name="role">The data to create the UserMselRole with</param>
        /// <param name="ct"></param>
        [HttpPost("usermselroles")]
        [ProducesResponseType(typeof(UserMselRole), (int)HttpStatusCode.Created)]
        [SwaggerOperation(OperationId = "createUserMselRole")]
        public async Task<IActionResult> Create([FromBody] UserMselRole role, CancellationToken ct)
        {
            role.CreatedBy = User.GetId();
            var createdUserMselRole = await _userUserMselRoleService.CreateAsync(role, ct);
            return CreatedAtAction(nameof(this.Get), new { id = createdUserMselRole.Id }, createdUserMselRole);
        }

        /// <summary>
        /// Deletes a UserMselRole
        /// </summary>
        /// <remarks>
        /// Deletes a UserMselRole with the specified id
        /// <para />
        /// Accessible only to a SuperUser
        /// </remarks>
        /// <param name="id">The id of the UserMselRole to delete</param>
        /// <param name="ct"></param>
        [HttpDelete("usermselroles/{id}")]
        [ProducesResponseType((int)HttpStatusCode.NoContent)]
        [SwaggerOperation(OperationId = "deleteUserMselRole")]
        public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
        {
            await _userUserMselRoleService.DeleteAsync(id, ct);
            return NoContent();
        }

    }
}

