// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Blueprint.Api.Services;
using Swashbuckle.AspNetCore.Annotations;

namespace Blueprint.Api.Controllers
{
    [AllowAnonymous]
    public class LmtController : BaseController
    {
        private readonly ILmtService _lmtService;

        public LmtController(ILmtService lmtService)
        {
            _lmtService = lmtService;
        }

        /// <summary>
        /// Gets IEEE 2881 (LMT) metadata for an MSEL as JSON-LD
        /// </summary>
        /// <remarks>
        /// Returns LMT (Learning Metadata) as JSON-LD conforming to IEEE 2881 schema.
        /// This endpoint is publicly accessible to enable catalog discovery by LMS systems,
        /// PCTE registries, and other TLA components. The JSON-LD includes:
        /// - Exercise name, description, objectives
        /// - Competencies assessed (lrmi:assesses from MselCompetency associations)
        /// - Educational metadata (difficulty, purpose, mode, keywords)
        /// - Prerequisites (if configured)
        ///
        /// Any LMS that supports LTI 1.3 Deep Linking or Content-Item can consume this
        /// metadata to auto-link competencies, tags, and prerequisites when importing
        /// the exercise.
        /// </remarks>
        [HttpGet("lmt/resource/{mselId}")]
        [ProducesResponseType(typeof(string), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "getLmtResource")]
        public async Task<IActionResult> GetLmtResource(Guid mselId, CancellationToken ct)
        {
            var jsonLd = await _lmtService.GetLmtResourceAsync(mselId, ct);
            if (jsonLd == null)
                return NotFound();
            return Content(jsonLd, "application/ld+json");
        }
    }
}
