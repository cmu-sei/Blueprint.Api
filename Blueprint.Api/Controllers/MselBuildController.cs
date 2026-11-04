// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Blueprint.Api.Data.Enumerations;
using Blueprint.Api.Infrastructure.Authorization;
using Blueprint.Api.Infrastructure.Exceptions;
using Blueprint.Api.Services;
using Blueprint.Api.ViewModels;
using Microsoft.AspNetCore.Mvc;
using Swashbuckle.AspNetCore.Annotations;

namespace Blueprint.Api.Controllers
{
    /// <summary>
    /// Builds a MSEL with AI in steps: a design the planner reviews, then the MSEL itself, which its injects, articles
    /// and profiles are written into and saved as they come, then what needs the whole MSEL.
    /// </summary>
    public class MselBuildController : BaseController
    {
        private readonly IMselDraftService _mselDraftService;
        private readonly IMselService _mselService;
        private readonly IMselBuildService _mselBuildService;
        private readonly IBlueprintAuthorizationService _authorizationService;

        public MselBuildController(IMselDraftService mselDraftService, IMselService mselService, IMselBuildService mselBuildService,
            IBlueprintAuthorizationService authorizationService)
        {
            _mselDraftService = mselDraftService;
            _mselService = mselService;
            _mselBuildService = mselBuildService;
            _authorizationService = authorizationService;
        }

        /// <summary> Design a MSEL with AI </summary>
        /// <remarks>
        /// The first step: the scenario, objectives, moves, teams, organizations and Gallery cards, and up to five questions
        /// for the planner, each with the answer the draft would use. Nothing is saved.
        /// </remarks>
        [HttpPost("msels/ai-draft/design")]
        [ProducesResponseType(typeof(MselDraftResult), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "designMsel")]
        public async Task<IActionResult> DesignAsync([FromBody] MselDraftRequest request, CancellationToken ct)
        {
            if (!await _authorizationService.AuthorizeAsync([SystemPermission.CreateMsels], ct))
                throw new ForbiddenException();
            return Ok(await _mselDraftService.DesignAsync(request, ct));
        }

        /// <summary> Create a MSEL from a reviewed AI design </summary>
        /// <remarks> The MSEL's injects, articles and profiles are written into it in the steps that follow. </remarks>
        [HttpPost("msels/ai-draft/create")]
        [ProducesResponseType(typeof(MselImportResult), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "createMselFromDesign")]
        public async Task<IActionResult> CreateAsync([FromBody] MselDesignBuild build, CancellationToken ct)
        {
            if (!await _authorizationService.AuthorizeAsync([SystemPermission.CreateMsels], ct))
                throw new ForbiddenException();
            return Ok(await _mselService.CreateFromDesignAsync(build, ct));
        }

        /// <summary> Where building a MSEL with AI stands </summary>
        [HttpGet("msels/{id}/ai-draft")]
        [ProducesResponseType(typeof(MselBuildStatus), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "getMselBuildStatus")]
        public async Task<IActionResult> GetStatusAsync(Guid id, CancellationToken ct) =>
            Ok(await _mselBuildService.GetStatusAsync(id, await CanEditAllAsync(ct), ct));

        /// <summary> Write a MSEL's injects with AI </summary>
        /// <remarks> The injects, CITE tasks and questions, and read-in, written to the MSEL's design and saved. </remarks>
        [HttpPost("msels/{id}/ai-draft/injects")]
        [ProducesResponseType(typeof(MselBuildStatus), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "writeMselInjects")]
        public async Task<IActionResult> WriteInjectsAsync(Guid id, [FromQuery] string connectionId, CancellationToken ct) =>
            Ok(await _mselBuildService.WriteInjectsAsync(id, connectionId, await CanEditAllAsync(ct), ct));

        /// <summary> Write the long text of one of a MSEL's drafted injects with AI </summary>
        /// <remarks> The injects are outlined first; each one's document, message or description is then written and saved on its own. </remarks>
        [HttpPost("msels/{id}/ai-draft/rows/{scenarioEventId}")]
        [ProducesResponseType(typeof(MselBuildContent), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "writeMselRow")]
        public async Task<IActionResult> WriteRowAsync(Guid id, Guid scenarioEventId, [FromQuery] string connectionId, CancellationToken ct) =>
            Ok(await _mselBuildService.WriteRowAsync(id, scenarioEventId, connectionId, await CanEditAllAsync(ct), ct));

        /// <summary> Write one of a MSEL's Gallery articles with AI </summary>
        /// <remarks> Saved on its row as written; its graphics are SVG until saved again as PNG. </remarks>
        [HttpPost("msels/{id}/ai-draft/articles/{scenarioEventId}")]
        [ProducesResponseType(typeof(ArticleDraftResult), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "writeMselArticle")]
        public async Task<IActionResult> WriteArticleAsync(Guid id, Guid scenarioEventId, [FromQuery] string connectionId, CancellationToken ct) =>
            Ok(await _mselBuildService.WriteArticleAsync(id, scenarioEventId, connectionId, await CanEditAllAsync(ct), ct));

        /// <summary> Save a written Gallery article again, with its graphics as PNG </summary>
        [HttpPut("msels/{id}/ai-draft/articles/{scenarioEventId}")]
        [ProducesResponseType((int)HttpStatusCode.NoContent)]
        [SwaggerOperation(OperationId = "saveMselArticle")]
        public async Task<IActionResult> SaveArticleAsync(Guid id, Guid scenarioEventId, [FromBody] MselBuildContent content, [FromQuery] string connectionId, CancellationToken ct)
        {
            await _mselBuildService.SaveArticleAsync(id, scenarioEventId, content?.Html, connectionId, await CanEditAllAsync(ct), ct);
            return NoContent();
        }

        /// <summary> Write a drafted team's or organization's profile with AI </summary>
        [HttpPost("msels/{id}/ai-draft/profiles/{ownerId}")]
        [ProducesResponseType(typeof(ProfileDraftResult), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "writeMselProfile")]
        public async Task<IActionResult> WriteProfileAsync(Guid id, Guid ownerId, [FromQuery] string connectionId, CancellationToken ct) =>
            Ok(await _mselBuildService.WriteProfileAsync(id, ownerId, connectionId, await CanEditAllAsync(ct), ct));

        /// <summary> Save a written profile again, with its logo as PNG </summary>
        [HttpPut("msels/{id}/ai-draft/profiles/{ownerId}")]
        [ProducesResponseType((int)HttpStatusCode.NoContent)]
        [SwaggerOperation(OperationId = "saveMselProfile")]
        public async Task<IActionResult> SaveProfileAsync(Guid id, Guid ownerId, [FromBody] MselBuildContent content, [FromQuery] string connectionId, CancellationToken ct)
        {
            await _mselBuildService.SaveProfileAsync(id, ownerId, content?.Html, connectionId, await CanEditAllAsync(ct), ct);
            return NoContent();
        }

        /// <summary> Finish building a MSEL with AI </summary>
        /// <remarks> The overview, facilitator and profile pages, the Player applications, and CITE. </remarks>
        [HttpPost("msels/{id}/ai-draft/finish")]
        [ProducesResponseType(typeof(MselBuildStatus), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "finishMselBuild")]
        public async Task<IActionResult> FinishAsync(Guid id, [FromQuery] string connectionId, CancellationToken ct) =>
            Ok(await _mselBuildService.FinishAsync(id, connectionId, await CanEditAllAsync(ct), ct));

        /// <summary> Build a MSEL with AI in this browser tab </summary>
        /// <remarks>
        /// One tab builds a MSEL at a time; the others are told who is building it. With takeOver, this tab takes the build
        /// from the one that has it, which is told to stop. Every writing step claims the build too, while no tab has it.
        /// </remarks>
        /// <param name="id">The MSEL</param>
        /// <param name="connectionId">This tab's hub connection id</param>
        /// <param name="takeOver">Take the build from another tab</param>
        /// <param name="ct"></param>
        [HttpPost("msels/{id}/ai-draft/claim")]
        [ProducesResponseType(typeof(MselBuildBuilder), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "claimMselBuild")]
        public async Task<IActionResult> ClaimAsync(Guid id, [FromQuery] string connectionId, [FromQuery] bool takeOver, CancellationToken ct) =>
            Ok(await _mselBuildService.ClaimAsync(id, connectionId, takeOver, await CanEditAllAsync(ct), ct));

        /// <summary> Stop building a MSEL with AI in this browser tab </summary>
        /// <remarks> Frees the build for another tab; what is written stays. </remarks>
        [HttpDelete("msels/{id}/ai-draft/claim")]
        [ProducesResponseType((int)HttpStatusCode.NoContent)]
        [SwaggerOperation(OperationId = "releaseMselBuild")]
        public async Task<IActionResult> ReleaseAsync(Guid id, [FromQuery] string connectionId, CancellationToken ct)
        {
            await _mselBuildService.ReleaseAsync(id, connectionId, await CanEditAllAsync(ct), ct);
            return NoContent();
        }

        private Task<bool> CanEditAllAsync(CancellationToken ct) =>
            _authorizationService.AuthorizeAsync([SystemPermission.EditMsels], ct);
    }
}
