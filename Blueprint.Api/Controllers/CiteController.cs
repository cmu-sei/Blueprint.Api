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

namespace Blueprint.Api.Controllers
{
    public class CiteController : BaseController
    {
        private readonly ICiteService _citeService;
        private readonly IAuthorizationService _authorizationService;

        public CiteController(ICiteService citeService, IAuthorizationService authorizationService)
        {
            _citeService = citeService;
            _authorizationService = authorizationService;
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

    }

}
