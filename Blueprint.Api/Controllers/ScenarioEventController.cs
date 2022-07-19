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
using Swashbuckle.AspNetCore.Annotations;

namespace Blueprint.Api.Controllers
{
    public class ScenarioEventController : BaseController
    {
        private readonly IScenarioEventService _scenarioEventService;
        private readonly IAuthorizationService _authorizationService;

        public ScenarioEventController(IScenarioEventService scenarioEventService, IAuthorizationService authorizationService)
        {
            _scenarioEventService = scenarioEventService;
            _authorizationService = authorizationService;
        }

        /// <summary>
        /// Gets ScenarioEvents
        /// </summary>
        /// <remarks>
        /// Returns a list of ScenarioEvents.
        /// </remarks>
        /// <param name="queryParameters">Result filtering criteria</param>
        /// <param name="ct"></param>
        /// <returns></returns>
        [HttpGet("scenarioEvents")]
        [ProducesResponseType(typeof(IEnumerable<ViewModels.ScenarioEvent>), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "getScenarioEvents")]
        public async Task<IActionResult> Get([FromQuery] ScenarioEventGet queryParameters, CancellationToken ct)
        {
            var list = await _scenarioEventService.GetAsync(queryParameters, ct);
            return Ok(list);
        }

        /// <summary>
        /// Gets a specific ScenarioEvent by id
        /// </summary>
        /// <remarks>
        /// Returns the ScenarioEvent with the id specified
        /// <para />
        /// Accessible to a User that is a member of a Team within the specified ScenarioEvent
        /// </remarks>
        /// <param name="id">The id of the ScenarioEvent</param>
        /// <param name="ct"></param>
        /// <returns></returns>
        [HttpGet("scenarioEvents/{id}")]
        [ProducesResponseType(typeof(ViewModels.ScenarioEvent), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "getScenarioEvent")]
        public async Task<IActionResult> Get(Guid id, CancellationToken ct)
        {
            var scenarioEvent = await _scenarioEventService.GetAsync(id, ct);

            if (scenarioEvent == null)
                throw new EntityNotFoundException<ViewModels.ScenarioEvent>();

            return Ok(scenarioEvent);
        }

        /// <summary>
        /// Creates a new ScenarioEvent
        /// </summary>
        /// <remarks>
        /// Creates a new ScenarioEvent with the attributes specified
        /// <para />
        /// Accessible only to a SuperUser or an Administrator
        /// </remarks>
        /// <param name="scenarioEvent">The data to create the ScenarioEvent with</param>
        /// <param name="ct"></param>
        [HttpPost("scenarioEvents")]
        [ProducesResponseType(typeof(ViewModels.ScenarioEvent), (int)HttpStatusCode.Created)]
        [SwaggerOperation(OperationId = "createScenarioEvent")]
        public async Task<IActionResult> Create([FromBody] ViewModels.ScenarioEvent scenarioEvent, CancellationToken ct)
        {
            scenarioEvent.CreatedBy = User.GetId();
            var createdScenarioEvent = await _scenarioEventService.CreateAsync(scenarioEvent, ct);
            return CreatedAtAction(nameof(this.Get), new { id = createdScenarioEvent.Id }, createdScenarioEvent);
        }

        /// <summary>
        /// Updates an ScenarioEvent
        /// </summary>
        /// <remarks>
        /// Updates an ScenarioEvent with the attributes specified
        /// <para />
        /// Accessible only to a SuperUser or a User on an Admin Team within the specified ScenarioEvent
        /// </remarks>
        /// <param name="id">The Id of the ScenarioEvent to update</param>
        /// <param name="scenarioEvent">The updated ScenarioEvent values</param>
        /// <param name="ct"></param>
        [HttpPut("scenarioEvents/{id}")]
        [ProducesResponseType(typeof(ViewModels.ScenarioEvent), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "updateScenarioEvent")]
        public async Task<IActionResult> Update([FromRoute] Guid id, [FromBody] ViewModels.ScenarioEvent scenarioEvent, CancellationToken ct)
        {
            scenarioEvent.ModifiedBy = User.GetId();
            var updatedScenarioEvent = await _scenarioEventService.UpdateAsync(id, scenarioEvent, ct);
            return Ok(updatedScenarioEvent);
        }

        /// <summary>
        /// Assigns a ScenarioEvent to a Team
        /// </summary>
        /// <remarks>
        /// Assigns the specified Team to the specified ScenarioEvent
        /// <para />
        /// Accessible only to a Content Developer or a MSEL owner
        /// </remarks>
        /// <param name="scenarioEventId">The ID of the ScenarioEvent</param>
        /// <param name="teamId">The ID of the Team</param>
        /// <param name="ct"></param>
        [HttpPut("scenarioEvents/{scenarioEventId}/assignTeam/{teamId}")]
        [ProducesResponseType(typeof(Guid), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "assignTeamToScenarioEvent")]
        public async Task<IActionResult> assignTeamToScenarioEvent([FromRoute] Guid scenarioEventId, [FromRoute] Guid teamId, CancellationToken ct)
        {
            var resultId = await _scenarioEventService.AssignTeamAsync(scenarioEventId, teamId, ct);
            return Ok(resultId);
        }

        /// <summary>
        /// Unassigns a ScenarioEvent from a Team
        /// </summary>
        /// <remarks>
        /// Unassigns the specified Team from the specified ScenarioEvent
        /// <para />
        /// Accessible only to a Content Developer or a MSEL owner
        /// </remarks>
        /// <param name="scenarioEventId">The ID of the ScenarioEvent</param>
        /// <param name="teamId">The ID of the Team</param>
        /// <param name="ct"></param>
        [HttpPut("scenarioEvents/{scenarioEventId}/unassignTeam/{teamId}")]
        [ProducesResponseType(typeof(bool), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "unassignTeamToScenarioEvent")]
        public async Task<IActionResult> unassignTeamToScenarioEvent([FromRoute] Guid scenarioEventId, [FromRoute] Guid teamId, CancellationToken ct)
        {
            var result = await _scenarioEventService.UnassignTeamAsync(scenarioEventId, teamId, ct);
            return Ok(result);
        }

        /// <summary>
        /// Deletes an ScenarioEvent
        /// </summary>
        /// <remarks>
        /// Deletes an ScenarioEvent with the specified id
        /// <para />
        /// Accessible only to a SuperUser or a User on an Admin Team within the specified ScenarioEvent
        /// </remarks>
        /// <param name="id">The id of the ScenarioEvent to delete</param>
        /// <param name="ct"></param>
        [HttpDelete("scenarioEvents/{id}")]
        [ProducesResponseType((int)HttpStatusCode.NoContent)]
        [SwaggerOperation(OperationId = "deleteScenarioEvent")]
        public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
        {
            await _scenarioEventService.DeleteAsync(id, ct);
            return NoContent();
        }

    }
}
