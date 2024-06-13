// Copyright 2024 Carnegie Mellon University. All Rights Reserved.
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
    public class InjectTypeController : BaseController
    {
        private readonly IInjectTypeService _injectTypeService;
        private readonly IAuthorizationService _authorizationService;

        public InjectTypeController(IInjectTypeService injectTypeService, IAuthorizationService authorizationService)
        {
            _injectTypeService = injectTypeService;
            _authorizationService = authorizationService;
        }

        /// <summary>
        /// Gets all InjectType in the system
        /// </summary>
        /// <remarks>
        /// Returns a list of all of the InjectTypes in the system.
        /// <para />
        /// Only accessible to a SuperUser
        /// </remarks>
        /// <returns></returns>
        [HttpGet("injectTypes")]
        [ProducesResponseType(typeof(IEnumerable<InjectType>), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "getInjectTypes")]
        public async Task<IActionResult> Get(CancellationToken ct)
        {
            var list = await _injectTypeService.GetAsync(ct);
            return Ok(list);
        }

        /// <summary>
        /// Gets a specific InjectType by id
        /// </summary>
        /// <remarks>
        /// Returns the InjectType with the id specified
        /// <para />
        /// Accessible to a SuperUser or a User that is a member of a Team within the specified InjectType
        /// </remarks>
        /// <param name="id">The id of the InjectType</param>
        /// <param name="ct"></param>
        /// <returns></returns>
        [HttpGet("injectTypes/{id}")]
        [ProducesResponseType(typeof(InjectType), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "getInjectType")]
        public async Task<IActionResult> Get(Guid id, CancellationToken ct)
        {
            var injectType = await _injectTypeService.GetAsync(id, ct);

            if (injectType == null)
                throw new EntityNotFoundException<InjectType>();

            return Ok(injectType);
        }

        /// <summary>
        /// Creates a new InjectType
        /// </summary>
        /// <remarks>
        /// Creates a new InjectType with the attributes specified
        /// <para />
        /// Accessible only to a SuperUser or an Administrator
        /// </remarks>
        /// <param name="injectType">The data to create the InjectType with</param>
        /// <param name="ct"></param>
        [HttpPost("injectTypes")]
        [ProducesResponseType(typeof(InjectType), (int)HttpStatusCode.Created)]
        [SwaggerOperation(OperationId = "createInjectType")]
        public async Task<IActionResult> Create([FromBody] InjectType injectType, CancellationToken ct)
        {
            injectType.CreatedBy = User.GetId();
            var createdInjectType = await _injectTypeService.CreateAsync(injectType, ct);
            return CreatedAtAction(nameof(this.Get), new { id = createdInjectType.Id }, createdInjectType);
        }

        /// <summary>
        /// Updates a InjectType
        /// </summary>
        /// <remarks>
        /// Updates a InjectType with the attributes specified
        /// <para />
        /// Accessible only to a SuperUser or a User on an Admin Team within the specified InjectType
        /// </remarks>
        /// <param name="id">The Id of the Exericse to update</param>
        /// <param name="injectType">The updated InjectType values</param>
        /// <param name="ct"></param>
        [HttpPut("injectTypes/{id}")]
        [ProducesResponseType(typeof(InjectType), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "updateInjectType")]
        public async Task<IActionResult> Update([FromRoute] Guid id, [FromBody] InjectType injectType, CancellationToken ct)
        {
            injectType.ModifiedBy = User.GetId();
            var updatedInjectType = await _injectTypeService.UpdateAsync(id, injectType, ct);
            return Ok(updatedInjectType);
        }

        /// <summary>
        /// Deletes a InjectType
        /// </summary>
        /// <remarks>
        /// Deletes a InjectType with the specified id
        /// <para />
        /// Accessible only to a SuperUser or a User on an Admin Team within the specified InjectType
        /// </remarks>
        /// <param name="id">The id of the InjectType to delete</param>
        /// <param name="ct"></param>
        [HttpDelete("injectTypes/{id}")]
        [ProducesResponseType((int)HttpStatusCode.NoContent)]
        [SwaggerOperation(OperationId = "deleteInjectType")]
        public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
        {
            await _injectTypeService.DeleteAsync(id, ct);
            return NoContent();
        }

    }
}
