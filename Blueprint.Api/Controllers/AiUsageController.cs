// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Blueprint.Api.Data.Enumerations;
using Blueprint.Api.Infrastructure.Exceptions;
using Blueprint.Api.Services;
using Blueprint.Api.ViewModels;
using Blueprint.Api.Infrastructure.Authorization;
using Microsoft.AspNetCore.Mvc;
using Swashbuckle.AspNetCore.Annotations;

namespace Blueprint.Api.Controllers
{
    public class AiUsageController : BaseController
    {
        private readonly IAiUsageService _aiUsageService;
        private readonly IBlueprintAuthorizationService _authorizationService;

        public AiUsageController(IAiUsageService aiUsageService, IBlueprintAuthorizationService authorizationService)
        {
            _aiUsageService = aiUsageService;
            _authorizationService = authorizationService;
        }

        /// <summary> What building a MSEL with AI used, and an estimate of what it cost </summary>
        /// <remarks>
        /// Every call to an AI model made for the MSEL, including those made while it was a draft, in all and by operation.
        /// The estimate uses the prices in the Ai:Prices setting. For the MSEL's owner, or with ViewMsels.
        /// </remarks>
        /// <param name="id"> The MSEL's ID </param>
        /// <param name="ct"></param>
        [HttpGet("msels/{id}/ai-usage")]
        [ProducesResponseType(typeof(AiUsageSummary), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "getMselAiUsage")]
        public async Task<IActionResult> GetByMselAsync(Guid id, CancellationToken ct)
        {
            var hasViewMsels = await _authorizationService.AuthorizeAsync([SystemPermission.ViewMsels], ct);
            return Ok(await _aiUsageService.GetByMselAsync(id, hasViewMsels, ct));
        }

        /// <summary> What building each MSEL with AI used, most expensive first </summary>
        /// <remarks> With ViewMsels. </remarks>
        /// <param name="since"> Only calls on or after this time </param>
        /// <param name="ct"></param>
        [HttpGet("ai-usage")]
        [ProducesResponseType(typeof(IEnumerable<AiUsageSummary>), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "getAiUsage")]
        public async Task<IActionResult> GetAllAsync([FromQuery] DateTime? since, CancellationToken ct)
        {
            if (!await _authorizationService.AuthorizeAsync([SystemPermission.ViewMsels], ct))
                throw new ForbiddenException();
            return Ok(await _aiUsageService.GetAllAsync(since, ct));
        }
    }
}
