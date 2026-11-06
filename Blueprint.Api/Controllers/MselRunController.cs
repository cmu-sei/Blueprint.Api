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
using Blueprint.Api.Services;
using Blueprint.Api.ViewModels;
using Swashbuckle.AspNetCore.Annotations;

namespace Blueprint.Api.Controllers
{
    public class MselRunController : BaseController
    {
        private readonly IMselRunService _mselRunService;
        private readonly IBlueprintAuthorizationService _authorizationService;

        public MselRunController(IMselRunService mselRunService, IBlueprintAuthorizationService authorizationService)
        {
            _mselRunService = mselRunService;
            _authorizationService = authorizationService;
        }

        /// <summary>
        /// Gets the runs of a MSEL
        /// </summary>
        /// <remarks>
        /// Returns every run of the MSEL, newest first: one per push, with the ids it created in the
        /// integrated applications, which are kept after its integrations are removed.
        /// Requires ViewMsels, or the Owner, Editor, Approver or Evaluator role on the MSEL.
        /// </remarks>
        /// <param name="mselId">The id of the MSEL</param>
        /// <param name="ct"></param>
        [HttpGet("msels/{mselId}/runs")]
        [ProducesResponseType(typeof(IEnumerable<MselRun>), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "getMselRuns")]
        public async Task<IActionResult> GetByMsel(Guid mselId, CancellationToken ct)
        {
            var hasSystemPermission = await _authorizationService.AuthorizeAsync([SystemPermission.ViewMsels], ct);
            var list = await _mselRunService.GetByMselAsync(mselId, hasSystemPermission, ct);
            return Ok(list);
        }

        /// <summary>
        /// Gets a run of a MSEL
        /// </summary>
        /// <remarks>
        /// Returns the run with the id specified.
        /// Requires ViewMsels, or the Owner, Editor, Approver or Evaluator role on the run's MSEL.
        /// </remarks>
        /// <param name="id">The id of the run</param>
        /// <param name="ct"></param>
        [HttpGet("runs/{id}")]
        [ProducesResponseType(typeof(MselRun), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "getMselRun")]
        public async Task<IActionResult> Get(Guid id, CancellationToken ct)
        {
            var hasSystemPermission = await _authorizationService.AuthorizeAsync([SystemPermission.ViewMsels], ct);
            var run = await _mselRunService.GetAsync(id, hasSystemPermission, ct);
            return Ok(run);
        }
    }
}
