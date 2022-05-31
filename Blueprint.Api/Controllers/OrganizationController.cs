// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Blueprint.Api.Infrastructure.Extensions;
using Blueprint.Api.Infrastructure.Exceptions;
using Blueprint.Api.Infrastructure.QueryParameters;
using Blueprint.Api.Services;
using Blueprint.Api.ViewModels;
using Swashbuckle.AspNetCore.Annotations;

namespace Blueprint.Api.Controllers
{
    public class OrganizationController : BaseController
    {
        private readonly IOrganizationService _organizationService;
        private readonly IAuthorizationService _authorizationService;

        public OrganizationController(IOrganizationService organizationService, IAuthorizationService authorizationService)
        {
            _organizationService = organizationService;
            _authorizationService = authorizationService;
        }

        /// <summary>
        /// Gets Organizations by msel
        /// </summary>
        /// <remarks>
        /// Returns a list of Organizations for the msel.
        /// </remarks>
        /// <param name="mselId"></param>
        /// <param name="ct"></param>
        /// <returns></returns>
        [HttpGet("msels/{mselId}/organizations")]
        [ProducesResponseType(typeof(IEnumerable<Organization>), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "getByMsel")]
        public async Task<IActionResult> GetByMsel(Guid mselId, CancellationToken ct)
        {
            var list = await _organizationService.GetByMselAsync(mselId, ct);
            return Ok(list);
        }

        /// <summary>
        /// Gets a specific Organization by id
        /// </summary>
        /// <remarks>
        /// Returns the Organization with the id specified
        /// </remarks>
        /// <param name="id">The id of the Organization</param>
        /// <param name="ct"></param>
        /// <returns></returns>
        [HttpGet("organizations/{id}")]
        [ProducesResponseType(typeof(Organization), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "getOrganization")]
        public async Task<IActionResult> Get(Guid id, CancellationToken ct)
        {
            var organization = await _organizationService.GetAsync(id, ct);

            if (organization == null)
                throw new EntityNotFoundException<Organization>();

            return Ok(organization);
        }

        /// <summary>
        /// Creates a new Organization
        /// </summary>
        /// <remarks>
        /// Creates a new Organization with the attributes specified
        /// <para />
        /// Accessible only to a ContentDeveloper or an Administrator
        /// </remarks>
        /// <param name="organization">The data used to create the Organization</param>
        /// <param name="ct"></param>
        [HttpPost("organizations")]
        [ProducesResponseType(typeof(Organization), (int)HttpStatusCode.Created)]
        [SwaggerOperation(OperationId = "createOrganization")]
        public async Task<IActionResult> Create([FromBody] Organization organization, CancellationToken ct)
        {
            organization.CreatedBy = User.GetId();
            var createdOrganization = await _organizationService.CreateAsync(organization, ct);
            return CreatedAtAction(nameof(this.Get), new { id = createdOrganization.Id }, createdOrganization);
        }

        /// <summary>
        /// Updates a  Organization
        /// </summary>
        /// <remarks>
        /// Updates a Organization with the attributes specified.
        /// The ID from the route MUST MATCH the ID contained in the organization parameter
        /// <para />
        /// Accessible only to a ContentDeveloper or an Administrator
        /// </remarks>
        /// <param name="id">The Id of the Organization to update</param>
        /// <param name="organization">The updated Organization values</param>
        /// <param name="ct"></param>
        [HttpPut("organizations/{id}")]
        [ProducesResponseType(typeof(Organization), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "updateOrganization")]
        public async Task<IActionResult> Update([FromRoute] Guid id, [FromBody] Organization organization, CancellationToken ct)
        {
            organization.ModifiedBy = User.GetId();
            var updatedOrganization = await _organizationService.UpdateAsync(id, organization, ct);
            return Ok(updatedOrganization);
        }

        /// <summary>
        /// Deletes a  Organization
        /// </summary>
        /// <remarks>
        /// Deletes a  Organization with the specified id
        /// <para />
        /// Accessible only to a ContentDeveloper or an Administrator
        /// </remarks>
        /// <param name="id">The id of the Organization to delete</param>
        /// <param name="ct"></param>
        [HttpDelete("organizations/{id}")]
        [ProducesResponseType((int)HttpStatusCode.NoContent)]
        [SwaggerOperation(OperationId = "deleteOrganization")]
        public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
        {
            await _organizationService.DeleteAsync(id, ct);
            return NoContent();
        }

    }
}

