// Copyright 2023 Carnegie Mellon University. All Rights Reserved.
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
using Blueprint.Api.Services;
using Blueprint.Api.ViewModels;
using Swashbuckle.AspNetCore.Annotations;

namespace Blueprint.Api.Controllers
{
    public class CiteRoleController : BaseController
    {
        private readonly ICiteRoleService _citeRoleService;
        private readonly IAuthorizationService _authorizationService;

        public CiteRoleController(ICiteRoleService citeRoleService, IAuthorizationService authorizationService)
        {
            _citeRoleService = citeRoleService;
            _authorizationService = authorizationService;
        }

        /// <summary>
        /// Gets CiteRole Templates
        /// </summary>
        /// <remarks>
        /// Returns a list of CiteRole Templates
        /// </remarks>
        /// <param name="ct"></param>
        /// <returns></returns>
        [HttpGet("citeRoles/templates")]
        [ProducesResponseType(typeof(IEnumerable<CiteRole>), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "getCiteRoleTemplates")]
        public async Task<IActionResult> GetCiteRoleTemplates(CancellationToken ct)
        {
            var list = await _citeRoleService.GetTemplatesAsync(ct);
            return Ok(list);
        }

        /// <summary>
        /// Gets CiteRoles by msel
        /// </summary>
        /// <remarks>
        /// Returns a list of CiteRoles for the msel.
        /// </remarks>
        /// <param name="mselId"></param>
        /// <param name="ct"></param>
        /// <returns></returns>
        [HttpGet("msels/{mselId}/citeRoles")]
        [ProducesResponseType(typeof(IEnumerable<CiteRole>), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "getRolesByMsel")]
        public async Task<IActionResult> GetByMsel(Guid mselId, CancellationToken ct)
        {
            var list = await _citeRoleService.GetByMselAsync(mselId, ct);
            return Ok(list);
        }

        /// <summary>
        /// Gets a specific CiteRole by id
        /// </summary>
        /// <remarks>
        /// Returns the CiteRole with the id specified
        /// </remarks>
        /// <param name="id">The id of the CiteRole</param>
        /// <param name="ct"></param>
        /// <returns></returns>
        [HttpGet("citeRoles/{id}")]
        [ProducesResponseType(typeof(CiteRole), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "getCiteRole")]
        public async Task<IActionResult> Get(Guid id, CancellationToken ct)
        {
            var citeRole = await _citeRoleService.GetAsync(id, ct);

            if (citeRole == null)
                throw new EntityNotFoundException<CiteRole>();

            return Ok(citeRole);
        }

        /// <summary>
        /// Creates a new CiteRole
        /// </summary>
        /// <remarks>
        /// Creates a new CiteRole with the attributes specified
        /// <para />
        /// Accessible only to a ContentDeveloper or an Administrator
        /// </remarks>
        /// <param name="citeRole">The data used to create the CiteRole</param>
        /// <param name="ct"></param>
        [HttpPost("citeRoles")]
        [ProducesResponseType(typeof(CiteRole), (int)HttpStatusCode.Created)]
        [SwaggerOperation(OperationId = "createCiteRole")]
        public async Task<IActionResult> Create([FromBody] CiteRole citeRole, CancellationToken ct)
        {
            citeRole.CreatedBy = User.GetId();
            var createdCiteRole = await _citeRoleService.CreateAsync(citeRole, ct);
            return CreatedAtAction(nameof(this.Get), new { id = createdCiteRole.Id }, createdCiteRole);
        }

        /// <summary>
        /// Updates a  CiteRole
        /// </summary>
        /// <remarks>
        /// Updates a CiteRole with the attributes specified.
        /// The ID from the route MUST MATCH the ID contained in the citeRole parameter
        /// <para />
        /// Accessible only to a ContentDeveloper or an Administrator
        /// </remarks>
        /// <param name="id">The Id of the CiteRole to update</param>
        /// <param name="citeRole">The updated CiteRole values</param>
        /// <param name="ct"></param>
        [HttpPut("citeRoles/{id}")]
        [ProducesResponseType(typeof(CiteRole), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "updateCiteRole")]
        public async Task<IActionResult> Update([FromRoute] Guid id, [FromBody] CiteRole citeRole, CancellationToken ct)
        {
            citeRole.ModifiedBy = User.GetId();
            var updatedCiteRole = await _citeRoleService.UpdateAsync(id, citeRole, ct);
            return Ok(updatedCiteRole);
        }

        /// <summary>
        /// Deletes a  CiteRole
        /// </summary>
        /// <remarks>
        /// Deletes a  CiteRole with the specified id
        /// <para />
        /// Accessible only to a ContentDeveloper or an Administrator
        /// </remarks>
        /// <param name="id">The id of the CiteRole to delete</param>
        /// <param name="ct"></param>
        [HttpDelete("citeRoles/{id}")]
        [ProducesResponseType((int)HttpStatusCode.NoContent)]
        [SwaggerOperation(OperationId = "deleteCiteRole")]
        public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
        {
            await _citeRoleService.DeleteAsync(id, ct);
            return NoContent();
        }

    }
}
