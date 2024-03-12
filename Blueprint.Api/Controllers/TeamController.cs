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
    public class TeamController : BaseController
    {
        private readonly ITeamService _teamService;
        private readonly IAuthorizationService _authorizationService;

        public TeamController(ITeamService teamService, IAuthorizationService authorizationService)
        {
            _teamService = teamService;
            _authorizationService = authorizationService;
        }

        /// <summary>
        /// Gets all Team in the system
        /// </summary>
        /// <remarks>
        /// Returns a list of all of the Teams in the system.
        /// <para />
        /// Only accessible to a SuperUser
        /// </remarks>
        /// <returns></returns>
        [HttpGet("teams")]
        [ProducesResponseType(typeof(IEnumerable<Team>), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "getTeams")]
        public async Task<IActionResult> Get(CancellationToken ct)
        {
            var list = await _teamService.GetAsync(ct);
            return Ok(list);
        }

        /// <summary>
        /// Gets Teams for the current user
        /// </summary>
        /// <remarks>
        /// Returns a list of the current user's Teams.
        /// </remarks>
        /// <returns></returns>
        [HttpGet("my-teams")]
        [ProducesResponseType(typeof(IEnumerable<Team>), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "getMyTeams")]
        public async Task<IActionResult> GetMine(CancellationToken ct)
        {
            var list = await _teamService.GetMineAsync(ct);
            return Ok(list);
        }

        /// <summary>
        /// Gets Teams for the specified MSEL
        /// </summary>
        /// <remarks>
        /// Returns a list of the Teams for the specified MSEL.
        /// <para />
        /// </remarks>
        /// <returns></returns>
        [HttpGet("msels/{mselId}/teams")]
        [ProducesResponseType(typeof(IEnumerable<Team>), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "getTeamsByMsel")]
        public async Task<IActionResult> GetByMsel([FromRoute] Guid mselId, CancellationToken ct)
        {
            var list = await _teamService.GetByMselAsync(mselId, ct);
            return Ok(list);
        }

        /// <summary>
        /// Gets Teams for the specified user
        /// </summary>
        /// <remarks>
        /// Returns a list of the specified user's Teams.
        /// <para />
        /// Only accessible to a SuperUser
        /// </remarks>
        /// <returns></returns>
        [HttpGet("users/{userId}/teams")]
        [ProducesResponseType(typeof(IEnumerable<Team>), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "getTeamsByUser")]
        public async Task<IActionResult> GetByUser([FromRoute] Guid userId, CancellationToken ct)
        {
            var list = await _teamService.GetByUserAsync(userId, ct);
            return Ok(list);
        }

        /// <summary>
        /// Gets a specific Team by id
        /// </summary>
        /// <remarks>
        /// Returns the Team with the id specified
        /// <para />
        /// Accessible to a SuperUser or a User that is a member of a Team within the specified Team
        /// </remarks>
        /// <param name="id">The id of the Team</param>
        /// <param name="ct"></param>
        /// <returns></returns>
        [HttpGet("teams/{id}")]
        [ProducesResponseType(typeof(Team), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "getTeam")]
        public async Task<IActionResult> Get(Guid id, CancellationToken ct)
        {
            var team = await _teamService.GetAsync(id, ct);

            if (team == null)
                throw new EntityNotFoundException<Team>();

            return Ok(team);
        }

        /// <summary>
        /// Creates a new Team
        /// </summary>
        /// <remarks>
        /// Creates a new Team with the attributes specified
        /// <para />
        /// Accessible only to a SuperUser or an Administrator
        /// </remarks>
        /// <param name="team">The data to create the Team with</param>
        /// <param name="ct"></param>
        [HttpPost("teams")]
        [ProducesResponseType(typeof(Team), (int)HttpStatusCode.Created)]
        [SwaggerOperation(OperationId = "createTeam")]
        public async Task<IActionResult> Create([FromBody] Team team, CancellationToken ct)
        {
            team.CreatedBy = User.GetId();
            var createdTeam = await _teamService.CreateAsync(team, ct);
            return CreatedAtAction(nameof(this.Get), new { id = createdTeam.Id }, createdTeam);
        }

        /// <summary>
        /// Updates a Team
        /// </summary>
        /// <remarks>
        /// Updates a Team with the attributes specified
        /// <para />
        /// Accessible only to a SuperUser or a User on an Admin Team within the specified Team
        /// </remarks>
        /// <param name="id">The Id of the Exericse to update</param>
        /// <param name="team">The updated Team values</param>
        /// <param name="ct"></param>
        [HttpPut("teams/{id}")]
        [ProducesResponseType(typeof(Team), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "updateTeam")]
        public async Task<IActionResult> Update([FromRoute] Guid id, [FromBody] Team team, CancellationToken ct)
        {
            team.ModifiedBy = User.GetId();
            var updatedTeam = await _teamService.UpdateAsync(id, team, ct);
            return Ok(updatedTeam);
        }

        /// <summary>
        /// Deletes a Team
        /// </summary>
        /// <remarks>
        /// Deletes a Team with the specified id
        /// <para />
        /// Accessible only to a SuperUser or a User on an Admin Team within the specified Team
        /// </remarks>
        /// <param name="id">The id of the Team to delete</param>
        /// <param name="ct"></param>
        [HttpDelete("teams/{id}")]
        [ProducesResponseType((int)HttpStatusCode.NoContent)]
        [SwaggerOperation(OperationId = "deleteTeam")]
        public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
        {
            await _teamService.DeleteAsync(id, ct);
            return NoContent();
        }

    }
}
