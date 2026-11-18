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
using Blueprint.Api.Infrastructure.Exceptions;
using Blueprint.Api.Services;
using Blueprint.Api.ViewModels;
using Swashbuckle.AspNetCore.Annotations;

namespace Blueprint.Api.Controllers
{
    /// <summary>
    /// Technique framework definitions. Reads are open to any authenticated user; creating, editing and deleting
    /// them is managed with the competency frameworks.
    /// </summary>
    public class TechniqueFrameworkController : BaseController
    {
        private readonly ITechniqueFrameworkService _techniqueFrameworkService;
        private readonly IBlueprintAuthorizationService _authorizationService;

        public TechniqueFrameworkController(ITechniqueFrameworkService techniqueFrameworkService, IBlueprintAuthorizationService authorizationService)
        {
            _techniqueFrameworkService = techniqueFrameworkService;
            _authorizationService = authorizationService;
        }

        /// <summary>
        /// Gets all technique framework definitions
        /// </summary>
        /// <param name="ct"></param>
        [HttpGet("techniqueframeworks")]
        [ProducesResponseType(typeof(IEnumerable<TechniqueFramework>), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "getTechniqueFrameworks")]
        public async Task<IActionResult> Get(CancellationToken ct)
        {
            var list = await _techniqueFrameworkService.GetAsync(ct);
            return Ok(list);
        }

        /// <summary>
        /// Gets a specific technique framework definition by id
        /// </summary>
        /// <param name="id">The id of the framework</param>
        /// <param name="ct"></param>
        [HttpGet("techniqueframeworks/{id}")]
        [ProducesResponseType(typeof(TechniqueFramework), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "getTechniqueFramework")]
        public async Task<IActionResult> Get(Guid id, CancellationToken ct)
        {
            var framework = await _techniqueFrameworkService.GetAsync(id, ct);
            return Ok(framework);
        }

        /// <summary>
        /// Creates a technique framework definition
        /// </summary>
        /// <remarks>
        /// Also how an exported definition is imported. The domain must be unique, at least one match condition is
        /// required, and the URLs must be http or https addresses.
        /// </remarks>
        /// <param name="framework">The definition</param>
        /// <param name="ct"></param>
        [HttpPost("techniqueframeworks")]
        [ProducesResponseType(typeof(TechniqueFramework), (int)HttpStatusCode.Created)]
        [SwaggerOperation(OperationId = "createTechniqueFramework")]
        public async Task<IActionResult> Create([FromBody] TechniqueFramework framework, CancellationToken ct)
        {
            if (!await _authorizationService.AuthorizeAsync([SystemPermission.ManageCompetencyFrameworks], ct))
                throw new ForbiddenException();

            var created = await _techniqueFrameworkService.CreateAsync(framework, ct);
            return CreatedAtAction(nameof(Get), new { id = created.Id }, created);
        }

        /// <summary>
        /// Updates a technique framework definition
        /// </summary>
        /// <remarks>
        /// Releases already imported keep their techniques; what they show from the framework follows the edit. The
        /// domain and family of a framework with releases cannot change.
        /// </remarks>
        /// <param name="id">The id of the framework</param>
        /// <param name="framework">The definition</param>
        /// <param name="ct"></param>
        [HttpPut("techniqueframeworks/{id}")]
        [ProducesResponseType(typeof(TechniqueFramework), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "updateTechniqueFramework")]
        public async Task<IActionResult> Update(Guid id, [FromBody] TechniqueFramework framework, CancellationToken ct)
        {
            if (!await _authorizationService.AuthorizeAsync([SystemPermission.ManageCompetencyFrameworks], ct))
                throw new ForbiddenException();

            var updated = await _techniqueFrameworkService.UpdateAsync(id, framework, ct);
            return Ok(updated);
        }

        /// <summary>
        /// Deletes a technique framework definition
        /// </summary>
        /// <remarks>
        /// Its releases stay, with no framework.
        /// </remarks>
        /// <param name="id">The id of the framework</param>
        /// <param name="ct"></param>
        [HttpDelete("techniqueframeworks/{id}")]
        [ProducesResponseType((int)HttpStatusCode.NoContent)]
        [SwaggerOperation(OperationId = "deleteTechniqueFramework")]
        public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
        {
            if (!await _authorizationService.AuthorizeAsync([SystemPermission.ManageCompetencyFrameworks], ct))
                throw new ForbiddenException();

            await _techniqueFrameworkService.DeleteAsync(id, ct);
            return NoContent();
        }
    }
}
