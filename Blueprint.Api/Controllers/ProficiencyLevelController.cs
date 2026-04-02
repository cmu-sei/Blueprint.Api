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
    public class ProficiencyLevelController : BaseController
    {
        private readonly IProficiencyLevelService _proficiencyLevelService;
        private readonly IBlueprintAuthorizationService _authorizationService;

        public ProficiencyLevelController(IProficiencyLevelService proficiencyLevelService, IBlueprintAuthorizationService authorizationService)
        {
            _proficiencyLevelService = proficiencyLevelService;
            _authorizationService = authorizationService;
        }

        /// <summary>
        /// Gets ProficiencyLevels by ProficiencyScale
        /// </summary>
        [HttpGet("proficiencyScales/{scaleId}/proficiencyLevels")]
        [ProducesResponseType(typeof(IEnumerable<ProficiencyLevel>), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "getProficiencyLevelsByScale")]
        public async Task<IActionResult> GetByScale(Guid scaleId, CancellationToken ct)
        {
            if (!await _authorizationService.AuthorizeAsync([SystemPermission.ViewCompetencyFrameworks], ct))
                throw new ForbiddenException();

            var list = await _proficiencyLevelService.GetByScaleAsync(scaleId, ct);
            return Ok(list);
        }

        /// <summary>
        /// Gets a specific ProficiencyLevel by id
        /// </summary>
        [HttpGet("proficiencyLevels/{id}")]
        [ProducesResponseType(typeof(ProficiencyLevel), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "getProficiencyLevel")]
        public async Task<IActionResult> Get(Guid id, CancellationToken ct)
        {
            if (!await _authorizationService.AuthorizeAsync([SystemPermission.ViewCompetencyFrameworks], ct))
                throw new ForbiddenException();

            var item = await _proficiencyLevelService.GetAsync(id, ct);

            if (item == null)
                throw new EntityNotFoundException<ProficiencyLevel>();

            return Ok(item);
        }

        /// <summary>
        /// Creates a new ProficiencyLevel
        /// </summary>
        [HttpPost("proficiencyLevels")]
        [ProducesResponseType(typeof(ProficiencyLevel), (int)HttpStatusCode.Created)]
        [SwaggerOperation(OperationId = "createProficiencyLevel")]
        public async Task<IActionResult> Create([FromBody] ProficiencyLevel proficiencyLevel, CancellationToken ct)
        {
            if (!await _authorizationService.AuthorizeAsync([SystemPermission.ManageCompetencyFrameworks], ct))
                throw new ForbiddenException();

            proficiencyLevel.CreatedBy = User.GetId();
            var created = await _proficiencyLevelService.CreateAsync(proficiencyLevel, ct);
            return CreatedAtAction(nameof(this.Get), new { id = created.Id }, created);
        }

        /// <summary>
        /// Updates a ProficiencyLevel
        /// </summary>
        [HttpPut("proficiencyLevels/{id}")]
        [ProducesResponseType(typeof(ProficiencyLevel), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "updateProficiencyLevel")]
        public async Task<IActionResult> Update([FromRoute] Guid id, [FromBody] ProficiencyLevel proficiencyLevel, CancellationToken ct)
        {
            if (!await _authorizationService.AuthorizeAsync([SystemPermission.ManageCompetencyFrameworks], ct))
                throw new ForbiddenException();

            proficiencyLevel.ModifiedBy = User.GetId();
            var updated = await _proficiencyLevelService.UpdateAsync(id, proficiencyLevel, ct);
            return Ok(updated);
        }

        /// <summary>
        /// Deletes a ProficiencyLevel
        /// </summary>
        [HttpDelete("proficiencyLevels/{id}")]
        [ProducesResponseType((int)HttpStatusCode.NoContent)]
        [SwaggerOperation(OperationId = "deleteProficiencyLevel")]
        public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
        {
            if (!await _authorizationService.AuthorizeAsync([SystemPermission.ManageCompetencyFrameworks], ct))
                throw new ForbiddenException();

            await _proficiencyLevelService.DeleteAsync(id, ct);
            return NoContent();
        }
    }
}
