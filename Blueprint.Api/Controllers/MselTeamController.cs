// Copyright 2023 Carnegie Mellon University. All Rights Reserved.
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
    public class MselTeamController : BaseController
    {
        private readonly IMselTeamService _mselTeamService;
        private readonly IAuthorizationService _authorizationService;

        public MselTeamController(IMselTeamService mselTeamService, IAuthorizationService authorizationService)
        {
            _mselTeamService = mselTeamService;
            _authorizationService = authorizationService;
        }

        /// <summary>
        /// Gets all MselTeams for a MSEL
        /// </summary>
        /// <remarks>
        /// Returns a list of all of the MselTeams for the msel.
        /// </remarks>
        /// <param name="mselId">The id of the Msel</param>
        /// <param name="ct"></param>
        /// <returns></returns>
        [HttpGet("msels/{mselId}/mselteams")]
        [ProducesResponseType(typeof(IEnumerable<MselTeam>), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "getMselTeams")]
        public async Task<IActionResult> GetByMsel(Guid mselId, CancellationToken ct)
        {
            var list = await _mselTeamService.GetByMselAsync(mselId, ct);
            return Ok(list);
        }

        /// <summary>
        /// Gets a specific MselTeam by id
        /// </summary>
        /// <remarks>
        /// Returns the MselTeam with the id specified
        /// <para />
        /// </remarks>
        /// <param name="id">The id of the MselTeam</param>
        /// <param name="ct"></param>
        /// <returns></returns>
        [HttpGet("mselteams/{id}")]
        [ProducesResponseType(typeof(MselTeam), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "getMselTeam")]
        public async Task<IActionResult> Get(Guid id, CancellationToken ct)
        {
            var team = await _mselTeamService.GetAsync(id, ct);

            if (team == null)
                throw new EntityNotFoundException<MselTeam>();

            return Ok(team);
        }

        /// <summary>
        /// Creates a new MselTeam
        /// </summary>
        /// <remarks>
        /// Creates a new MselTeam with the attributes specified
        /// <para />
        /// </remarks>
        /// <param name="mselTeam">The data to create the MselTeam with</param>
        /// <param name="ct"></param>
        [HttpPost("mselteams")]
        [ProducesResponseType(typeof(MselTeam), (int)HttpStatusCode.Created)]
        [SwaggerOperation(OperationId = "createMselTeam")]
        public async Task<IActionResult> Create([FromBody] MselTeam mselTeam, CancellationToken ct)
        {
            var createdMselTeam = await _mselTeamService.CreateAsync(mselTeam, ct);
            return CreatedAtAction(nameof(this.Get), new { id = createdMselTeam.Id }, createdMselTeam);
        }

        /// <summary>
        /// Updates a MselTeam
        /// </summary>
        /// <remarks>
        /// Updates a MselTeam with the attributes specified
        /// <para />
        /// </remarks>
        /// <param name="id">The Id of the MselTeam to update</param>
        /// <param name="mselTeam">The updated MselTeam values</param>
        /// <param name="ct"></param>
        [HttpPut("mselteams/{id}")]
        [ProducesResponseType(typeof(Team), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "updateMselTeam")]
        public async Task<IActionResult> Update([FromRoute] Guid id, [FromBody] MselTeam mselTeam, CancellationToken ct)
        {
            var updatedTeam = await _mselTeamService.UpdateAsync(id, mselTeam, ct);
            return Ok(updatedTeam);
        }

        /// <summary>
        /// Deletes a MselTeam
        /// </summary>
        /// <remarks>
        /// Deletes a MselTeam with the specified id
        /// <para />
        /// </remarks>
        /// <param name="id">The id of the MselTeam to delete</param>
        /// <param name="ct"></param>
        [HttpDelete("mselteams/{id}")]
        [ProducesResponseType((int)HttpStatusCode.NoContent)]
        [SwaggerOperation(OperationId = "deleteMselTeam")]
        public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
        {
            await _mselTeamService.DeleteAsync(id, ct);
            return NoContent();
        }

        /// <summary>
        /// Deletes a MselTeam by msel ID and team ID
        /// </summary>
        /// <remarks>
        /// Deletes a MselTeam with the specified msel ID and team ID
        /// <para />
        /// </remarks>
        /// <param name="mselId">ID of a msel.</param>
        /// <param name="teamId">ID of a team.</param>
        /// <param name="ct"></param>
        [HttpDelete("msels/{mselId}/teams/{teamId}")]
        [ProducesResponseType((int)HttpStatusCode.NoContent)]
        [SwaggerOperation(OperationId = "deleteMselTeamByIds")]
        public async Task<IActionResult> Delete(Guid mselId, Guid teamId, CancellationToken ct)
        {
            await _mselTeamService.DeleteByIdsAsync(mselId, teamId, ct);
            return NoContent();
        }

    }
}

