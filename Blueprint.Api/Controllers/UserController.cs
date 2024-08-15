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
    public class UserController : BaseController
    {
        private readonly IUserService _userService;
        private readonly IAuthorizationService _authorizationService;

        public UserController(IUserService userService, IAuthorizationService authorizationService)
        {
            _userService = userService;
            _authorizationService = authorizationService;
        }

        /// <summary>
        /// Gets all Users in the system
        /// </summary>
        /// <remarks>
        /// Returns a list of all of the Users in the system.
        /// <para />
        /// Only accessible to a SuperUser
        /// </remarks>
        /// <returns></returns>
        [HttpGet("users")]
        [ProducesResponseType(typeof(IEnumerable<User>), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "getUsers")]
        public async Task<IActionResult> Get(CancellationToken ct)
        {
            var list = await _userService.GetAsync(ct);
            return Ok(list);
        }

        /// <summary>
        /// Gets a specific User by id
        /// </summary>
        /// <remarks>
        /// Returns the User with the id specified
        /// <para />
        /// Only accessible to a SuperUser
        /// </remarks>
        /// <param name="id">The id of the User</param>
        /// <param name="ct"></param>
        /// <returns></returns>
        [HttpGet("users/{id}")]
        [ProducesResponseType(typeof(User), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "getUser")]
        public async Task<IActionResult> Get(Guid id, CancellationToken ct)
        {
            var user = await _userService.GetAsync(id, ct);

            if (user == null)
                throw new EntityNotFoundException<User>();

            return Ok(user);
        }

        /// <summary>
        /// Gets all MSEL Users
        /// </summary>
        /// <remarks>
        /// Returns a list of all of the MSEL Users.
        /// </remarks>
        /// <param name="mselId">The id of the MSEL</param>
        /// <param name="ct"></param>
        /// <returns></returns>
        [HttpGet("msels/{mselId}/users")]
        [ProducesResponseType(typeof(IEnumerable<User>), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "getMselUsers")]
        public async Task<IActionResult> GetByMsel(Guid mselId, CancellationToken ct)
        {
            var list = await _userService.GetByMselAsync(mselId, ct);
            return Ok(list);
        }

        /// <summary>
        /// Gets all Users for a team
        /// </summary>
        /// <remarks>
        /// Returns a list of all of the Users on the team.
        /// <para />
        /// Only accessible to a SuperUser
        /// </remarks>
        /// <param name="teamId">The id of the Team</param>
        /// <param name="ct"></param>
        /// <returns></returns>
        [HttpGet("teams/{teamId}/users")]
        [ProducesResponseType(typeof(IEnumerable<User>), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "getTeamUsers")]
        public async Task<IActionResult> GetByTeam(Guid teamId, CancellationToken ct)
        {
            var list = await _userService.GetByTeamAsync(teamId, ct);
            return Ok(list);
        }

        /// <summary>
        /// Gets all Users for a unit
        /// </summary>
        /// <remarks>
        /// Returns a list of all of the Users on the unit.
        /// <para />
        /// Only accessible to a SuperUser
        /// </remarks>
        /// <param name="unitId">The id of the Unit</param>
        /// <param name="ct"></param>
        /// <returns></returns>
        [HttpGet("units/{unitId}/users")]
        [ProducesResponseType(typeof(IEnumerable<User>), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "getUnitUsers")]
        public async Task<IActionResult> GetByUnit(Guid unitId, CancellationToken ct)
        {
            var list = await _userService.GetByUnitAsync(unitId, ct);
            return Ok(list);
        }

        /// <summary>
        /// Creates a new User
        /// </summary>
        /// <remarks>
        /// Creates a new User with the attributes specified
        /// <para />
        /// Accessible only to a SuperUser
        /// </remarks>
        /// <param name="user">The data to create the User with</param>
        /// <param name="ct"></param>
        [HttpPost("users")]
        [ProducesResponseType(typeof(User), (int)HttpStatusCode.Created)]
        [SwaggerOperation(OperationId = "createUser")]
        public async Task<IActionResult> Create([FromBody] User user, CancellationToken ct)
        {
            user.CreatedBy = User.GetId();
            var createdUser = await _userService.CreateAsync(user, ct);
            return CreatedAtAction(nameof(this.Get), new { id = createdUser.Id }, createdUser);
        }

        /// <summary>
        /// Updates a  User
        /// </summary>
        /// <remarks>
        /// Updates a User with the attributes specified.
        /// The ID from the route MUST MATCH the ID contained in the user parameter
        /// <para />
        /// Accessible only to a ContentDeveloper or an Administrator
        /// </remarks>
        /// <param name="id">The Id of the User to update</param>
        /// <param name="user">The updated User values</param>
        /// <param name="ct"></param>
        [HttpPut("users/{id}")]
        [ProducesResponseType(typeof(User), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "updateUser")]
        public async Task<IActionResult> Update([FromRoute] Guid id, [FromBody] User user, CancellationToken ct)
        {
            user.ModifiedBy = User.GetId();
            var updatedUser = await _userService.UpdateAsync(id, user, ct);
            return Ok(updatedUser);
        }

        /// <summary>
        /// Deletes a User
        /// </summary>
        /// <remarks>
        /// Deletes a User with the specified id
        /// <para />
        /// Accessible only to a SuperUser
        /// </remarks>
        /// <param name="id">The id of the User to delete</param>
        /// <param name="ct"></param>
        [HttpDelete("users/{id}")]
        [ProducesResponseType((int)HttpStatusCode.NoContent)]
        [SwaggerOperation(OperationId = "deleteUser")]
        public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
        {
            await _userService.DeleteAsync(id, ct);
            return NoContent();
        }


    }
}
