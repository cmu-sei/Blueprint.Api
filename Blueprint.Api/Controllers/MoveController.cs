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
    public class MoveController : BaseController
    {
        private readonly IMoveService _moveService;
        private readonly IAuthorizationService _authorizationService;

        public MoveController(IMoveService moveService, IAuthorizationService authorizationService)
        {
            _moveService = moveService;
            _authorizationService = authorizationService;
        }

        /// <summary>
        /// Gets Moves by msel
        /// </summary>
        /// <remarks>
        /// Returns a list of Moves for the msel.
        /// </remarks>
        /// <param name="mselId"></param>
        /// <param name="ct"></param>
        /// <returns></returns>
        [HttpGet("msels/{mselId}/moves")]
        [ProducesResponseType(typeof(IEnumerable<Move>), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "getMovesByMsel")]
        public async Task<IActionResult> GetByMsel(Guid mselId, CancellationToken ct)
        {
            var list = await _moveService.GetByMselAsync(mselId, ct);
            return Ok(list);
        }

        /// <summary>
        /// Gets a specific Move by id
        /// </summary>
        /// <remarks>
        /// Returns the Move with the id specified
        /// </remarks>
        /// <param name="id">The id of the Move</param>
        /// <param name="ct"></param>
        /// <returns></returns>
        [HttpGet("moves/{id}")]
        [ProducesResponseType(typeof(Move), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "getMove")]
        public async Task<IActionResult> Get(Guid id, CancellationToken ct)
        {
            var move = await _moveService.GetAsync(id, ct);

            if (move == null)
                throw new EntityNotFoundException<Move>();

            return Ok(move);
        }

        /// <summary>
        /// Creates a new Move
        /// </summary>
        /// <remarks>
        /// Creates a new Move with the attributes specified
        /// <para />
        /// Accessible only to a ContentDeveloper or an Administrator
        /// </remarks>
        /// <param name="move">The data used to create the Move</param>
        /// <param name="ct"></param>
        [HttpPost("moves")]
        [ProducesResponseType(typeof(Move), (int)HttpStatusCode.Created)]
        [SwaggerOperation(OperationId = "createMove")]
        public async Task<IActionResult> Create([FromBody] Move move, CancellationToken ct)
        {
            move.CreatedBy = User.GetId();
            var createdMove = await _moveService.CreateAsync(move, ct);
            return CreatedAtAction(nameof(this.Get), new { id = createdMove.Id }, createdMove);
        }

        /// <summary>
        /// Updates a  Move
        /// </summary>
        /// <remarks>
        /// Updates a Move with the attributes specified.
        /// The ID from the route MUST MATCH the ID contained in the move parameter
        /// <para />
        /// Accessible only to a ContentDeveloper or an Administrator
        /// </remarks>
        /// <param name="id">The Id of the Move to update</param>
        /// <param name="move">The updated Move values</param>
        /// <param name="ct"></param>
        [HttpPut("moves/{id}")]
        [ProducesResponseType(typeof(Move), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "updateMove")]
        public async Task<IActionResult> Update([FromRoute] Guid id, [FromBody] Move move, CancellationToken ct)
        {
            move.ModifiedBy = User.GetId();
            var updatedMove = await _moveService.UpdateAsync(id, move, ct);
            return Ok(updatedMove);
        }

        /// <summary>
        /// Deletes a  Move
        /// </summary>
        /// <remarks>
        /// Deletes a  Move with the specified id
        /// <para />
        /// Accessible only to a ContentDeveloper or an Administrator
        /// </remarks>
        /// <param name="id">The id of the Move to delete</param>
        /// <param name="ct"></param>
        [HttpDelete("moves/{id}")]
        [ProducesResponseType((int)HttpStatusCode.NoContent)]
        [SwaggerOperation(OperationId = "deleteMove")]
        public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
        {
            await _moveService.DeleteAsync(id, ct);
            return NoContent();
        }

    }
}
