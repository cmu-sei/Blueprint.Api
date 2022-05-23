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
    public class TeamUserController : BaseController
    {
        private readonly ITeamUserService _teamUserService;
        private readonly IAuthorizationService _authorizationService;

        public TeamUserController(ITeamUserService teamUserService, IAuthorizationService authorizationService)
        {
            _teamUserService = teamUserService;
            _authorizationService = authorizationService;
        }

        /// <summary>
        /// Gets all TeamUsers in the system
        /// </summary>
        /// <remarks>
        /// Returns a list of all of the TeamUsers in the system.
        /// <para />
        /// Only accessible to a SuperUser
        /// </remarks>
        /// <returns></returns>
        [HttpGet("teamusers")]
        [ProducesResponseType(typeof(IEnumerable<TeamUser>), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "getTeamUsers")]
        public async Task<IActionResult> Get(CancellationToken ct)
        {
            var list = await _teamUserService.GetAsync(ct);
            return Ok(list);
        }

        /// <summary>
        /// Gets a specific TeamUser by id
        /// </summary>
        /// <remarks>
        /// Returns the TeamUser with the id specified
        /// <para />
        /// Only accessible to a SuperUser
        /// </remarks>
        /// <param name="id">The id of the TeamUser</param>
        /// <param name="ct"></param>
        /// <returns></returns>
        [HttpGet("teamusers/{id}")]
        [ProducesResponseType(typeof(TeamUser), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "getTeamUser")]
        public async Task<IActionResult> Get(Guid id, CancellationToken ct)
        {
            var team = await _teamUserService.GetAsync(id, ct);

            if (team == null)
                throw new EntityNotFoundException<TeamUser>();

            return Ok(team);
        }

        /// <summary>
        /// Creates a new TeamUser
        /// </summary>
        /// <remarks>
        /// Creates a new TeamUser with the attributes specified
        /// <para />
        /// Accessible only to a SuperUser
        /// </remarks>
        /// <param name="team">The data to create the TeamUser with</param>
        /// <param name="ct"></param>
        [HttpPost("teamusers")]
        [ProducesResponseType(typeof(TeamUser), (int)HttpStatusCode.Created)]
        [SwaggerOperation(OperationId = "createTeamUser")]
        public async Task<IActionResult> Create([FromBody] TeamUser team, CancellationToken ct)
        {
            team.CreatedBy = User.GetId();
            var createdTeamUser = await _teamUserService.CreateAsync(team, ct);
            return CreatedAtAction(nameof(this.Get), new { id = createdTeamUser.Id }, createdTeamUser);
        }

        /// <summary>
        /// Deletes a TeamUser
        /// </summary>
        /// <remarks>
        /// Deletes a TeamUser with the specified id
        /// <para />
        /// Accessible only to a SuperUser
        /// </remarks>
        /// <param name="id">The id of the TeamUser to delete</param>
        /// <param name="ct"></param>
        [HttpDelete("teamusers/{id}")]
        [ProducesResponseType((int)HttpStatusCode.NoContent)]
        [SwaggerOperation(OperationId = "deleteTeamUser")]
        public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
        {
            await _teamUserService.DeleteAsync(id, ct);
            return NoContent();
        }

        /// <summary>
        /// Deletes a TeamUser by user ID and team ID
        /// </summary>
        /// <remarks>
        /// Deletes a TeamUser with the specified user ID and team ID
        /// <para />
        /// Accessible only to a SuperUser
        /// </remarks>
        /// <param name="userId">ID of a user.</param>
        /// <param name="teamId">ID of a team.</param>
        /// <param name="ct"></param>
        [HttpDelete("teams/{teamId}/users/{userId}")]
        [ProducesResponseType((int)HttpStatusCode.NoContent)]
        [SwaggerOperation(OperationId = "deleteTeamUserByIds")]
        public async Task<IActionResult> Delete(Guid teamId, Guid userId, CancellationToken ct)
        {
            await _teamUserService.DeleteByIdsAsync(teamId, userId, ct);
            return NoContent();
        }

    }
}

