// Copyright 2024 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Blueprint.Api.Services;
using Swashbuckle.AspNetCore.Annotations;

namespace Blueprint.Api.Controllers
{
    public class XApiController : BaseController
    {
        private readonly IXApiService _xApiService;

        public XApiController(IXApiService xApiService)
        {
            _xApiService = xApiService;
        }

        /// <summary>
        /// Gets xAPI statements for an MSEL from the LRS
        /// </summary>
        /// <remarks>
        /// Queries the LRS for xAPI statements related to the MSEL's integrations.
        /// When source is omitted, queries all configured integrations (Blueprint, CITE, Steamfitter, Player, Gallery).
        /// When source is specified, queries only that integration's activity ID.
        /// </remarks>
        [HttpGet("xapi/statements")]
        [ProducesResponseType(typeof(string), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "getXApiStatements")]
        public async Task<IActionResult> GetStatements(
            [FromQuery] Guid mselId,
            [FromQuery] DateTime? since,
            [FromQuery] DateTime? until,
            [FromQuery] int limit = 100,
            [FromQuery] string source = null,
            CancellationToken ct = default)
        {
            var result = await _xApiService.GetStatementsAsync(mselId, since, until, limit, source, ct);
            return Content(result, "application/json");
        }
    }
}
