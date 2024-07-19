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
        /// Gets ScenarioEvents for a MSEL
        /// </summary>
        /// <remarks>
        /// Returns a list of ScenarioEvents for the MSEL.
        /// </remarks>
        /// <param name="mselId">The ID of the MSEL</param>
        /// <param name="ct"></param>
        /// <returns></returns>
        [HttpGet("msels/{mselId}/scenarioEvents")]
        [ProducesResponseType(typeof(IEnumerable<ViewModels.ScenarioEvent>), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "getScenarioEventsByMsel")]
        public async Task<IActionResult> GetByMsel(Guid mselId, CancellationToken ct)
        {
            var list = await _scenarioEventService.GetByMselAsync(mselId, ct);
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
        [ProducesResponseType(typeof(IEnumerable<ViewModels.ScenarioEvent>), (int)HttpStatusCode.Created)]
        [SwaggerOperation(OperationId = "createScenarioEvent")]
        public async Task<IActionResult> Create([FromBody] ViewModels.ScenarioEvent scenarioEvent, CancellationToken ct)
        {
            var list = await _scenarioEventService.CreateAsync(scenarioEvent, ct);
            return Ok(list);
        }

        /// <summary>
        /// Creates ScenarioEvents from a list of injects
        /// </summary>
        /// <remarks>
        /// Creates new ScenarioEvents from the specified injects for the specified MSEL
        /// </remarks>
        /// <param name="createFromInjectsForm">The data to create the ScenarioEvents</param>
        /// <param name="ct"></param>
        [HttpPost("scenarioEvents/fromInjects")]
        [ProducesResponseType(typeof(IEnumerable<ViewModels.ScenarioEvent>), (int)HttpStatusCode.Created)]
        [SwaggerOperation(OperationId = "createScenarioEventsFromInjects")]
        public async Task<IActionResult> CreateFromInjects([FromBody] CreateFromInjectsForm createFromInjectsForm, CancellationToken ct)
        {
            var list = await _scenarioEventService.CreateFromInjectsAsync(createFromInjectsForm, ct);
            return Ok(list);
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
        [ProducesResponseType(typeof(IEnumerable<ViewModels.ScenarioEvent>), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "updateScenarioEvent")]
        public async Task<IActionResult> Update([FromRoute] Guid id, [FromBody] ViewModels.ScenarioEvent scenarioEvent, CancellationToken ct)
        {
            scenarioEvent.ModifiedBy = User.GetId();
            var list = await _scenarioEventService.UpdateAsync(id, scenarioEvent, ct);
            return Ok(list);
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
            var returnVal = await _scenarioEventService.DeleteAsync(id, ct);
            return Ok(returnVal);
        }

        /// <summary>
        /// Deletes multiple ScenarioEvents
        /// </summary>
        /// <remarks>
        /// Deletes the ScenarioEvents specified
        /// </remarks>
        /// <param name="scenarioEventIdList">The list of ScenarioEvent IDs to delete</param>
        /// <param name="ct"></param>
        [HttpPost("scenarioEvents/batchDelete")]
        [ProducesResponseType((int)HttpStatusCode.NoContent)]
        [SwaggerOperation(OperationId = "batchDeleteScenarioEvents")]
        public async Task<IActionResult> BatchDelete([FromBody] Guid[] scenarioEventIdList, CancellationToken ct)
        {
            var returnVal = await _scenarioEventService.BatchDeleteAsync(scenarioEventIdList, ct);
            return Ok(returnVal);
        }

    }
}
