// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Blueprint.Api.Automation;
using Blueprint.Api.Data.Enumerations;
using Blueprint.Api.Infrastructure.Authorization;
using Blueprint.Api.Services;
using Blueprint.Api.ViewModels;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Swashbuckle.AspNetCore.Annotations;

namespace Blueprint.Api.Controllers
{
    /// <summary>Automation tasks, the actions they can perform, and their runs.</summary>
    public class AutomationController : BaseController
    {
        private readonly IAutomationService _automationService;
        private readonly IBlueprintAuthorizationService _authorizationService;

        public AutomationController(IAutomationService automationService, IBlueprintAuthorizationService authorizationService)
        {
            _automationService = automationService;
            _authorizationService = authorizationService;
        }

        /// <summary>Gets the actions an automation task can perform, and the parameters each takes</summary>
        [HttpGet("automationActions")]
        [ProducesResponseType(typeof(IEnumerable<AutomationActionDefinition>), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "getAutomationActions")]
        public IActionResult GetActions() => Ok(_automationService.GetActions());

        /// <summary>Gets a MSEL's automation tasks</summary>
        /// <remarks>A participant of the MSEL is sent none: the Exercise View shows no automation.</remarks>
        [HttpGet("msels/{mselId}/automationTasks")]
        [ProducesResponseType(typeof(IEnumerable<AutomationTask>), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "getAutomationTasksByMsel")]
        public async Task<IActionResult> GetTasksByMsel(Guid mselId, CancellationToken ct)
        {
            var hasViewMsels = await _authorizationService.AuthorizeAsync([SystemPermission.ViewMsels], ct);
            var hasCreateMsels = await _authorizationService.AuthorizeAsync([SystemPermission.CreateMsels], ct);
            return Ok(await _automationService.GetTasksByMselAsync(mselId, hasViewMsels, hasCreateMsels, ct));
        }

        /// <summary>Gets a scenario event's automation tasks, in order</summary>
        [HttpGet("scenarioEvents/{scenarioEventId}/automationTasks")]
        [ProducesResponseType(typeof(IEnumerable<AutomationTask>), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "getAutomationTasksByScenarioEvent")]
        public async Task<IActionResult> GetTasksByScenarioEvent(Guid scenarioEventId, CancellationToken ct)
        {
            var hasViewMsels = await _authorizationService.AuthorizeAsync([SystemPermission.ViewMsels], ct);
            var hasCreateMsels = await _authorizationService.AuthorizeAsync([SystemPermission.CreateMsels], ct);
            return Ok(await _automationService.GetTasksByScenarioEventAsync(scenarioEventId, hasViewMsels, hasCreateMsels, ct));
        }

        /// <summary>Tests an automation task</summary>
        /// <remarks>
        /// Runs the task as the form has it, saved or not, once, against a Player view the caller can see (or the MSEL's
        /// own). A test is not exercise history. Accessible to whoever may edit the MSEL's scenario events.
        /// </remarks>
        [HttpPost("automationTasks/test")]
        [ProducesResponseType(typeof(AutomationRun), (int)HttpStatusCode.Created)]
        [SwaggerOperation(OperationId = "testAutomationTask")]
        public async Task<IActionResult> Test([FromBody] AutomationTestForm form, CancellationToken ct)
        {
            var hasEditMsels = await _authorizationService.AuthorizeAsync([SystemPermission.EditMsels], ct);
            var run = await _automationService.TestAsync(form, hasEditMsels, ct);
            return CreatedAtAction(nameof(GetRun), new { id = run.Id }, run);
        }

        /// <summary>Lists the VMs of a Player view an automation task's mask matches</summary>
        [HttpPost("msels/{mselId}/automationTasks/vmPreview")]
        [ProducesResponseType(typeof(AutomationVmPreview), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "previewAutomationTaskVms")]
        public async Task<IActionResult> PreviewVms(Guid mselId, [FromBody] AutomationVmPreviewForm form, CancellationToken ct)
        {
            var hasEditMsels = await _authorizationService.AuthorizeAsync([SystemPermission.EditMsels], ct);
            return Ok(await _automationService.PreviewVmsAsync(mselId, form, hasEditMsels, ct));
        }

        /// <summary>Runs an automation task of a deployed MSEL now</summary>
        /// <remarks>Accessible to the MSEL's Owners and Facilitators.</remarks>
        [HttpPost("automationTasks/{id}/run")]
        [ProducesResponseType(typeof(AutomationRun), (int)HttpStatusCode.Created)]
        [SwaggerOperation(OperationId = "runAutomationTask")]
        public async Task<IActionResult> Run(Guid id, [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] AutomationRunForm form, CancellationToken ct)
        {
            var hasEditMsels = await _authorizationService.AuthorizeAsync([SystemPermission.EditMsels], ct);
            var run = await _automationService.RunAsync(id, form, hasEditMsels, ct);
            return CreatedAtAction(nameof(GetRun), new { id = run.Id }, run);
        }

        /// <summary>Gets the tasks participants can run on a MSEL, with the exercise's score</summary>
        /// <remarks>Accessible to anyone who may read the MSEL, its participants included. A task's action, parameters and
        /// output are not part of it.</remarks>
        [HttpGet("msels/{mselId}/participantTasks")]
        [ProducesResponseType(typeof(ParticipantTasks), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "getParticipantTasks")]
        public async Task<IActionResult> GetParticipantTasks(Guid mselId, CancellationToken ct)
        {
            var hasViewMsels = await _authorizationService.AuthorizeAsync([SystemPermission.ViewMsels], ct);
            return Ok(await _automationService.GetParticipantTasksAsync(mselId, hasViewMsels, ct));
        }

        /// <summary>Runs a task participants can run, as a participant</summary>
        /// <remarks>Accessible to the MSEL's participants and contributors, and with EditMsels, while it is deployed and its
        /// automation is not paused. ViewMsels alone does not run tasks.</remarks>
        [HttpPost("msels/{mselId}/participantTasks/{taskId}/run")]
        [ProducesResponseType(typeof(ParticipantTask), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "runParticipantTask")]
        public async Task<IActionResult> RunParticipantTask(Guid mselId, Guid taskId, CancellationToken ct)
        {
            var hasEditMsels = await _authorizationService.AuthorizeAsync([SystemPermission.EditMsels], ct);
            return Ok(await _automationService.RunAsParticipantAsync(mselId, taskId, hasEditMsels, ct));
        }

        /// <summary>Gets a MSEL's automation runs, newest first, with their results</summary>
        /// <remarks>Accessible to whoever may watch the MSEL's Facilitator View.</remarks>
        [HttpGet("msels/{mselId}/automationRuns")]
        [ProducesResponseType(typeof(IEnumerable<AutomationRun>), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "getAutomationRunsByMsel")]
        public async Task<IActionResult> GetRunsByMsel(Guid mselId, [FromQuery] AutomationRunKind? kind, [FromQuery] Guid? taskId,
            [FromQuery] DateTime? since, CancellationToken ct)
        {
            var hasViewMsels = await _authorizationService.AuthorizeAsync([SystemPermission.ViewMsels], ct);
            return Ok(await _automationService.GetRunsByMselAsync(mselId, kind, taskId, since, hasViewMsels, ct));
        }

        /// <summary>Gets an automation run with its results</summary>
        [HttpGet("automationRuns/{id}")]
        [ProducesResponseType(typeof(AutomationRun), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "getAutomationRun")]
        public async Task<IActionResult> GetRun(Guid id, CancellationToken ct)
        {
            var hasViewMsels = await _authorizationService.AuthorizeAsync([SystemPermission.ViewMsels], ct);
            return Ok(await _automationService.GetRunAsync(id, hasViewMsels, ct));
        }

        /// <summary>Cancels an automation run that has not ended</summary>
        [HttpPost("automationRuns/{id}/cancel")]
        [ProducesResponseType(typeof(AutomationRun), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "cancelAutomationRun")]
        public async Task<IActionResult> Cancel(Guid id, CancellationToken ct)
        {
            var hasEditMsels = await _authorizationService.AuthorizeAsync([SystemPermission.EditMsels], ct);
            return Ok(await _automationService.CancelAsync(id, hasEditMsels, ct));
        }

        /// <summary>Runs the task of an exercise run that has ended again</summary>
        [HttpPost("automationRuns/{id}/rerun")]
        [ProducesResponseType(typeof(AutomationRun), (int)HttpStatusCode.Created)]
        [SwaggerOperation(OperationId = "rerunAutomationRun")]
        public async Task<IActionResult> Rerun(Guid id, [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] AutomationRunForm form, CancellationToken ct)
        {
            var hasEditMsels = await _authorizationService.AuthorizeAsync([SystemPermission.EditMsels], ct);
            var run = await _automationService.RerunAsync(id, form, hasEditMsels, ct);
            return CreatedAtAction(nameof(GetRun), new { id = run.Id }, run);
        }
    }

    /// <summary>Running a deployed MSEL from the Facilitator View.</summary>
    public class MselExecutionController : BaseController
    {
        private readonly IMselExecutionService _executionService;
        private readonly IBlueprintAuthorizationService _authorizationService;

        public MselExecutionController(IMselExecutionService executionService, IBlueprintAuthorizationService authorizationService)
        {
            _executionService = executionService;
            _authorizationService = authorizationService;
        }

        /// <summary>Gets where a deployed MSEL is, and each scenario event's move and group</summary>
        [HttpGet("msels/{mselId}/execution")]
        [ProducesResponseType(typeof(MselExecution), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "getMselExecution")]
        public async Task<IActionResult> Get(Guid mselId, CancellationToken ct)
        {
            var hasViewMsels = await _authorizationService.AuthorizeAsync([SystemPermission.ViewMsels], ct);
            return Ok(await _executionService.GetAsync(mselId, hasViewMsels, ct));
        }

        /// <summary>Starts a move: moves the MSEL's CITE evaluation and Gallery exhibit to it, and runs the manual tasks chosen</summary>
        [HttpPost("msels/{mselId}/execution/moves/{moveNumber}/start")]
        [ProducesResponseType(typeof(MselAdvanceResult), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "startMselMove")]
        public async Task<IActionResult> StartMove(Guid mselId, int moveNumber, [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] MselAdvanceForm form, CancellationToken ct)
        {
            var hasEditMsels = await _authorizationService.AuthorizeAsync([SystemPermission.EditMsels], ct);
            return Ok(await _executionService.StartMoveAsync(mselId, moveNumber, form, hasEditMsels, ct));
        }

        /// <summary>Starts a group of a move: moves the MSEL's Gallery exhibit to it, and runs the manual tasks chosen</summary>
        [HttpPost("msels/{mselId}/execution/moves/{moveNumber}/groups/{groupNumber}/start")]
        [ProducesResponseType(typeof(MselAdvanceResult), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "startMselGroup")]
        public async Task<IActionResult> StartGroup(Guid mselId, int moveNumber, int groupNumber, [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] MselAdvanceForm form, CancellationToken ct)
        {
            var hasEditMsels = await _authorizationService.AuthorizeAsync([SystemPermission.EditMsels], ct);
            return Ok(await _executionService.StartGroupAsync(mselId, moveNumber, groupNumber, form, hasEditMsels, ct));
        }

        /// <summary>Pauses or resumes the tasks a deployed MSEL runs at their event time</summary>
        [HttpPut("msels/{mselId}/execution/paused")]
        [ProducesResponseType(typeof(MselExecution), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "setMselAutomationPaused")]
        public async Task<IActionResult> SetPaused(Guid mselId, [FromBody] AutomationPausedForm form, CancellationToken ct)
        {
            var hasEditMsels = await _authorizationService.AuthorizeAsync([SystemPermission.EditMsels], ct);
            return Ok(await _executionService.SetPausedAsync(mselId, form?.Paused ?? false, hasEditMsels, ct));
        }
    }
}
