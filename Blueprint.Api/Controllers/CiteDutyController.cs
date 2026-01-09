// Copyright 2023 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Blueprint.Api.Data.Enumerations;
using Blueprint.Api.Infrastructure.Authorization;
using Blueprint.Api.Infrastructure.Extensions;
using Blueprint.Api.Infrastructure.Exceptions;
using Blueprint.Api.Services;
using Blueprint.Api.ViewModels;
using Swashbuckle.AspNetCore.Annotations;

namespace Blueprint.Api.Controllers
{
    public class CiteDutyController : BaseController
    {
        private readonly ICiteDutyService _citeDutyService;
        private readonly IBlueprintAuthorizationService _authorizationService;

        public CiteDutyController(ICiteDutyService citeDutyService, IBlueprintAuthorizationService authorizationService)
        {
            _citeDutyService = citeDutyService;
            _authorizationService = authorizationService;
        }

        /// <summary>
        /// Gets CiteDuty Templates
        /// </summary>
        /// <remarks>
        /// Returns a list of CiteDuty Templates
        /// </remarks>
        /// <param name="ct"></param>
        /// <returns></returns>
        [HttpGet("citeDuties/templates")]
        [ProducesResponseType(typeof(IEnumerable<CiteDuty>), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "getCiteDutyTemplates")]
        public async Task<IActionResult> GetCiteDutyTemplates(CancellationToken ct)
        {
            var list = await _citeDutyService.GetTemplatesAsync(ct);
            return Ok(list);
        }

        /// <summary>
        /// Gets CiteDuties by msel
        /// </summary>
        /// <remarks>
        /// Returns a list of CiteDuties for the msel.
        /// </remarks>
        /// <param name="mselId"></param>
        /// <param name="ct"></param>
        /// <returns></returns>
        [HttpGet("msels/{mselId}/citeDuties")]
        [ProducesResponseType(typeof(IEnumerable<CiteDuty>), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "getDutiesByMsel")]
        public async Task<IActionResult> GetByMsel(Guid mselId, CancellationToken ct)
        {
            var hasSystemPermission = await _authorizationService.AuthorizeAsync([SystemPermission.ViewMsels], ct);
            var list = await _citeDutyService.GetByMselAsync(mselId, hasSystemPermission, ct);
            return Ok(list);
        }

        /// <summary>
        /// Gets a specific CiteDuty by id
        /// </summary>
        /// <remarks>
        /// Returns the CiteDuty with the id specified
        /// </remarks>
        /// <param name="id">The id of the CiteDuty</param>
        /// <param name="ct"></param>
        /// <returns></returns>
        [HttpGet("citeDuties/{id}")]
        [ProducesResponseType(typeof(CiteDuty), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "getCiteDuty")]
        public async Task<IActionResult> Get(Guid id, CancellationToken ct)
        {
            var hasSystemPermission = await _authorizationService.AuthorizeAsync([SystemPermission.ViewMsels], ct);
            var citeDuty = await _citeDutyService.GetAsync(id, hasSystemPermission, ct);

            if (citeDuty == null)
                throw new EntityNotFoundException<CiteDuty>();

            return Ok(citeDuty);
        }

        /// <summary>
        /// Creates a new CiteDuty
        /// </summary>
        /// <remarks>
        /// Creates a new CiteDuty with the attributes specified
        /// <para />
        /// Accessible only to a ContentDeveloper or an Administrator
        /// </remarks>
        /// <param name="citeDuty">The data used to create the CiteDuty</param>
        /// <param name="ct"></param>
        [HttpPost("citeDuties")]
        [ProducesResponseType(typeof(CiteDuty), (int)HttpStatusCode.Created)]
        [SwaggerOperation(OperationId = "createCiteDuty")]
        public async Task<IActionResult> Create([FromBody] CiteDuty citeDuty, CancellationToken ct)
        {
            var hasMselPermission = await _authorizationService.AuthorizeAsync([SystemPermission.EditMsels], ct);
            var hasCiteDutyPermission = await _authorizationService.AuthorizeAsync([SystemPermission.ManageCiteDuties], ct);
            citeDuty.CreatedBy = User.GetId();
            var createdCiteDuty = await _citeDutyService.CreateAsync(citeDuty, hasMselPermission, hasCiteDutyPermission, ct);
            return CreatedAtAction(nameof(this.Get), new { id = createdCiteDuty.Id }, createdCiteDuty);
        }

        /// <summary>
        /// Updates a  CiteDuty
        /// </summary>
        /// <remarks>
        /// Updates a CiteDuty with the attributes specified.
        /// The ID from the route MUST MATCH the ID contained in the citeDuty parameter
        /// <para />
        /// Accessible only to a ContentDeveloper or an Administrator
        /// </remarks>
        /// <param name="id">The Id of the CiteDuty to update</param>
        /// <param name="citeDuty">The updated CiteDuty values</param>
        /// <param name="ct"></param>
        [HttpPut("citeDuties/{id}")]
        [ProducesResponseType(typeof(CiteDuty), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "updateCiteDuty")]
        public async Task<IActionResult> Update([FromRoute] Guid id, [FromBody] CiteDuty citeDuty, CancellationToken ct)
        {
            var hasMselPermission = await _authorizationService.AuthorizeAsync([SystemPermission.EditMsels], ct);
            var hasCiteDutyPermission = await _authorizationService.AuthorizeAsync([SystemPermission.ManageCiteDuties], ct);
            citeDuty.ModifiedBy = User.GetId();
            var updatedCiteDuty = await _citeDutyService.UpdateAsync(id, citeDuty, hasMselPermission, hasCiteDutyPermission, ct);
            return Ok(updatedCiteDuty);
        }

        /// <summary>
        /// Deletes a  CiteDuty
        /// </summary>
        /// <remarks>
        /// Deletes a  CiteDuty with the specified id
        /// <para />
        /// Accessible only to a ContentDeveloper or an Administrator
        /// </remarks>
        /// <param name="id">The id of the CiteDuty to delete</param>
        /// <param name="ct"></param>
        [HttpDelete("citeDuties/{id}")]
        [ProducesResponseType((int)HttpStatusCode.NoContent)]
        [SwaggerOperation(OperationId = "deleteCiteDuty")]
        public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
        {
            var hasMselPermission = await _authorizationService.AuthorizeAsync([SystemPermission.EditMsels], ct);
            var hasCiteDutyPermission = await _authorizationService.AuthorizeAsync([SystemPermission.ManageCiteDuties], ct);
            await _citeDutyService.DeleteAsync(id, hasMselPermission, hasCiteDutyPermission, ct);
            return NoContent();
        }

    }
}
