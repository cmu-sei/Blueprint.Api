// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Blueprint.Api.Data.Enumerations;
using Blueprint.Api.Infrastructure.Authorization;
using Blueprint.Api.Services;
using Blueprint.Api.ViewModels;
using Swashbuckle.AspNetCore.Annotations;

namespace Blueprint.Api.Controllers
{
    public class MselScoringModelController : BaseController
    {
        private readonly IMselScoringModelService _mselScoringModelService;
        private readonly IBlueprintAuthorizationService _authorizationService;

        public MselScoringModelController(IMselScoringModelService mselScoringModelService, IBlueprintAuthorizationService authorizationService)
        {
            _mselScoringModelService = mselScoringModelService;
            _authorizationService = authorizationService;
        }

        /// <summary>
        /// Gets the scoring model a Msel authors itself
        /// </summary>
        /// <remarks>
        /// Returns the CITE scoring model the Msel authors in Blueprint, which pushing the Msel creates in CITE.
        /// A Msel without one, which may link a CITE scoring model instead, answers 204.
        /// </remarks>
        /// <param name="id">The id of the Msel</param>
        /// <param name="ct"></param>
        [HttpGet("msels/{id}/scoringmodel")]
        [ProducesResponseType(typeof(MselScoringModel), (int)HttpStatusCode.OK)]
        [ProducesResponseType((int)HttpStatusCode.NoContent)]
        [SwaggerOperation(OperationId = "getMselScoringModel")]
        public async Task<IActionResult> Get(Guid id, CancellationToken ct)
        {
            var hasSystemPermission = await _authorizationService.AuthorizeAsync([SystemPermission.ViewMsels], ct);
            var hasCreateMselsPermission = await _authorizationService.AuthorizeAsync([SystemPermission.CreateMsels], ct);
            var scoringModel = await _mselScoringModelService.GetAsync(id, hasSystemPermission, hasCreateMselsPermission, ct);
            return scoringModel == null ? NoContent() : Ok(scoringModel);
        }

        /// <summary>
        /// Replaces the scoring model a Msel authors itself
        /// </summary>
        /// <remarks>
        /// Stores the scoring model as the Msel's own, in place of any it had, and stops the Msel linking a CITE
        /// scoring model. Categories and options are kept in the order sent. Not while the Msel is pushed.
        /// </remarks>
        /// <param name="id">The id of the Msel</param>
        /// <param name="scoringModel">The scoring model</param>
        /// <param name="ct"></param>
        [HttpPut("msels/{id}/scoringmodel")]
        [ProducesResponseType(typeof(MselScoringModel), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "replaceMselScoringModel")]
        public async Task<IActionResult> Replace(Guid id, [FromBody] MselScoringModel scoringModel, CancellationToken ct)
        {
            var hasSystemPermission = await _authorizationService.AuthorizeAsync([SystemPermission.EditMsels], ct);
            var stored = await _mselScoringModelService.ReplaceAsync(id, scoringModel, hasSystemPermission, ct);
            return Ok(stored);
        }

        /// <summary>
        /// Creates a Msel's own scoring model from a preset
        /// </summary>
        /// <remarks>
        /// Replaces any scoring model the Msel authors with the preset, and stops the Msel linking a CITE scoring
        /// model. QuestionsByMove gives each of the Msel's moves a category for its discussion questions, and the
        /// last move an after-action review; the planner adds the questions.
        /// </remarks>
        /// <param name="id">The id of the Msel</param>
        /// <param name="preset">The preset to start from</param>
        /// <param name="ct"></param>
        [HttpPost("msels/{id}/scoringmodel/presets/{preset}")]
        [ProducesResponseType(typeof(MselScoringModel), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "createMselScoringModelFromPreset")]
        public async Task<IActionResult> CreateFromPreset(Guid id, ScoringModelPreset preset, CancellationToken ct)
        {
            var hasSystemPermission = await _authorizationService.AuthorizeAsync([SystemPermission.EditMsels], ct);
            var stored = await _mselScoringModelService.CreateFromPresetAsync(id, preset, hasSystemPermission, ct);
            return Ok(stored);
        }

        /// <summary>
        /// Copies a CITE scoring model to be a Msel's own
        /// </summary>
        /// <remarks>
        /// Reads the CITE scoring model as the caller, so the caller must be able to view it in CITE, and stores a
        /// copy as the Msel's own scoring model, to edit in Blueprint. The Msel stops linking a CITE scoring model.
        /// </remarks>
        /// <param name="id">The id of the Msel</param>
        /// <param name="scoringModelId">The id of the CITE scoring model</param>
        /// <param name="ct"></param>
        [HttpPost("msels/{id}/scoringmodel/cite/{scoringModelId}")]
        [ProducesResponseType(typeof(MselScoringModel), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "copyMselScoringModelFromCite")]
        public async Task<IActionResult> CopyFromCite(Guid id, Guid scoringModelId, CancellationToken ct)
        {
            var hasSystemPermission = await _authorizationService.AuthorizeAsync([SystemPermission.EditMsels], ct);
            var stored = await _mselScoringModelService.CopyFromCiteAsync(id, scoringModelId, hasSystemPermission, ct);
            return Ok(stored);
        }

        /// <summary>
        /// Deletes the scoring model a Msel authors itself
        /// </summary>
        /// <remarks>
        /// Deletes the Msel's own scoring model, so that it can link a CITE scoring model instead.
        /// </remarks>
        /// <param name="id">The id of the Msel</param>
        /// <param name="ct"></param>
        [HttpDelete("msels/{id}/scoringmodel")]
        [ProducesResponseType((int)HttpStatusCode.NoContent)]
        [SwaggerOperation(OperationId = "deleteMselScoringModel")]
        public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
        {
            var hasSystemPermission = await _authorizationService.AuthorizeAsync([SystemPermission.EditMsels], ct);
            await _mselScoringModelService.DeleteAsync(id, hasSystemPermission, ct);
            return NoContent();
        }
    }
}
