// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Blueprint.Api.Data.Enumerations;
using Blueprint.Api.Infrastructure.Authorization;
using Blueprint.Api.Infrastructure.Exceptions;
using Blueprint.Api.Services;
using Blueprint.Api.ViewModels;
using Swashbuckle.AspNetCore.Annotations;

namespace Blueprint.Api.Controllers
{
    public class TeamCompetencyController : BaseController
    {
        private readonly ITeamCompetencyService _teamCompetencyService;
        private readonly IBlueprintAuthorizationService _authorizationService;

        public TeamCompetencyController(ITeamCompetencyService teamCompetencyService, IBlueprintAuthorizationService authorizationService)
        {
            _teamCompetencyService = teamCompetencyService;
            _authorizationService = authorizationService;
        }

        /// <summary>
        /// Gets all TeamCompetencies for a Team
        /// </summary>
        /// <param name="teamId">The id of the Team</param>
        /// <param name="ct"></param>
        /// <returns></returns>
        [HttpGet("teams/{teamId}/teamcompetencies")]
        [ProducesResponseType(typeof(IEnumerable<TeamCompetency>), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "getTeamCompetencies")]
        public async Task<IActionResult> GetByTeam(Guid teamId, CancellationToken ct)
        {
            var hasSystemPermission = await _authorizationService.AuthorizeAsync([SystemPermission.ViewMsels], ct);
            var list = await _teamCompetencyService.GetByTeamAsync(teamId, hasSystemPermission, ct);
            return Ok(list);
        }

        /// <summary>
        /// Gets all TeamCompetencies for a MSEL
        /// </summary>
        /// <param name="mselId">The id of the Msel</param>
        /// <param name="ct"></param>
        /// <returns></returns>
        [HttpGet("msels/{mselId}/teamcompetencies")]
        [ProducesResponseType(typeof(IEnumerable<TeamCompetency>), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "getMselTeamCompetencies")]
        public async Task<IActionResult> GetByMsel(Guid mselId, CancellationToken ct)
        {
            var hasSystemPermission = await _authorizationService.AuthorizeAsync([SystemPermission.ViewMsels], ct);
            var list = await _teamCompetencyService.GetByMselAsync(mselId, hasSystemPermission, ct);
            return Ok(list);
        }

        /// <summary>
        /// Gets a specific TeamCompetency by id
        /// </summary>
        /// <param name="id">The id of the TeamCompetency</param>
        /// <param name="ct"></param>
        /// <returns></returns>
        [HttpGet("teamcompetencies/{id}")]
        [ProducesResponseType(typeof(TeamCompetency), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "getTeamCompetency")]
        public async Task<IActionResult> Get(Guid id, CancellationToken ct)
        {
            var hasSystemPermission = await _authorizationService.AuthorizeAsync([SystemPermission.ViewMsels], ct);
            var item = await _teamCompetencyService.GetAsync(id, hasSystemPermission, ct);

            if (item == null)
                throw new EntityNotFoundException<TeamCompetency>();

            return Ok(item);
        }

        /// <summary>
        /// Creates a new TeamCompetency
        /// </summary>
        /// <param name="teamCompetency">The data to create the TeamCompetency with</param>
        /// <param name="ct"></param>
        [HttpPost("teamcompetencies")]
        [ProducesResponseType(typeof(TeamCompetency), (int)HttpStatusCode.Created)]
        [SwaggerOperation(OperationId = "createTeamCompetency")]
        public async Task<IActionResult> Create([FromBody] TeamCompetency teamCompetency, CancellationToken ct)
        {
            var hasSystemPermission = await _authorizationService.AuthorizeAsync([SystemPermission.ManageMsels], ct);
            var createdItem = await _teamCompetencyService.CreateAsync(teamCompetency, hasSystemPermission, ct);
            return CreatedAtAction(nameof(this.Get), new { id = createdItem.Id }, createdItem);
        }

        /// <summary>
        /// Deletes a TeamCompetency
        /// </summary>
        /// <param name="id">The id of the TeamCompetency to delete</param>
        /// <param name="ct"></param>
        [HttpDelete("teamcompetencies/{id}")]
        [ProducesResponseType((int)HttpStatusCode.NoContent)]
        [SwaggerOperation(OperationId = "deleteTeamCompetency")]
        public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
        {
            var hasSystemPermission = await _authorizationService.AuthorizeAsync([SystemPermission.ManageMsels], ct);
            await _teamCompetencyService.DeleteAsync(id, hasSystemPermission, ct);
            return NoContent();
        }

        /// <summary>
        /// Deletes a TeamCompetency by team ID and competency ID
        /// </summary>
        /// <param name="teamId">ID of a team.</param>
        /// <param name="competencyId">ID of a competency.</param>
        /// <param name="ct"></param>
        [HttpDelete("teams/{teamId}/competencies/{competencyId}")]
        [ProducesResponseType((int)HttpStatusCode.NoContent)]
        [SwaggerOperation(OperationId = "deleteTeamCompetencyByIds")]
        public async Task<IActionResult> Delete(Guid teamId, Guid competencyId, CancellationToken ct)
        {
            var hasSystemPermission = await _authorizationService.AuthorizeAsync([SystemPermission.ManageMsels], ct);
            await _teamCompetencyService.DeleteByIdsAsync(teamId, competencyId, hasSystemPermission, ct);
            return NoContent();
        }
    }
}
