// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Blueprint.Api.Data.Enumerations;
using Blueprint.Api.Infrastructure.Authorization;
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
        private readonly IBlueprintAuthorizationService _authorizationService;

        public TeamUserController(ITeamUserService teamUserService, IBlueprintAuthorizationService authorizationService)
        {
            _teamUserService = teamUserService;
            _authorizationService = authorizationService;
        }

        /// <summary>
        /// Gets TeamUsers for the specified msel
        /// </summary>
        /// <remarks>
        /// Returns a list of the specified msel's TeamUsers.
        /// <para />
        /// Only accessible to an msel user
        /// </remarks>
        /// <returns></returns>
        [HttpGet("msels/{mselId}/teamusers")]
        [ProducesResponseType(typeof(IEnumerable<TeamUser>), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "getMselTeamUsers")]
        public async Task<IActionResult> GetByMsel([FromRoute] Guid mselId, CancellationToken ct)
        {
            var hasSystemPermission = await _authorizationService.AuthorizeAsync([SystemPermission.ViewMsels], ct);
            var list = await _teamUserService.GetByMselAsync(mselId, hasSystemPermission, ct);
            return Ok(list);
        }

        /// <summary>
        /// Gets TeamUsers for the specified team
        /// </summary>
        /// <remarks>
        /// Returns a list of the specified team's TeamUsers.
        /// <para />
        /// Only accessible to an msel user
        /// </remarks>
        /// <returns></returns>
        [HttpGet("teams/{teamId}/teamusers")]
        [ProducesResponseType(typeof(IEnumerable<TeamUser>), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "getTeamTeamUsers")]
        public async Task<IActionResult> GetByTeam([FromRoute] Guid teamId, CancellationToken ct)
        {
            var hasSystemPermission = await _authorizationService.AuthorizeAsync([SystemPermission.ViewMsels], ct);
            var list = await _teamUserService.GetByTeamAsync(teamId, hasSystemPermission, ct);
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
            var hasSystemPermission = await _authorizationService.AuthorizeAsync([SystemPermission.ViewMsels], ct);
            var team = await _teamUserService.GetAsync(id, hasSystemPermission, ct);

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
        /// Accessible to a SuperUser or a MSEL Owner
        /// </remarks>
        /// <param name="team">The data to create the TeamUser with</param>
        /// <param name="ct"></param>
        [HttpPost("teamusers")]
        [ProducesResponseType(typeof(TeamUser), (int)HttpStatusCode.Created)]
        [SwaggerOperation(OperationId = "createTeamUser")]
        public async Task<IActionResult> Create([FromBody] TeamUser team, CancellationToken ct)
        {
            var hasSystemPermission = await _authorizationService.AuthorizeAsync([SystemPermission.ManageUsers], ct);
            team.CreatedBy = User.GetId();
            var createdTeamUser = await _teamUserService.CreateAsync(team, hasSystemPermission, ct);
            return CreatedAtAction(nameof(this.Get), new { id = createdTeamUser.Id }, createdTeamUser);
        }

        /// <summary>
        /// Deletes a TeamUser
        /// </summary>
        /// <remarks>
        /// Deletes a TeamUser with the specified id
        /// <para />
        /// Accessible to a SuperUser or a MSEL Owner
        /// </remarks>
        /// <param name="id">The id of the TeamUser to delete</param>
        /// <param name="ct"></param>
        [HttpDelete("teamusers/{id}")]
        [ProducesResponseType((int)HttpStatusCode.NoContent)]
        [SwaggerOperation(OperationId = "deleteTeamUser")]
        public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
        {
            var hasSystemPermission = await _authorizationService.AuthorizeAsync([SystemPermission.ManageUsers], ct);
            await _teamUserService.DeleteAsync(id, hasSystemPermission, ct);
            return NoContent();
        }

        /// <summary>
        /// Deletes a TeamUser by user ID and team ID
        /// </summary>
        /// <remarks>
        /// Deletes a TeamUser with the specified user ID and team ID
        /// <para />
        /// Accessible to a SuperUser or a MSEL Owner
        /// </remarks>
        /// <param name="userId">ID of a user.</param>
        /// <param name="teamId">ID of a team.</param>
        /// <param name="ct"></param>
        [HttpDelete("teams/{teamId}/users/{userId}")]
        [ProducesResponseType((int)HttpStatusCode.NoContent)]
        [SwaggerOperation(OperationId = "deleteTeamUserByIds")]
        public async Task<IActionResult> Delete(Guid teamId, Guid userId, CancellationToken ct)
        {
            var hasSystemPermission = await _authorizationService.AuthorizeAsync([SystemPermission.ManageUsers], ct);
            await _teamUserService.DeleteByIdsAsync(teamId, userId, hasSystemPermission, ct);
            return NoContent();
        }

    }
}

