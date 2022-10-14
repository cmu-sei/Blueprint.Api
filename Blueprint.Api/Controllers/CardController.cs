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
using Blueprint.Api.Infrastructure.QueryParameters;
using Blueprint.Api.Services;
using Blueprint.Api.ViewModels;
using Swashbuckle.AspNetCore.Annotations;

namespace Blueprint.Api.Controllers
{
    public class CardController : BaseController
    {
        private readonly ICardService _cardService;
        private readonly IAuthorizationService _authorizationService;

        public CardController(ICardService cardService, IAuthorizationService authorizationService)
        {
            _cardService = cardService;
            _authorizationService = authorizationService;
        }

        /// <summary>
        /// Gets Cards by msel
        /// </summary>
        /// <remarks>
        /// Returns a list of Cards for the msel.
        /// </remarks>
        /// <param name="mselId"></param>
        /// <param name="ct"></param>
        /// <returns></returns>
        [HttpGet("msels/{mselId}/cards")]
        [ProducesResponseType(typeof(IEnumerable<Card>), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "getByMsel")]
        public async Task<IActionResult> GetByMsel(Guid mselId, CancellationToken ct)
        {
            var list = await _cardService.GetByMselAsync(mselId, ct);
            return Ok(list);
        }

        /// <summary>
        /// Gets a specific Card by id
        /// </summary>
        /// <remarks>
        /// Returns the Card with the id specified
        /// </remarks>
        /// <param name="id">The id of the Card</param>
        /// <param name="ct"></param>
        /// <returns></returns>
        [HttpGet("cards/{id}")]
        [ProducesResponseType(typeof(Card), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "getCard")]
        public async Task<IActionResult> Get(Guid id, CancellationToken ct)
        {
            var card = await _cardService.GetAsync(id, ct);

            if (card == null)
                throw new EntityNotFoundException<Card>();

            return Ok(card);
        }

        /// <summary>
        /// Creates a new Card
        /// </summary>
        /// <remarks>
        /// Creates a new Card with the attributes specified
        /// <para />
        /// Accessible only to a ContentDeveloper or an Administrator
        /// </remarks>
        /// <param name="card">The data used to create the Card</param>
        /// <param name="ct"></param>
        [HttpPost("cards")]
        [ProducesResponseType(typeof(Card), (int)HttpStatusCode.Created)]
        [SwaggerOperation(OperationId = "createCard")]
        public async Task<IActionResult> Create([FromBody] Card card, CancellationToken ct)
        {
            card.CreatedBy = User.GetId();
            var createdCard = await _cardService.CreateAsync(card, ct);
            return CreatedAtAction(nameof(this.Get), new { id = createdCard.Id }, createdCard);
        }

        /// <summary>
        /// Updates a  Card
        /// </summary>
        /// <remarks>
        /// Updates a Card with the attributes specified.
        /// The ID from the route MUST MATCH the ID contained in the card parameter
        /// <para />
        /// Accessible only to a ContentDeveloper or an Administrator
        /// </remarks>
        /// <param name="id">The Id of the Card to update</param>
        /// <param name="card">The updated Card values</param>
        /// <param name="ct"></param>
        [HttpPut("cards/{id}")]
        [ProducesResponseType(typeof(Card), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "updateCard")]
        public async Task<IActionResult> Update([FromRoute] Guid id, [FromBody] Card card, CancellationToken ct)
        {
            card.ModifiedBy = User.GetId();
            var updatedCard = await _cardService.UpdateAsync(id, card, ct);
            return Ok(updatedCard);
        }

        /// <summary>
        /// Deletes a  Card
        /// </summary>
        /// <remarks>
        /// Deletes a  Card with the specified id
        /// <para />
        /// Accessible only to a ContentDeveloper or an Administrator
        /// </remarks>
        /// <param name="id">The id of the Card to delete</param>
        /// <param name="ct"></param>
        [HttpDelete("cards/{id}")]
        [ProducesResponseType((int)HttpStatusCode.NoContent)]
        [SwaggerOperation(OperationId = "deleteCard")]
        public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
        {
            await _cardService.DeleteAsync(id, ct);
            return NoContent();
        }

    }
}

