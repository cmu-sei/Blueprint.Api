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
        private readonly IPlayerApplicationTeamService _cardTeamService;
        private readonly IAuthorizationService _authorizationService;

        public PlayerApplicationTeamController(IPlayerApplicationTeamService cardTeamService, IAuthorizationService authorizationService)
        {
            _cardTeamService = cardTeamService;
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
        [HttpGet("teamcards")]
        [ProducesResponseType(typeof(IEnumerable<PlayerApplicationTeam>), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "getPlayerApplicationTeams")]
        public async Task<IActionResult> Get(CancellationToken ct)
        {
            var list = await _cardTeamService.GetAsync(ct);
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
        [HttpGet("msels/{mselId}/teamcards")]
        [ProducesResponseType(typeof(IEnumerable<PlayerApplicationTeam>), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "getMselPlayerApplicationTeams")]
        public async Task<IActionResult> GetByMsel(Guid mselId, CancellationToken ct)
        {
            var list = await _cardTeamService.GetByMselAsync(mselId, ct);
            return Ok(list);
        }

        /// <summary>
        /// Gets all PlayerApplicationTeams for a card
        /// </summary>
        /// <remarks>
        /// Returns a list of all of the PlayerApplicationTeams for the card.
        /// </remarks>
        /// <param name="cardId">The id of the PlayerApplicationTeam</param>
        /// <param name="ct"></param>
        /// <returns></returns>
        [HttpGet("cards/{cardId}/teamcards")]
        [ProducesResponseType(typeof(IEnumerable<PlayerApplicationTeam>), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "getPlayerApplicationPlayerApplicationTeams")]
        public async Task<IActionResult> GetByPlayerApplication(Guid cardId, CancellationToken ct)
        {
            var list = await _cardTeamService.GetByPlayerApplicationAsync(cardId, ct);
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
        [HttpGet("teamcards/{id}")]
        [ProducesResponseType(typeof(PlayerApplicationTeam), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "getPlayerApplicationTeam")]
        public async Task<IActionResult> Get(Guid id, CancellationToken ct)
        {
            var team = await _cardTeamService.GetAsync(id, ct);

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
        /// <param name="cardTeam">The data to create the PlayerApplicationTeam with</param>
        /// <param name="ct"></param>
        [HttpPost("teamcards")]
        [ProducesResponseType(typeof(PlayerApplicationTeam), (int)HttpStatusCode.Created)]
        [SwaggerOperation(OperationId = "createPlayerApplicationTeam")]
        public async Task<IActionResult> Create([FromBody] PlayerApplicationTeam cardTeam, CancellationToken ct)
        {
            var createdPlayerApplicationTeam = await _cardTeamService.CreateAsync(cardTeam, ct);
            return CreatedAtAction(nameof(this.Get), new { id = createdPlayerApplicationTeam.Id }, createdPlayerApplicationTeam);
        }

        /// <summary>
        /// Updates a  PlayerApplicationTeam
        /// </summary>
        /// <remarks>
        /// Updates a PlayerApplicationTeam with the attributes specified.
        /// The ID from the route MUST MATCH the ID contained in the cardTeam parameter
        /// <para />
        /// Accessible only to a ContentDeveloper or an Administrator
        /// </remarks>
        /// <param name="id">The Id of the PlayerApplicationTeam to update</param>
        /// <param name="cardTeam">The updated PlayerApplicationTeam values</param>
        /// <param name="ct"></param>
        [HttpPut("cardteams/{id}")]
        [ProducesResponseType(typeof(PlayerApplicationTeam), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "updatePlayerApplicationTeam")]
        public async Task<IActionResult> Update([FromRoute] Guid id, [FromBody] PlayerApplicationTeam cardTeam, CancellationToken ct)
        {
            var updatedPlayerApplicationTeam = await _cardTeamService.UpdateAsync(id, cardTeam, ct);
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
        [HttpDelete("teamcards/{id}")]
        [ProducesResponseType((int)HttpStatusCode.NoContent)]
        [SwaggerOperation(OperationId = "deletePlayerApplicationTeam")]
        public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
        {
            await _cardTeamService.DeleteAsync(id, ct);
            return NoContent();
        }

        /// <summary>
        /// Deletes a PlayerApplicationTeam by card ID and team ID
        /// </summary>
        /// <remarks>
        /// Deletes a PlayerApplicationTeam with the specified card ID and team ID
        /// <para />
        /// </remarks>
        /// <param name="cardId">ID of a card.</param>
        /// <param name="teamId">ID of a team.</param>
        /// <param name="ct"></param>
        [HttpDelete("teams/{teamId}/cards/{cardId}")]
        [ProducesResponseType((int)HttpStatusCode.NoContent)]
        [SwaggerOperation(OperationId = "deletePlayerApplicationTeamByIds")]
        public async Task<IActionResult> Delete(Guid teamId, Guid cardId, CancellationToken ct)
        {
            await _cardTeamService.DeleteByIdsAsync(teamId, cardId, ct);
            return NoContent();
        }

    }
}

