// Copyright 2023 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Blueprint.Api.Services;
using Swashbuckle.AspNetCore.Annotations;
using Cite.Api.Client;
using System;
using Blueprint.Api.Infrastructure.Authorization;
using Blueprint.Api.ViewModels;
using SystemPermission = Blueprint.Api.Data.Enumerations.SystemPermission;

namespace Blueprint.Api.Controllers
{
    public class CiteController : BaseController
    {
        private readonly ICiteService _citeService;
        private readonly IAuthorizationService _authorizationService;
        private readonly ICiteSubmissionService _citeSubmissionService;
        private readonly IBlueprintAuthorizationService _blueprintAuthorizationService;

        public CiteController(
            ICiteService citeService,
            IAuthorizationService authorizationService,
            ICiteSubmissionService citeSubmissionService,
            IBlueprintAuthorizationService blueprintAuthorizationService)
        {
            _citeService = citeService;
            _authorizationService = authorizationService;
            _citeSubmissionService = citeSubmissionService;
            _blueprintAuthorizationService = blueprintAuthorizationService;
        }

        /// <summary>
        /// Gets all ScoringModels
        /// </summary>
        /// <remarks>
        /// Returns a list of all of the ScoringModels.
        /// </remarks>
        /// <param name="ct"></param>
        /// <returns></returns>
        [HttpGet("scoringmodels")]
        [ProducesResponseType(typeof(IEnumerable<ScoringModel>), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "getScoringModels")]
        public async Task<IActionResult> GetScoringModels(CancellationToken ct)
        {
            var list = await _citeService.GetScoringModelsAsync(ct);
            return Ok(list);
        }

        /// <summary>
        /// Gets all TeamTypes
        /// </summary>
        /// <remarks>
        /// Returns a list of all of the TeamTypes.
        /// </remarks>
        /// <param name="ct"></param>
        /// <returns></returns>
        [HttpGet("teamtypes")]
        [ProducesResponseType(typeof(IEnumerable<TeamType>), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "getTeamTypes")]
        public async Task<IActionResult> GetTeamTypes(CancellationToken ct)
        {
            var list = await _citeService.GetTeamTypesAsync(ct);
            return Ok(list);
        }

        /// <summary>
        /// Gets all TeamRoles from CITE
        /// </summary>
        /// <remarks>
        /// Returns a list of all of the TeamRoles.
        /// </remarks>
        /// <param name="ct"></param>
        /// <returns></returns>
        [HttpGet("teamroles")]
        [ProducesResponseType(typeof(IEnumerable<TeamRole>), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "getTeamRoles")]
        public async Task<IActionResult> GetTeamRoles(CancellationToken ct)
        {
            var list = await _citeService.GetTeamRolesAsync(ct);
            return Ok(list);
        }

        /// <summary>
        /// Gets a team's CITE answers for a move
        /// </summary>
        /// <remarks>
        /// Returns what the team answered on the MSEL's CITE scoring model for one move: per scoring category,
        /// the selected options, comments and score, plus the official and team-average submissions when the
        /// scoring model uses them. Team-level answers only; no participant's own submission.
        /// <para />
        /// When there is nothing to read, the status says why: the MSEL is not pushed to CITE, the team or
        /// the move is not in CITE, the move has not started, or CITE could not be read.
        /// <para />
        /// Accessible with the ViewMsels permission, or to the MSEL's Owners, Editors, Approvers and Evaluators
        /// </remarks>
        /// <param name="mselId">The MSEL</param>
        /// <param name="teamId">The MSEL team</param>
        /// <param name="moveNumber">The move number</param>
        /// <param name="ct"></param>
        /// <returns></returns>
        [HttpGet("msels/{mselId}/citeSubmissions")]
        [ProducesResponseType(typeof(CiteTeamResponses), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "getMselCiteTeamResponses")]
        public async Task<IActionResult> GetTeamResponses(Guid mselId, [FromQuery] Guid teamId, [FromQuery] int moveNumber, CancellationToken ct)
        {
            var hasSystemPermission = await _blueprintAuthorizationService.AuthorizeAsync([SystemPermission.ViewMsels], ct);
            var responses = await _citeSubmissionService.GetTeamResponsesAsync(mselId, teamId, moveNumber, hasSystemPermission, ct);
            return Ok(responses);
        }

    }

}
