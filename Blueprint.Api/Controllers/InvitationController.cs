// Copyright 2024 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Blueprint.Api.Infrastructure.Exceptions;
using Blueprint.Api.Services;
using Blueprint.Api.ViewModels;
using Swashbuckle.AspNetCore.Annotations;

namespace Blueprint.Api.Controllers
{
    public class InvitationController : BaseController
    {
        private readonly IInvitationService _invitationService;
        private readonly IAuthorizationService _authorizationService;

        public InvitationController(IInvitationService invitationService, IAuthorizationService authorizationService)
        {
            _invitationService = invitationService;
            _authorizationService = authorizationService;
        }

        /// <summary>
        /// Gets all Invitations for a MSEL
        /// </summary>
        /// <remarks>
        /// Returns a list of all of the Invitations for the msel.
        /// </remarks>
        /// <param name="mselId">The id of the Msel</param>
        /// <param name="ct"></param>
        /// <returns></returns>
        [HttpGet("msels/{mselId}/invitations")]
        [ProducesResponseType(typeof(IEnumerable<Invitation>), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "getInvitations")]
        public async Task<IActionResult> GetByMsel(Guid mselId, CancellationToken ct)
        {
            var list = await _invitationService.GetByMselAsync(mselId, ct);
            return Ok(list);
        }

        /// <summary>
        /// Gets a specific Invitation by id
        /// </summary>
        /// <remarks>
        /// Returns the Invitation with the id specified
        /// <para />
        /// </remarks>
        /// <param name="id">The id of the Invitation</param>
        /// <param name="ct"></param>
        /// <returns></returns>
        [HttpGet("invitations/{id}")]
        [ProducesResponseType(typeof(Invitation), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "getInvitation")]
        public async Task<IActionResult> Get(Guid id, CancellationToken ct)
        {
            var invitation = await _invitationService.GetAsync(id, ct);

            if (invitation == null)
                throw new EntityNotFoundException<Invitation>();

            return Ok(invitation);
        }

        /// <summary>
        /// Creates a new Invitation
        /// </summary>
        /// <remarks>
        /// Creates a new Invitation with the attributes specified
        /// <para />
        /// </remarks>
        /// <param name="invitation">The data to create the Invitation with</param>
        /// <param name="ct"></param>
        [HttpPost("invitations")]
        [ProducesResponseType(typeof(Invitation), (int)HttpStatusCode.Created)]
        [SwaggerOperation(OperationId = "createInvitation")]
        public async Task<IActionResult> Create([FromBody] Invitation invitation, CancellationToken ct)
        {
            var createdInvitation = await _invitationService.CreateAsync(invitation, ct);
            return CreatedAtAction(nameof(this.Get), new { id = createdInvitation.Id }, createdInvitation);
        }

        /// <summary>
        /// Updates a Invitation
        /// </summary>
        /// <remarks>
        /// Updates a Invitation with the attributes specified
        /// <para />
        /// </remarks>
        /// <param name="id">The Id of the Invitation to update</param>
        /// <param name="invitation">The updated Invitation values</param>
        /// <param name="ct"></param>
        [HttpPut("invitations/{id}")]
        [ProducesResponseType(typeof(Invitation), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "updateInvitation")]
        public async Task<IActionResult> Update([FromRoute] Guid id, [FromBody] Invitation invitation, CancellationToken ct)
        {
            var updatedPage = await _invitationService.UpdateAsync(id, invitation, ct);
            return Ok(updatedPage);
        }

        /// <summary>
        /// Deletes a Invitation
        /// </summary>
        /// <remarks>
        /// Deletes a Invitation with the specified id
        /// <para />
        /// </remarks>
        /// <param name="id">The id of the Invitation to delete</param>
        /// <param name="ct"></param>
        [HttpDelete("invitations/{id}")]
        [ProducesResponseType((int)HttpStatusCode.NoContent)]
        [SwaggerOperation(OperationId = "deleteInvitation")]
        public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
        {
            await _invitationService.DeleteAsync(id, ct);
            return NoContent();
        }

    }
}

