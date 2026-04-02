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
    public class ProficiencyScaleController : BaseController
    {
        private readonly IProficiencyScaleService _proficiencyScaleService;
        private readonly IBlueprintAuthorizationService _authorizationService;

        public ProficiencyScaleController(IProficiencyScaleService proficiencyScaleService, IBlueprintAuthorizationService authorizationService)
        {
            _proficiencyScaleService = proficiencyScaleService;
            _authorizationService = authorizationService;
        }

        /// <summary>
        /// Gets ProficiencyScales by CompetencyFramework
        /// </summary>
        [HttpGet("competencyFrameworks/{frameworkId}/proficiencyScales")]
        [ProducesResponseType(typeof(IEnumerable<ProficiencyScale>), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "getProficiencyScalesByFramework")]
        public async Task<IActionResult> GetByFramework(Guid frameworkId, CancellationToken ct)
        {
            if (!await _authorizationService.AuthorizeAsync([SystemPermission.ViewCompetencyFrameworks], ct))
                throw new ForbiddenException();

            var list = await _proficiencyScaleService.GetByFrameworkAsync(frameworkId, ct);
            return Ok(list);
        }

        /// <summary>
        /// Gets a specific ProficiencyScale by id
        /// </summary>
        [HttpGet("proficiencyScales/{id}")]
        [ProducesResponseType(typeof(ProficiencyScale), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "getProficiencyScale")]
        public async Task<IActionResult> Get(Guid id, CancellationToken ct)
        {
            if (!await _authorizationService.AuthorizeAsync([SystemPermission.ViewCompetencyFrameworks], ct))
                throw new ForbiddenException();

            var item = await _proficiencyScaleService.GetAsync(id, ct);

            if (item == null)
                throw new EntityNotFoundException<ProficiencyScale>();

            return Ok(item);
        }

        /// <summary>
        /// Creates a new ProficiencyScale
        /// </summary>
        [HttpPost("proficiencyScales")]
        [ProducesResponseType(typeof(ProficiencyScale), (int)HttpStatusCode.Created)]
        [SwaggerOperation(OperationId = "createProficiencyScale")]
        public async Task<IActionResult> Create([FromBody] ProficiencyScale proficiencyScale, CancellationToken ct)
        {
            if (!await _authorizationService.AuthorizeAsync([SystemPermission.ManageCompetencyFrameworks], ct))
                throw new ForbiddenException();

            proficiencyScale.CreatedBy = User.GetId();
            var created = await _proficiencyScaleService.CreateAsync(proficiencyScale, ct);
            return CreatedAtAction(nameof(this.Get), new { id = created.Id }, created);
        }

        /// <summary>
        /// Updates a ProficiencyScale
        /// </summary>
        [HttpPut("proficiencyScales/{id}")]
        [ProducesResponseType(typeof(ProficiencyScale), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "updateProficiencyScale")]
        public async Task<IActionResult> Update([FromRoute] Guid id, [FromBody] ProficiencyScale proficiencyScale, CancellationToken ct)
        {
            if (!await _authorizationService.AuthorizeAsync([SystemPermission.ManageCompetencyFrameworks], ct))
                throw new ForbiddenException();

            proficiencyScale.ModifiedBy = User.GetId();
            var updated = await _proficiencyScaleService.UpdateAsync(id, proficiencyScale, ct);
            return Ok(updated);
        }

        /// <summary>
        /// Deletes a ProficiencyScale
        /// </summary>
        [HttpDelete("proficiencyScales/{id}")]
        [ProducesResponseType((int)HttpStatusCode.NoContent)]
        [SwaggerOperation(OperationId = "deleteProficiencyScale")]
        public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
        {
            if (!await _authorizationService.AuthorizeAsync([SystemPermission.ManageCompetencyFrameworks], ct))
                throw new ForbiddenException();

            await _proficiencyScaleService.DeleteAsync(id, ct);
            return NoContent();
        }
    }
}
