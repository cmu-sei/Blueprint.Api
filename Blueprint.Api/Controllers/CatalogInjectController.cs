// Copyright 2024 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Blueprint.Api.Infrastructure.Exceptions;
using Blueprint.Api.Services;
using Blueprint.Api.ViewModels;
using Swashbuckle.AspNetCore.Annotations;

namespace Blueprint.Api.Controllers
{
    public class CatalogInjectController : BaseController
    {
        private readonly ICatalogInjectService _catalogInjectService;
        private readonly IAuthorizationService _authorizationService;

        public CatalogInjectController(ICatalogInjectService catalogInjectService, IAuthorizationService authorizationService)
        {
            _catalogInjectService = catalogInjectService;
            _authorizationService = authorizationService;
        }

        /// <summary>
        /// Gets a specific CatalogInject by id
        /// </summary>
        /// <remarks>
        /// Returns the CatalogInject with the id specified
        /// <para />
        /// Only accessible to a SuperCatalog
        /// </remarks>
        /// <param name="id">The id of the CatalogInject</param>
        /// <param name="ct"></param>
        /// <returns></returns>
        [HttpGet("cataloginjects/{id}")]
        [ProducesResponseType(typeof(CatalogInject), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "getCatalogInject")]
        public async Task<IActionResult> Get(Guid id, CancellationToken ct)
        {
            var inject = await _catalogInjectService.GetAsync(id, ct);

            if (inject == null)
                throw new EntityNotFoundException<CatalogInject>();

            return Ok(inject);
        }

        /// <summary>
        /// Creates a new CatalogInject
        /// </summary>
        /// <remarks>
        /// Creates a new CatalogInject with the attributes specified
        /// <para />
        /// Accessible only to a SuperCatalog
        /// </remarks>
        /// <param name="inject">The data to create the CatalogInject with</param>
        /// <param name="ct"></param>
        [HttpPost("cataloginjects")]
        [ProducesResponseType(typeof(CatalogInject), (int)HttpStatusCode.Created)]
        [SwaggerOperation(OperationId = "createCatalogInject")]
        public async Task<IActionResult> Create([FromBody] CatalogInject inject, CancellationToken ct)
        {
            var createdCatalogInject = await _catalogInjectService.CreateAsync(inject, ct);
            return CreatedAtAction(nameof(this.Get), new { id = createdCatalogInject.Id }, createdCatalogInject);
        }

        /// <summary>
        /// Deletes a CatalogInject
        /// </summary>
        /// <remarks>
        /// Deletes a CatalogInject with the specified id
        /// <para />
        /// Accessible only to a SuperCatalog
        /// </remarks>
        /// <param name="id">The id of the CatalogInject to delete</param>
        /// <param name="ct"></param>
        [HttpDelete("cataloginjects/{id}")]
        [ProducesResponseType((int)HttpStatusCode.NoContent)]
        [SwaggerOperation(OperationId = "deleteCatalogInject")]
        public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
        {
            await _catalogInjectService.DeleteAsync(id, ct);
            return NoContent();
        }

        /// <summary>
        /// Deletes a CatalogInject by catalog ID and inject ID
        /// </summary>
        /// <remarks>
        /// Deletes a CatalogInject with the specified catalog ID and inject ID
        /// <para />
        /// Accessible only to a SuperCatalog
        /// </remarks>
        /// <param name="catalogId">ID of a catalog.</param>
        /// <param name="injectId">ID of a inject.</param>
        /// <param name="ct"></param>
        [HttpDelete("catalogs/{catalogId}/injects/{injectId}")]
        [ProducesResponseType((int)HttpStatusCode.NoContent)]
        [SwaggerOperation(OperationId = "deleteCatalogInjectByIds")]
        public async Task<IActionResult> Delete(Guid catalogId, Guid injectId, CancellationToken ct)
        {
            await _catalogInjectService.DeleteByIdsAsync(catalogId, injectId, ct);
            return NoContent();
        }

    }
}
