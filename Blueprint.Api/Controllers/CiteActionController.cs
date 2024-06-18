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
    public class CiteActionController : BaseController
    {
        private readonly ICiteActionService _citeActionService;
        private readonly IAuthorizationService _authorizationService;

        public CiteActionController(ICiteActionService citeActionService, IAuthorizationService authorizationService)
        {
            _citeActionService = citeActionService;
            _authorizationService = authorizationService;
        }

        /// <summary>
        /// Gets CiteAction Templates
        /// </summary>
        /// <remarks>
        /// Returns a list of CiteAction Templates
        /// </remarks>
        /// <param name="ct"></param>
        /// <returns></returns>
        [HttpGet("citeActions/templates")]
        [ProducesResponseType(typeof(IEnumerable<CiteAction>), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "getCiteActionTemplates")]
        public async Task<IActionResult> GetCiteActionTemplates(CancellationToken ct)
        {
            var list = await _citeActionService.GetTemplatesAsync(ct);
            return Ok(list);
        }

        /// <summary>
        /// Gets CiteActions by msel
        /// </summary>
        /// <remarks>
        /// Returns a list of CiteActions for the msel.
        /// </remarks>
        /// <param name="mselId"></param>
        /// <param name="ct"></param>
        /// <returns></returns>
        [HttpGet("msels/{mselId}/citeActions")]
        [ProducesResponseType(typeof(IEnumerable<CiteAction>), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "getActionsByMsel")]
        public async Task<IActionResult> GetByMsel(Guid mselId, CancellationToken ct)
        {
            var list = await _citeActionService.GetByMselAsync(mselId, ct);
            return Ok(list);
        }

        /// <summary>
        /// Gets a specific CiteAction by id
        /// </summary>
        /// <remarks>
        /// Returns the CiteAction with the id specified
        /// </remarks>
        /// <param name="id">The id of the CiteAction</param>
        /// <param name="ct"></param>
        /// <returns></returns>
        [HttpGet("citeActions/{id}")]
        [ProducesResponseType(typeof(CiteAction), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "getCiteAction")]
        public async Task<IActionResult> Get(Guid id, CancellationToken ct)
        {
            var citeAction = await _citeActionService.GetAsync(id, ct);

            if (citeAction == null)
                throw new EntityNotFoundException<CiteAction>();

            return Ok(citeAction);
        }

        /// <summary>
        /// Creates a new CiteAction
        /// </summary>
        /// <remarks>
        /// Creates a new CiteAction with the attributes specified
        /// <para />
        /// Accessible only to a ContentDeveloper or an Administrator
        /// </remarks>
        /// <param name="citeAction">The data used to create the CiteAction</param>
        /// <param name="ct"></param>
        [HttpPost("citeActions")]
        [ProducesResponseType(typeof(CiteAction), (int)HttpStatusCode.Created)]
        [SwaggerOperation(OperationId = "createCiteAction")]
        public async Task<IActionResult> Create([FromBody] CiteAction citeAction, CancellationToken ct)
        {
            citeAction.CreatedBy = User.GetId();
            var createdCiteAction = await _citeActionService.CreateAsync(citeAction, ct);
            return CreatedAtAction(nameof(this.Get), new { id = createdCiteAction.Id }, createdCiteAction);
        }

        /// <summary>
        /// Updates a  CiteAction
        /// </summary>
        /// <remarks>
        /// Updates a CiteAction with the attributes specified.
        /// The ID from the route MUST MATCH the ID contained in the citeAction parameter
        /// <para />
        /// Accessible only to a ContentDeveloper or an Administrator
        /// </remarks>
        /// <param name="id">The Id of the CiteAction to update</param>
        /// <param name="citeAction">The updated CiteAction values</param>
        /// <param name="ct"></param>
        [HttpPut("citeActions/{id}")]
        [ProducesResponseType(typeof(CiteAction), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "updateCiteAction")]
        public async Task<IActionResult> Update([FromRoute] Guid id, [FromBody] CiteAction citeAction, CancellationToken ct)
        {
            citeAction.ModifiedBy = User.GetId();
            var updatedCiteAction = await _citeActionService.UpdateAsync(id, citeAction, ct);
            return Ok(updatedCiteAction);
        }

        /// <summary>
        /// Deletes a  CiteAction
        /// </summary>
        /// <remarks>
        /// Deletes a  CiteAction with the specified id
        /// <para />
        /// Accessible only to a ContentDeveloper or an Administrator
        /// </remarks>
        /// <param name="id">The id of the CiteAction to delete</param>
        /// <param name="ct"></param>
        [HttpDelete("citeActions/{id}")]
        [ProducesResponseType((int)HttpStatusCode.NoContent)]
        [SwaggerOperation(OperationId = "deleteCiteAction")]
        public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
        {
            await _citeActionService.DeleteAsync(id, ct);
            return NoContent();
        }

    }
}
