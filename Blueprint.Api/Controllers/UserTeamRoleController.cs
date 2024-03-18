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
    public class UserTeamRoleController : BaseController
    {
        private readonly IUserTeamRoleService _userUserTeamRoleService;
        private readonly IAuthorizationService _authorizationService;

        public UserTeamRoleController(IUserTeamRoleService userUserTeamRoleService, IAuthorizationService authorizationService)
        {
            _userUserTeamRoleService = userUserTeamRoleService;
            _authorizationService = authorizationService;
        }

        /// <summary>
        /// Gets all UserTeamRoles for the msel
        /// </summary>
        /// <remarks>
        /// Returns a list of all of the UserTeamRoles for the msel.
        /// </remarks>
        /// <returns></returns>
        [HttpGet("msels/{mselId}/userteamroles")]
        [ProducesResponseType(typeof(IEnumerable<UserTeamRole>), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "getUserTeamRolesByMsel")]
        public async Task<IActionResult> GetByMsel(Guid mselId, CancellationToken ct)
        {
            var list = await _userUserTeamRoleService.GetByMselAsync(mselId, ct);
            return Ok(list);
        }

        /// <summary>
        /// Gets a specific UserTeamRole by id
        /// </summary>
        /// <remarks>
        /// Returns the UserTeamRole with the id specified
        /// </remarks>
        /// <param name="id">The id of the UserTeamRole</param>
        /// <param name="ct"></param>
        /// <returns></returns>
        [HttpGet("userteamroles/{id}")]
        [ProducesResponseType(typeof(UserTeamRole), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "getUserTeamRole")]
        public async Task<IActionResult> Get(Guid id, CancellationToken ct)
        {
            var role = await _userUserTeamRoleService.GetAsync(id, ct);

            if (role == null)
                throw new EntityNotFoundException<UserTeamRole>();

            return Ok(role);
        }

        /// <summary>
        /// Creates a new UserTeamRole
        /// </summary>
        /// <remarks>
        /// Creates a new UserTeamRole with the attributes specified
        /// <para />
        /// Accessible only to a SuperUser
        /// </remarks>
        /// <param name="role">The data to create the UserTeamRole with</param>
        /// <param name="ct"></param>
        [HttpPost("userteamroles")]
        [ProducesResponseType(typeof(UserTeamRole), (int)HttpStatusCode.Created)]
        [SwaggerOperation(OperationId = "createUserTeamRole")]
        public async Task<IActionResult> Create([FromBody] UserTeamRole role, CancellationToken ct)
        {
            role.CreatedBy = User.GetId();
            var createdUserTeamRole = await _userUserTeamRoleService.CreateAsync(role, ct);
            return CreatedAtAction(nameof(this.Get), new { id = createdUserTeamRole.Id }, createdUserTeamRole);
        }

        /// <summary>
        /// Deletes a UserTeamRole
        /// </summary>
        /// <remarks>
        /// Deletes a UserTeamRole with the specified id
        /// <para />
        /// Accessible only to a SuperUser
        /// </remarks>
        /// <param name="id">The id of the UserTeamRole to delete</param>
        /// <param name="ct"></param>
        [HttpDelete("userteamroles/{id}")]
        [ProducesResponseType((int)HttpStatusCode.NoContent)]
        [SwaggerOperation(OperationId = "deleteUserTeamRole")]
        public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
        {
            await _userUserTeamRoleService.DeleteAsync(id, ct);
            return NoContent();
        }

    }
}

