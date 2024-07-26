// Copyright 2024 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using System;
using System.Collections.Generic;
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
        /// Gets all CatalogInjects for a Catalog
        /// </summary>
        /// <remarks>
        /// Returns a list of all of the CatalogInjects for the catalog.
        /// </remarks>
        /// <param name="catalogId">The id of the Catalog</param>
        /// <param name="ct"></param>
        /// <returns></returns>
        [HttpGet("catalogs/{catalogId}/cataloginjects")]
        [ProducesResponseType(typeof(IEnumerable<CatalogInject>), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "getCatalogInjects")]
        public async Task<IActionResult> GetByCatalog(Guid catalogId, CancellationToken ct)
        {
            var list = await _catalogInjectService.GetByCatalogAsync(catalogId, ct);
            return Ok(list);
        }

        /// <summary>
        /// Gets a specific CatalogInject by id
        /// </summary>
        /// <remarks>
        /// Returns the CatalogInject with the id specified
        /// <para />
        /// Only accessible to a SuperUser
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
        /// Accessible only to a SuperUser
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
        /// Creates multiple CatalogInjects
        /// </summary>
        /// <remarks>
        /// Creates multiple CatalogInjects with the attributes specified
        /// <para />
        /// Accessible only to a SuperUser
        /// </remarks>
        /// <param name="catalogInjects">The data to create the CatalogInjects with</param>
        /// <param name="ct"></param>
        [HttpPost("cataloginjects/multiple")]
        [ProducesResponseType(typeof(IEnumerable<CatalogInject>), (int)HttpStatusCode.Created)]
        [SwaggerOperation(OperationId = "createMultipleCatalogInjects")]
        public async Task<IActionResult> CreateMultiple([FromBody] List<CatalogInject> catalogInjects, CancellationToken ct)
        {
            var createdCatalogInjects = await _catalogInjectService.CreateMultipleAsync(catalogInjects, ct);
            return Ok(createdCatalogInjects);
        }

        /// <summary>
        /// Deletes a CatalogInject
        /// </summary>
        /// <remarks>
        /// Deletes a CatalogInject with the specified id
        /// <para />
        /// Accessible only to a SuperUser
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
        /// Accessible only to a SuperUser
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
