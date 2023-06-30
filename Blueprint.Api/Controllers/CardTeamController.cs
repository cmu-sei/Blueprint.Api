// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
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
    public class CardTeamController : BaseController
    {
        private readonly ICardTeamService _cardTeamService;
        private readonly IAuthorizationService _authorizationService;

        public CardTeamController(ICardTeamService cardTeamService, IAuthorizationService authorizationService)
        {
            _cardTeamService = cardTeamService;
            _authorizationService = authorizationService;
        }

        /// <summary>
        /// Gets all CardTeams in the system
        /// </summary>
        /// <remarks>
        /// Returns a list of all of the CardTeams in the system.
        /// <para />
        /// </remarks>
        /// <returns></returns>
        [HttpGet("teamcards")]
        [ProducesResponseType(typeof(IEnumerable<CardTeam>), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "getCardTeams")]
        public async Task<IActionResult> Get(CancellationToken ct)
        {
            var list = await _cardTeamService.GetAsync(ct);
            return Ok(list);
        }

        /// <summary>
        /// Gets all CardTeams for a MSEL
        /// </summary>
        /// <remarks>
        /// Returns a list of all of the CardTeams for the msel.
        /// </remarks>
        /// <param name="mselId">The id of the Msel</param>
        /// <param name="ct"></param>
        /// <returns></returns>
        [HttpGet("msels/{mselId}/teamcards")]
        [ProducesResponseType(typeof(IEnumerable<CardTeam>), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "getMselCardTeams")]
        public async Task<IActionResult> GetByMsel(Guid mselId, CancellationToken ct)
        {
            var list = await _cardTeamService.GetByMselAsync(mselId, ct);
            return Ok(list);
        }

        /// <summary>
        /// Gets all CardTeams for a card
        /// </summary>
        /// <remarks>
        /// Returns a list of all of the CardTeams for the card.
        /// </remarks>
        /// <param name="cardId">The id of the CardTeam</param>
        /// <param name="ct"></param>
        /// <returns></returns>
        [HttpGet("cards/{cardId}/teamcards")]
        [ProducesResponseType(typeof(IEnumerable<CardTeam>), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "getCardCardTeams")]
        public async Task<IActionResult> GetByCard(Guid cardId, CancellationToken ct)
        {
            var list = await _cardTeamService.GetByCardAsync(cardId, ct);
            return Ok(list);
        }

        /// <summary>
        /// Gets a specific CardTeam by id
        /// </summary>
        /// <remarks>
        /// Returns the CardTeam with the id specified
        /// <para />
        /// </remarks>
        /// <param name="id">The id of the CardTeam</param>
        /// <param name="ct"></param>
        /// <returns></returns>
        [HttpGet("teamcards/{id}")]
        [ProducesResponseType(typeof(CardTeam), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "getCardTeam")]
        public async Task<IActionResult> Get(Guid id, CancellationToken ct)
        {
            var team = await _cardTeamService.GetAsync(id, ct);

            if (team == null)
                throw new EntityNotFoundException<CardTeam>();

            return Ok(team);
        }

        /// <summary>
        /// Creates a new CardTeam
        /// </summary>
        /// <remarks>
        /// Creates a new CardTeam with the attributes specified
        /// <para />
        /// </remarks>
        /// <param name="cardTeam">The data to create the CardTeam with</param>
        /// <param name="ct"></param>
        [HttpPost("teamcards")]
        [ProducesResponseType(typeof(CardTeam), (int)HttpStatusCode.Created)]
        [SwaggerOperation(OperationId = "createCardTeam")]
        public async Task<IActionResult> Create([FromBody] CardTeam cardTeam, CancellationToken ct)
        {
            var createdCardTeam = await _cardTeamService.CreateAsync(cardTeam, ct);
            return CreatedAtAction(nameof(this.Get), new { id = createdCardTeam.Id }, createdCardTeam);
        }

        /// <summary>
        /// Updates a  CardTeam
        /// </summary>
        /// <remarks>
        /// Updates a CardTeam with the attributes specified.
        /// The ID from the route MUST MATCH the ID contained in the cardTeam parameter
        /// <para />
        /// Accessible only to a ContentDeveloper or an Administrator
        /// </remarks>
        /// <param name="id">The Id of the CardTeam to update</param>
        /// <param name="cardTeam">The updated CardTeam values</param>
        /// <param name="ct"></param>
        [HttpPut("cardteams/{id}")]
        [ProducesResponseType(typeof(CardTeam), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "updateCardTeam")]
        public async Task<IActionResult> Update([FromRoute] Guid id, [FromBody] CardTeam cardTeam, CancellationToken ct)
        {
            var updatedCardTeam = await _cardTeamService.UpdateAsync(id, cardTeam, ct);
            return Ok(updatedCardTeam);
        }

        /// <summary>
        /// Deletes a CardTeam
        /// </summary>
        /// <remarks>
        /// Deletes a CardTeam with the specified id
        /// <para />
        /// </remarks>
        /// <param name="id">The id of the CardTeam to delete</param>
        /// <param name="ct"></param>
        [HttpDelete("teamcards/{id}")]
        [ProducesResponseType((int)HttpStatusCode.NoContent)]
        [SwaggerOperation(OperationId = "deleteCardTeam")]
        public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
        {
            await _cardTeamService.DeleteAsync(id, ct);
            return NoContent();
        }

        /// <summary>
        /// Deletes a CardTeam by card ID and team ID
        /// </summary>
        /// <remarks>
        /// Deletes a CardTeam with the specified card ID and team ID
        /// <para />
        /// </remarks>
        /// <param name="cardId">ID of a card.</param>
        /// <param name="teamId">ID of a team.</param>
        /// <param name="ct"></param>
        [HttpDelete("teams/{teamId}/cards/{cardId}")]
        [ProducesResponseType((int)HttpStatusCode.NoContent)]
        [SwaggerOperation(OperationId = "deleteCardTeamByIds")]
        public async Task<IActionResult> Delete(Guid teamId, Guid cardId, CancellationToken ct)
        {
            await _cardTeamService.DeleteByIdsAsync(teamId, cardId, ct);
            return NoContent();
        }

    }
}

