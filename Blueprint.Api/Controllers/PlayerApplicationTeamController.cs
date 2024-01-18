// Copyright 2024 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

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
    public class PlayerApplicationTeamController : BaseController
    {
        private readonly IPlayerApplicationTeamService _playerApplicationTeamService;
        private readonly IAuthorizationService _authorizationService;

        public PlayerApplicationTeamController(IPlayerApplicationTeamService playerApplicationTeamService, IAuthorizationService authorizationService)
        {
            _playerApplicationTeamService = playerApplicationTeamService;
            _authorizationService = authorizationService;
        }

        /// <summary>
        /// Gets all PlayerApplicationTeams in the system
        /// </summary>
        /// <remarks>
        /// Returns a list of all of the PlayerApplicationTeams in the system.
        /// <para />
        /// </remarks>
        /// <returns></returns>
        [HttpGet("teamplayerApplications")]
        [ProducesResponseType(typeof(IEnumerable<PlayerApplicationTeam>), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "getPlayerApplicationTeams")]
        public async Task<IActionResult> Get(CancellationToken ct)
        {
            var list = await _playerApplicationTeamService.GetAsync(ct);
            return Ok(list);
        }

        /// <summary>
        /// Gets all PlayerApplicationTeams for a MSEL
        /// </summary>
        /// <remarks>
        /// Returns a list of all of the PlayerApplicationTeams for the msel.
        /// </remarks>
        /// <param name="mselId">The id of the Msel</param>
        /// <param name="ct"></param>
        /// <returns></returns>
        [HttpGet("msels/{mselId}/teamplayerApplications")]
        [ProducesResponseType(typeof(IEnumerable<PlayerApplicationTeam>), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "getMselPlayerApplicationTeams")]
        public async Task<IActionResult> GetByMsel(Guid mselId, CancellationToken ct)
        {
            var list = await _playerApplicationTeamService.GetByMselAsync(mselId, ct);
            return Ok(list);
        }

        /// <summary>
        /// Gets all PlayerApplicationTeams for a playerApplication
        /// </summary>
        /// <remarks>
        /// Returns a list of all of the PlayerApplicationTeams for the playerApplication.
        /// </remarks>
        /// <param name="playerApplicationId">The id of the PlayerApplicationTeam</param>
        /// <param name="ct"></param>
        /// <returns></returns>
        [HttpGet("playerApplications/{playerApplicationId}/teamplayerApplications")]
        [ProducesResponseType(typeof(IEnumerable<PlayerApplicationTeam>), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "getPlayerApplicationPlayerApplicationTeams")]
        public async Task<IActionResult> GetByPlayerApplication(Guid playerApplicationId, CancellationToken ct)
        {
            var list = await _playerApplicationTeamService.GetByPlayerApplicationAsync(playerApplicationId, ct);
            return Ok(list);
        }

        /// <summary>
        /// Gets a specific PlayerApplicationTeam by id
        /// </summary>
        /// <remarks>
        /// Returns the PlayerApplicationTeam with the id specified
        /// <para />
        /// </remarks>
        /// <param name="id">The id of the PlayerApplicationTeam</param>
        /// <param name="ct"></param>
        /// <returns></returns>
        [HttpGet("teamplayerApplications/{id}")]
        [ProducesResponseType(typeof(PlayerApplicationTeam), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "getPlayerApplicationTeam")]
        public async Task<IActionResult> Get(Guid id, CancellationToken ct)
        {
            var team = await _playerApplicationTeamService.GetAsync(id, ct);

            if (team == null)
                throw new EntityNotFoundException<PlayerApplicationTeam>();

            return Ok(team);
        }

        /// <summary>
        /// Creates a new PlayerApplicationTeam
        /// </summary>
        /// <remarks>
        /// Creates a new PlayerApplicationTeam with the attributes specified
        /// <para />
        /// </remarks>
        /// <param name="playerApplicationTeam">The data to create the PlayerApplicationTeam with</param>
        /// <param name="ct"></param>
        [HttpPost("teamplayerApplications")]
        [ProducesResponseType(typeof(PlayerApplicationTeam), (int)HttpStatusCode.Created)]
        [SwaggerOperation(OperationId = "createPlayerApplicationTeam")]
        public async Task<IActionResult> Create([FromBody] PlayerApplicationTeam playerApplicationTeam, CancellationToken ct)
        {
            var createdPlayerApplicationTeam = await _playerApplicationTeamService.CreateAsync(playerApplicationTeam, ct);
            return CreatedAtAction(nameof(this.Get), new { id = createdPlayerApplicationTeam.Id }, createdPlayerApplicationTeam);
        }

        /// <summary>
        /// Updates a  PlayerApplicationTeam
        /// </summary>
        /// <remarks>
        /// Updates a PlayerApplicationTeam with the attributes specified.
        /// The ID from the route MUST MATCH the ID contained in the playerApplicationTeam parameter
        /// <para />
        /// Accessible only to a ContentDeveloper or an Administrator
        /// </remarks>
        /// <param name="id">The Id of the PlayerApplicationTeam to update</param>
        /// <param name="playerApplicationTeam">The updated PlayerApplicationTeam values</param>
        /// <param name="ct"></param>
        [HttpPut("playerApplicationteams/{id}")]
        [ProducesResponseType(typeof(PlayerApplicationTeam), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "updatePlayerApplicationTeam")]
        public async Task<IActionResult> Update([FromRoute] Guid id, [FromBody] PlayerApplicationTeam playerApplicationTeam, CancellationToken ct)
        {
            var updatedPlayerApplicationTeam = await _playerApplicationTeamService.UpdateAsync(id, playerApplicationTeam, ct);
            return Ok(updatedPlayerApplicationTeam);
        }

        /// <summary>
        /// Deletes a PlayerApplicationTeam
        /// </summary>
        /// <remarks>
        /// Deletes a PlayerApplicationTeam with the specified id
        /// <para />
        /// </remarks>
        /// <param name="id">The id of the PlayerApplicationTeam to delete</param>
        /// <param name="ct"></param>
        [HttpDelete("teamplayerApplications/{id}")]
        [ProducesResponseType((int)HttpStatusCode.NoContent)]
        [SwaggerOperation(OperationId = "deletePlayerApplicationTeam")]
        public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
        {
            await _playerApplicationTeamService.DeleteAsync(id, ct);
            return NoContent();
        }

        /// <summary>
        /// Deletes a PlayerApplicationTeam by playerApplication ID and team ID
        /// </summary>
        /// <remarks>
        /// Deletes a PlayerApplicationTeam with the specified playerApplication ID and team ID
        /// <para />
        /// </remarks>
        /// <param name="playerApplicationId">ID of a playerApplication.</param>
        /// <param name="teamId">ID of a team.</param>
        /// <param name="ct"></param>
        [HttpDelete("teams/{teamId}/playerApplications/{playerApplicationId}")]
        [ProducesResponseType((int)HttpStatusCode.NoContent)]
        [SwaggerOperation(OperationId = "deletePlayerApplicationTeamByIds")]
        public async Task<IActionResult> Delete(Guid teamId, Guid playerApplicationId, CancellationToken ct)
        {
            await _playerApplicationTeamService.DeleteByIdsAsync(teamId, playerApplicationId, ct);
            return NoContent();
        }

    }
}

