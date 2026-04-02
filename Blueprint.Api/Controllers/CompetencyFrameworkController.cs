// Copyright 2025 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Blueprint.Api.Infrastructure.Extensions;
using Blueprint.Api.Infrastructure.Exceptions;
using Blueprint.Api.Infrastructure.Authorization;
using Blueprint.Api.Data.Enumerations;
using Blueprint.Api.Services;
using Blueprint.Api.ViewModels;
using Swashbuckle.AspNetCore.Annotations;

namespace Blueprint.Api.Controllers
{
    public class CompetencyFrameworkController : BaseController
    {
        private readonly ICompetencyFrameworkService _competencyFrameworkService;
        private readonly IBlueprintAuthorizationService _authorizationService;

        public CompetencyFrameworkController(ICompetencyFrameworkService competencyFrameworkService, IBlueprintAuthorizationService authorizationService)
        {
            _competencyFrameworkService = competencyFrameworkService;
            _authorizationService = authorizationService;
        }

        /// <summary>
        /// Gets all CompetencyFrameworks in the system
        /// </summary>
        /// <remarks>
        /// Returns a list of all of the CompetencyFrameworks in the system.
        /// </remarks>
        /// <returns></returns>
        [HttpGet("competencyFrameworks")]
        [ProducesResponseType(typeof(IEnumerable<CompetencyFramework>), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "getCompetencyFrameworks")]
        public async Task<IActionResult> Get(CancellationToken ct)
        {
            if (!await _authorizationService.AuthorizeAsync([SystemPermission.ViewCompetencyFrameworks], ct))
                throw new ForbiddenException();

            var list = await _competencyFrameworkService.GetAsync(ct);
            return Ok(list);
        }

        /// <summary>
        /// Gets a specific CompetencyFramework by id
        /// </summary>
        /// <remarks>
        /// Returns the CompetencyFramework with the id specified, including its elements and proficiency scales.
        /// </remarks>
        /// <param name="id">The id of the CompetencyFramework</param>
        /// <param name="ct"></param>
        /// <returns></returns>
        [HttpGet("competencyFrameworks/{id}")]
        [ProducesResponseType(typeof(CompetencyFramework), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "getCompetencyFramework")]
        public async Task<IActionResult> Get(Guid id, CancellationToken ct)
        {
            if (!await _authorizationService.AuthorizeAsync([SystemPermission.ViewCompetencyFrameworks], ct))
                throw new ForbiddenException();

            var competencyFramework = await _competencyFrameworkService.GetAsync(id, ct);

            if (competencyFramework == null)
                throw new EntityNotFoundException<CompetencyFramework>();

            return Ok(competencyFramework);
        }

        /// <summary>
        /// Creates a new CompetencyFramework
        /// </summary>
        /// <remarks>
        /// Creates a new CompetencyFramework with the attributes specified.
        /// Admin only.
        /// </remarks>
        /// <param name="competencyFramework">The data to create the CompetencyFramework with</param>
        /// <param name="ct"></param>
        [HttpPost("competencyFrameworks")]
        [ProducesResponseType(typeof(CompetencyFramework), (int)HttpStatusCode.Created)]
        [SwaggerOperation(OperationId = "createCompetencyFramework")]
        public async Task<IActionResult> Create([FromBody] CompetencyFramework competencyFramework, CancellationToken ct)
        {
            if (!await _authorizationService.AuthorizeAsync([SystemPermission.ManageCompetencyFrameworks], ct))
                throw new ForbiddenException();

            competencyFramework.CreatedBy = User.GetId();
            var createdCompetencyFramework = await _competencyFrameworkService.CreateAsync(competencyFramework, ct);
            return CreatedAtAction(nameof(this.Get), new { id = createdCompetencyFramework.Id }, createdCompetencyFramework);
        }

        /// <summary>
        /// Updates a CompetencyFramework
        /// </summary>
        /// <remarks>
        /// Updates a CompetencyFramework with the attributes specified.
        /// Admin only.
        /// </remarks>
        /// <param name="id">The Id of the CompetencyFramework to update</param>
        /// <param name="competencyFramework">The updated CompetencyFramework values</param>
        /// <param name="ct"></param>
        [HttpPut("competencyFrameworks/{id}")]
        [ProducesResponseType(typeof(CompetencyFramework), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "updateCompetencyFramework")]
        public async Task<IActionResult> Update([FromRoute] Guid id, [FromBody] CompetencyFramework competencyFramework, CancellationToken ct)
        {
            if (!await _authorizationService.AuthorizeAsync([SystemPermission.ManageCompetencyFrameworks], ct))
                throw new ForbiddenException();

            competencyFramework.ModifiedBy = User.GetId();
            var updatedCompetencyFramework = await _competencyFrameworkService.UpdateAsync(id, competencyFramework, ct);
            return Ok(updatedCompetencyFramework);
        }

        /// <summary>
        /// Deletes a CompetencyFramework
        /// </summary>
        /// <remarks>
        /// Deletes a CompetencyFramework with the specified id and all its elements and scales.
        /// Admin only.
        /// </remarks>
        /// <param name="id">The id of the CompetencyFramework to delete</param>
        /// <param name="ct"></param>
        [HttpDelete("competencyFrameworks/{id}")]
        [ProducesResponseType((int)HttpStatusCode.NoContent)]
        [SwaggerOperation(OperationId = "deleteCompetencyFramework")]
        public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
        {
            if (!await _authorizationService.AuthorizeAsync([SystemPermission.ManageCompetencyFrameworks], ct))
                throw new ForbiddenException();

            await _competencyFrameworkService.DeleteAsync(id, ct);
            return NoContent();
        }
    }
}
