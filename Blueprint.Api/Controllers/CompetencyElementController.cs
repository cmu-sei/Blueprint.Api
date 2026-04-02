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
    public class CompetencyElementController : BaseController
    {
        private readonly ICompetencyElementService _competencyElementService;
        private readonly IBlueprintAuthorizationService _authorizationService;

        public CompetencyElementController(ICompetencyElementService competencyElementService, IBlueprintAuthorizationService authorizationService)
        {
            _competencyElementService = competencyElementService;
            _authorizationService = authorizationService;
        }

        /// <summary>
        /// Gets CompetencyElements by CompetencyFramework
        /// </summary>
        [HttpGet("competencyFrameworks/{frameworkId}/competencyElements")]
        [ProducesResponseType(typeof(IEnumerable<CompetencyElement>), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "getCompetencyElementsByFramework")]
        public async Task<IActionResult> GetByFramework(Guid frameworkId, CancellationToken ct)
        {
            if (!await _authorizationService.AuthorizeAsync([SystemPermission.ViewCompetencyFrameworks], ct))
                throw new ForbiddenException();

            var list = await _competencyElementService.GetByFrameworkAsync(frameworkId, ct);
            return Ok(list);
        }

        /// <summary>
        /// Gets a specific CompetencyElement by id
        /// </summary>
        [HttpGet("competencyElements/{id}")]
        [ProducesResponseType(typeof(CompetencyElement), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "getCompetencyElement")]
        public async Task<IActionResult> Get(Guid id, CancellationToken ct)
        {
            if (!await _authorizationService.AuthorizeAsync([SystemPermission.ViewCompetencyFrameworks], ct))
                throw new ForbiddenException();

            var item = await _competencyElementService.GetAsync(id, ct);

            if (item == null)
                throw new EntityNotFoundException<CompetencyElement>();

            return Ok(item);
        }

        /// <summary>
        /// Creates a new CompetencyElement
        /// </summary>
        [HttpPost("competencyElements")]
        [ProducesResponseType(typeof(CompetencyElement), (int)HttpStatusCode.Created)]
        [SwaggerOperation(OperationId = "createCompetencyElement")]
        public async Task<IActionResult> Create([FromBody] CompetencyElement competencyElement, CancellationToken ct)
        {
            if (!await _authorizationService.AuthorizeAsync([SystemPermission.ManageCompetencyFrameworks], ct))
                throw new ForbiddenException();

            competencyElement.CreatedBy = User.GetId();
            var created = await _competencyElementService.CreateAsync(competencyElement, ct);
            return CreatedAtAction(nameof(this.Get), new { id = created.Id }, created);
        }

        /// <summary>
        /// Updates a CompetencyElement
        /// </summary>
        [HttpPut("competencyElements/{id}")]
        [ProducesResponseType(typeof(CompetencyElement), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "updateCompetencyElement")]
        public async Task<IActionResult> Update([FromRoute] Guid id, [FromBody] CompetencyElement competencyElement, CancellationToken ct)
        {
            if (!await _authorizationService.AuthorizeAsync([SystemPermission.ManageCompetencyFrameworks], ct))
                throw new ForbiddenException();

            competencyElement.ModifiedBy = User.GetId();
            var updated = await _competencyElementService.UpdateAsync(id, competencyElement, ct);
            return Ok(updated);
        }

        /// <summary>
        /// Deletes a CompetencyElement
        /// </summary>
        [HttpDelete("competencyElements/{id}")]
        [ProducesResponseType((int)HttpStatusCode.NoContent)]
        [SwaggerOperation(OperationId = "deleteCompetencyElement")]
        public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
        {
            if (!await _authorizationService.AuthorizeAsync([SystemPermission.ManageCompetencyFrameworks], ct))
                throw new ForbiddenException();

            await _competencyElementService.DeleteAsync(id, ct);
            return NoContent();
        }
    }
}
