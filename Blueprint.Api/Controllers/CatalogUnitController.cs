// Copyright 2024 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

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
    public class CatalogUnitController : BaseController
    {
        private readonly ICatalogUnitService _catalogUnitService;
        private readonly IAuthorizationService _authorizationService;

        public CatalogUnitController(ICatalogUnitService catalogUnitService, IAuthorizationService authorizationService)
        {
            _catalogUnitService = catalogUnitService;
            _authorizationService = authorizationService;
        }

        /// <summary>
        /// Gets all CatalogUnits for a Catalog
        /// </summary>
        /// <remarks>
        /// Returns a list of all of the CatalogUnits for the catalog.
        /// </remarks>
        /// <param name="catalogId">The id of the Catalog</param>
        /// <param name="ct"></param>
        /// <returns></returns>
        [HttpGet("catalogs/{catalogId}/catalogunits")]
        [ProducesResponseType(typeof(IEnumerable<CatalogUnit>), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "getCatalogUnits")]
        public async Task<IActionResult> GetByCatalog(Guid catalogId, CancellationToken ct)
        {
            var list = await _catalogUnitService.GetByCatalogAsync(catalogId, ct);
            return Ok(list);
        }

        /// <summary>
        /// Gets a specific CatalogUnit by id
        /// </summary>
        /// <remarks>
        /// Returns the CatalogUnit with the id specified
        /// <para />
        /// </remarks>
        /// <param name="id">The id of the CatalogUnit</param>
        /// <param name="ct"></param>
        /// <returns></returns>
        [HttpGet("catalogunits/{id}")]
        [ProducesResponseType(typeof(CatalogUnit), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "getCatalogUnit")]
        public async Task<IActionResult> Get(Guid id, CancellationToken ct)
        {
            var unit = await _catalogUnitService.GetAsync(id, ct);

            if (unit == null)
                throw new EntityNotFoundException<CatalogUnit>();

            return Ok(unit);
        }

        /// <summary>
        /// Creates a new CatalogUnit
        /// </summary>
        /// <remarks>
        /// Creates a new CatalogUnit with the attributes specified
        /// <para />
        /// </remarks>
        /// <param name="catalogUnit">The data to create the CatalogUnit with</param>
        /// <param name="ct"></param>
        [HttpPost("catalogunits")]
        [ProducesResponseType(typeof(CatalogUnit), (int)HttpStatusCode.Created)]
        [SwaggerOperation(OperationId = "createCatalogUnit")]
        public async Task<IActionResult> Create([FromBody] CatalogUnit catalogUnit, CancellationToken ct)
        {
            var createdCatalogUnit = await _catalogUnitService.CreateAsync(catalogUnit, ct);
            return CreatedAtAction(nameof(this.Get), new { id = createdCatalogUnit.Id }, createdCatalogUnit);
        }

        /// <summary>
        /// Updates a CatalogUnit
        /// </summary>
        /// <remarks>
        /// Updates a CatalogUnit with the attributes specified
        /// <para />
        /// </remarks>
        /// <param name="id">The Id of the CatalogUnit to update</param>
        /// <param name="catalogUnit">The updated CatalogUnit values</param>
        /// <param name="ct"></param>
        [HttpPut("catalogunits/{id}")]
        [ProducesResponseType(typeof(Unit), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "updateCatalogUnit")]
        public async Task<IActionResult> Update([FromRoute] Guid id, [FromBody] CatalogUnit catalogUnit, CancellationToken ct)
        {
            var updatedUnit = await _catalogUnitService.UpdateAsync(id, catalogUnit, ct);
            return Ok(updatedUnit);
        }

        /// <summary>
        /// Deletes a CatalogUnit
        /// </summary>
        /// <remarks>
        /// Deletes a CatalogUnit with the specified id
        /// <para />
        /// </remarks>
        /// <param name="id">The id of the CatalogUnit to delete</param>
        /// <param name="ct"></param>
        [HttpDelete("catalogunits/{id}")]
        [ProducesResponseType((int)HttpStatusCode.NoContent)]
        [SwaggerOperation(OperationId = "deleteCatalogUnit")]
        public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
        {
            await _catalogUnitService.DeleteAsync(id, ct);
            return NoContent();
        }

        /// <summary>
        /// Deletes a CatalogUnit by catalog ID and unit ID
        /// </summary>
        /// <remarks>
        /// Deletes a CatalogUnit with the specified catalog ID and unit ID
        /// <para />
        /// </remarks>
        /// <param name="catalogId">ID of a catalog.</param>
        /// <param name="unitId">ID of a unit.</param>
        /// <param name="ct"></param>
        [HttpDelete("catalogs/{catalogId}/units/{unitId}")]
        [ProducesResponseType((int)HttpStatusCode.NoContent)]
        [SwaggerOperation(OperationId = "deleteCatalogUnitByIds")]
        public async Task<IActionResult> Delete(Guid catalogId, Guid unitId, CancellationToken ct)
        {
            await _catalogUnitService.DeleteByIdsAsync(catalogId, unitId, ct);
            return NoContent();
        }

    }
}
