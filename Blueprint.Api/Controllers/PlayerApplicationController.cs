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
    public class PlayerApplicationController : BaseController
    {
        private readonly IPlayerApplicationService _cardService;
        private readonly IAuthorizationService _authorizationService;

        public PlayerApplicationController(IPlayerApplicationService cardService, IAuthorizationService authorizationService)
        {
            _cardService = cardService;
            _authorizationService = authorizationService;
        }

        /// <summary>
        /// Gets PlayerApplications by msel
        /// </summary>
        /// <remarks>
        /// Returns a list of PlayerApplications for the msel.
        /// </remarks>
        /// <param name="mselId"></param>
        /// <param name="ct"></param>
        /// <returns></returns>
        [HttpGet("msels/{mselId}/cards")]
        [ProducesResponseType(typeof(IEnumerable<PlayerApplication>), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "getByMsel")]
        public async Task<IActionResult> GetByMsel(Guid mselId, CancellationToken ct)
        {
            var list = await _cardService.GetByMselAsync(mselId, ct);
            return Ok(list);
        }

        /// <summary>
        /// Gets a specific PlayerApplication by id
        /// </summary>
        /// <remarks>
        /// Returns the PlayerApplication with the id specified
        /// </remarks>
        /// <param name="id">The id of the PlayerApplication</param>
        /// <param name="ct"></param>
        /// <returns></returns>
        [HttpGet("cards/{id}")]
        [ProducesResponseType(typeof(PlayerApplication), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "getPlayerApplication")]
        public async Task<IActionResult> Get(Guid id, CancellationToken ct)
        {
            var card = await _cardService.GetAsync(id, ct);

            if (card == null)
                throw new EntityNotFoundException<PlayerApplication>();

            return Ok(card);
        }

        /// <summary>
        /// Creates a new PlayerApplication
        /// </summary>
        /// <remarks>
        /// Creates a new PlayerApplication with the attributes specified
        /// <para />
        /// Accessible only to a ContentDeveloper or an Administrator
        /// </remarks>
        /// <param name="card">The data used to create the PlayerApplication</param>
        /// <param name="ct"></param>
        [HttpPost("cards")]
        [ProducesResponseType(typeof(PlayerApplication), (int)HttpStatusCode.Created)]
        [SwaggerOperation(OperationId = "createPlayerApplication")]
        public async Task<IActionResult> Create([FromBody] PlayerApplication card, CancellationToken ct)
        {
            card.CreatedBy = User.GetId();
            var createdPlayerApplication = await _cardService.CreateAsync(card, ct);
            return CreatedAtAction(nameof(this.Get), new { id = createdPlayerApplication.Id }, createdPlayerApplication);
        }

        /// <summary>
        /// Updates a  PlayerApplication
        /// </summary>
        /// <remarks>
        /// Updates a PlayerApplication with the attributes specified.
        /// The ID from the route MUST MATCH the ID contained in the card parameter
        /// <para />
        /// Accessible only to a ContentDeveloper or an Administrator
        /// </remarks>
        /// <param name="id">The Id of the PlayerApplication to update</param>
        /// <param name="card">The updated PlayerApplication values</param>
        /// <param name="ct"></param>
        [HttpPut("cards/{id}")]
        [ProducesResponseType(typeof(PlayerApplication), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "updatePlayerApplication")]
        public async Task<IActionResult> Update([FromRoute] Guid id, [FromBody] PlayerApplication card, CancellationToken ct)
        {
            card.ModifiedBy = User.GetId();
            var updatedPlayerApplication = await _cardService.UpdateAsync(id, card, ct);
            return Ok(updatedPlayerApplication);
        }

        /// <summary>
        /// Deletes a  PlayerApplication
        /// </summary>
        /// <remarks>
        /// Deletes a  PlayerApplication with the specified id
        /// <para />
        /// Accessible only to a ContentDeveloper or an Administrator
        /// </remarks>
        /// <param name="id">The id of the PlayerApplication to delete</param>
        /// <param name="ct"></param>
        [HttpDelete("cards/{id}")]
        [ProducesResponseType((int)HttpStatusCode.NoContent)]
        [SwaggerOperation(OperationId = "deletePlayerApplication")]
        public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
        {
            await _cardService.DeleteAsync(id, ct);
            return NoContent();
        }

    }
}

