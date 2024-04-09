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
        private readonly IPlayerApplicationService _playerApplicationService;
        private readonly IAuthorizationService _authorizationService;

        public PlayerApplicationController(IPlayerApplicationService playerApplicationService, IAuthorizationService authorizationService)
        {
            _playerApplicationService = playerApplicationService;
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
        [HttpGet("msels/{mselId}/playerApplications")]
        [ProducesResponseType(typeof(IEnumerable<PlayerApplication>), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "getByMsel")]
        public async Task<IActionResult> GetByMsel(Guid mselId, CancellationToken ct)
        {
            var list = await _playerApplicationService.GetByMselAsync(mselId, ct);
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
        [HttpGet("playerApplications/{id}")]
        [ProducesResponseType(typeof(PlayerApplication), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "getPlayerApplication")]
        public async Task<IActionResult> Get(Guid id, CancellationToken ct)
        {
            var playerApplication = await _playerApplicationService.GetAsync(id, ct);

            if (playerApplication == null)
                throw new EntityNotFoundException<PlayerApplication>();

            return Ok(playerApplication);
        }

        /// <summary>
        /// Creates a new PlayerApplication
        /// </summary>
        /// <remarks>
        /// Creates a new PlayerApplication with the attributes specified
        /// <para />
        /// Accessible only to a ContentDeveloper or an Administrator
        /// </remarks>
        /// <param name="playerApplication">The data used to create the PlayerApplication</param>
        /// <param name="ct"></param>
        [HttpPost("playerApplications")]
        [ProducesResponseType(typeof(PlayerApplication), (int)HttpStatusCode.Created)]
        [SwaggerOperation(OperationId = "createPlayerApplication")]
        public async Task<IActionResult> Create([FromBody] PlayerApplication playerApplication, CancellationToken ct)
        {
            playerApplication.CreatedBy = User.GetId();
            var createdPlayerApplication = await _playerApplicationService.CreateAsync(playerApplication, ct);
            return CreatedAtAction(nameof(this.Get), new { id = createdPlayerApplication.Id }, createdPlayerApplication);
        }

        /// <summary>
        /// Creates a new PlayerApplication and pushes to a Player View
        /// </summary>
        /// <remarks>
        /// Creates a new PlayerApplication with the attributes specified
        /// <para />
        /// Accessible only to a ContentDeveloper or MSEL owner
        /// </remarks>
        /// <param name="playerApplication">The data used to create the PlayerApplication</param>
        /// <param name="ct"></param>
        [HttpPost("playerApplications/push")]
        [ProducesResponseType(typeof(PlayerApplication), (int)HttpStatusCode.Created)]
        [SwaggerOperation(OperationId = "createAndPushPlayerApplication")]
        public async Task<IActionResult> CreateAndPush([FromBody] PlayerApplication playerApplication, CancellationToken ct)
        {
            playerApplication.CreatedBy = User.GetId();
            var createdPlayerApplication = await _playerApplicationService.CreateAndPushAsync(playerApplication, ct);
            return CreatedAtAction(nameof(this.Get), new { id = createdPlayerApplication.Id }, createdPlayerApplication);
        }

        /// <summary>
        /// Updates a  PlayerApplication
        /// </summary>
        /// <remarks>
        /// Updates a PlayerApplication with the attributes specified.
        /// The ID from the route MUST MATCH the ID contained in the playerApplication parameter
        /// <para />
        /// Accessible only to a ContentDeveloper or an Administrator
        /// </remarks>
        /// <param name="id">The Id of the PlayerApplication to update</param>
        /// <param name="playerApplication">The updated PlayerApplication values</param>
        /// <param name="ct"></param>
        [HttpPut("playerApplications/{id}")]
        [ProducesResponseType(typeof(PlayerApplication), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "updatePlayerApplication")]
        public async Task<IActionResult> Update([FromRoute] Guid id, [FromBody] PlayerApplication playerApplication, CancellationToken ct)
        {
            playerApplication.ModifiedBy = User.GetId();
            var updatedPlayerApplication = await _playerApplicationService.UpdateAsync(id, playerApplication, ct);
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
        [HttpDelete("playerApplications/{id}")]
        [ProducesResponseType((int)HttpStatusCode.NoContent)]
        [SwaggerOperation(OperationId = "deletePlayerApplication")]
        public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
        {
            await _playerApplicationService.DeleteAsync(id, ct);
            return NoContent();
        }

    }
}

