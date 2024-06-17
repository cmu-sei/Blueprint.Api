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
using Swashbuckle.AspNetCore.Annotations;

namespace Blueprint.Api.Controllers
{
    public class InjectController : BaseController
    {
        private readonly IInjectService _injectService;
        private readonly IAuthorizationService _authorizationService;

        public InjectController(IInjectService injectService, IAuthorizationService authorizationService)
        {
            _injectService = injectService;
            _authorizationService = authorizationService;
        }

        /// <summary>
        /// Gets Injects for a Catalog
        /// </summary>
        /// <remarks>
        /// Returns a list of Injects for the Catalog.
        /// </remarks>
        /// <param name="catalogId">The ID of the Catalog</param>
        /// <param name="ct"></param>
        /// <returns></returns>
        [HttpGet("catalogs/{catalogId}/injects")]
        [ProducesResponseType(typeof(IEnumerable<ViewModels.Injectm>), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "getInjectsByCatalog")]
        public async Task<IActionResult> GetByCatalog(Guid catalogId, CancellationToken ct)
        {
            var list = await _injectService.GetByCatalogAsync(catalogId, ct);
            return Ok(list);
        }

        /// <summary>
        /// Gets a specific Inject by id
        /// </summary>
        /// <remarks>
        /// Returns the Inject with the id specified
        /// <para />
        /// Accessible to a User that is a member of a Team within the specified Inject
        /// </remarks>
        /// <param name="id">The id of the Inject</param>
        /// <param name="ct"></param>
        /// <returns></returns>
        [HttpGet("injects/{id}")]
        [ProducesResponseType(typeof(ViewModels.Injectm), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "getInject")]
        public async Task<IActionResult> Get(Guid id, CancellationToken ct)
        {
            var inject = await _injectService.GetAsync(id, ct);

            if (inject == null)
                throw new EntityNotFoundException<ViewModels.Injectm>();

            return Ok(inject);
        }

        /// <summary>
        /// Creates a new Inject
        /// </summary>
        /// <remarks>
        /// Creates a new Inject with the attributes specified
        /// <para />
        /// Accessible only to a SuperUser or an Administrator
        /// </remarks>
        /// <param name="catalogId"> The ID of the catalog where the inject will be added
        /// <param name="inject">The data to create the Inject with</param>
        /// <param name="ct"></param>
        [HttpPost("catalog/{catalogId}/injects")]
        [ProducesResponseType(typeof(ViewModels.Injectm), (int)HttpStatusCode.Created)]
        [SwaggerOperation(OperationId = "createInject")]
        public async Task<IActionResult> Create([FromRoute] Guid catalogId, [FromBody] ViewModels.Injectm inject, CancellationToken ct)
        {
            var list = await _injectService.CreateAsync(catalogId, inject, ct);
            return Ok(list);
        }

        /// <summary>
        /// Updates an Inject
        /// </summary>
        /// <remarks>
        /// Updates an Inject with the attributes specified
        /// <para />
        /// Accessible only to a SuperUser or a User on an Admin Team within the specified Inject
        /// </remarks>
        /// <param name="id">The Id of the Inject to update</param>
        /// <param name="inject">The updated Inject values</param>
        /// <param name="ct"></param>
        [HttpPut("injects/{id}")]
        [ProducesResponseType(typeof(ViewModels.Injectm), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "updateInject")]
        public async Task<IActionResult> Update([FromRoute] Guid id, [FromBody] ViewModels.Injectm inject, CancellationToken ct)
        {
            inject.ModifiedBy = User.GetId();
            var list = await _injectService.UpdateAsync(id, inject, ct);
            return Ok(list);
        }

        /// <summary>
        /// Deletes an Inject
        /// </summary>
        /// <remarks>
        /// Deletes an Inject with the specified id
        /// <para />
        /// Accessible only to a SuperUser or a User on an Admin Team within the specified Inject
        /// </remarks>
        /// <param name="id">The id of the Inject to delete</param>
        /// <param name="ct"></param>
        [HttpDelete("injects/{id}")]
        [ProducesResponseType((int)HttpStatusCode.NoContent)]
        [SwaggerOperation(OperationId = "deleteInject")]
        public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
        {
            var returnVal = await _injectService.DeleteAsync(id, ct);
            return Ok(returnVal);
        }

    }
}
