// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using System;
using System.Collections.Generic;
using System.Data;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Blueprint.Api.Data.Enumerations;
using Blueprint.Api.Infrastructure.Extensions;
using Blueprint.Api.Infrastructure.QueryParameters;
using Blueprint.Api.Services;
using Blueprint.Api.ViewModels;
using Swashbuckle.AspNetCore.Annotations;

namespace Blueprint.Api.Controllers
{
    public class CatalogController : BaseController
    {
        private readonly ICatalogService _catalogService;
        private readonly IAuthorizationService _authorizationService;

        public CatalogController(
            ICatalogService catalogService,
            ICiteService citeService,
            IPlayerService playerService,
            IAuthorizationService authorizationService)
        {
            _catalogService = catalogService;
            _authorizationService = authorizationService;
        }

        /// <summary>
        /// Gets Catalogs
        /// </summary>
        /// <remarks>
        /// Returns a list of Catalogs.
        /// </remarks>
        /// <param name="ct"></param>
        /// <returns></returns>
        [HttpGet("catalogs")]
        [ProducesResponseType(typeof(IEnumerable<Catalog>), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "getCatalogs")]
        public async Task<IActionResult> Get(CancellationToken ct)
        {
            var list = await _catalogService.GetAsync(ct);
            return Ok(list);
        }

        /// <summary>
        /// Gets Catalogs for the current user
        /// </summary>
        /// <remarks>
        /// Returns a list of the current user's active Catalogs.
        /// </remarks>
        /// <param name="ct"></param>
        /// <returns></returns>
        [HttpGet("my-catalogs")]
        [ProducesResponseType(typeof(IEnumerable<Catalog>), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "getMyCatalogs")]
        public async Task<IActionResult> GetMine(CancellationToken ct)
        {
            var list = await _catalogService.GetMineAsync(ct);
            return Ok(list);
        }

        /// <summary>
        /// Gets Catalogs for requested user
        /// </summary>
        /// <remarks>
        /// Returns a list of the requested user's active Catalogs.
        /// </remarks>
        /// <param name="userId"></param>
        /// <param name="ct"></param>
        /// <returns></returns>
        [HttpGet("users/{userId}/catalogs")]
        [ProducesResponseType(typeof(IEnumerable<Catalog>), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "getUserCatalogs")]
        public async Task<IActionResult> GetUserCatalogs(Guid userId, CancellationToken ct)
        {
            var list = await _catalogService.GetUserCatalogsAsync(userId, ct);
            return Ok(list);
        }

        /// <summary>
        /// Gets a specific Catalog by id
        /// </summary>
        /// <remarks>
        /// Returns the Catalog with the id specified
        /// </remarks>
        /// <param name="id">The id of the Catalog</param>
        /// <param name="ct"></param>
        /// <returns></returns>
        [HttpGet("catalogs/{id}")]
        [ProducesResponseType(typeof(Catalog), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "getCatalog")]
        public async Task<IActionResult> Get(Guid id, CancellationToken ct)
        {
            var catalog = await _catalogService.GetAsync(id, ct);
            return Ok(catalog);
        }

        /// <summary>
        /// Creates a new Catalog
        /// </summary>
        /// <remarks>
        /// Creates a new Catalog with the attributes specified
        /// <para />
        /// Accessible only to a ContentDeveloper or an Administrator
        /// </remarks>
        /// <param name="catalog">The data used to create the Catalog</param>
        /// <param name="ct"></param>
        [HttpPost("catalogs")]
        [ProducesResponseType(typeof(Catalog), (int)HttpStatusCode.Created)]
        [SwaggerOperation(OperationId = "createCatalog")]
        public async Task<IActionResult> Create([FromBody] Catalog catalog, CancellationToken ct)
        {
            catalog.CreatedBy = User.GetId();
            var createdCatalog = await _catalogService.CreateAsync(catalog, ct);
            return CreatedAtAction(nameof(this.Get), new { id = createdCatalog.Id }, createdCatalog);
        }

        /// <summary>
        /// Creates a new Catalog by copying an existing Catalog
        /// </summary>
        /// <remarks>
        /// Creates a new Catalog from the specified existing Catalog
        /// <para />
        /// Accessible only to a ContentDeveloper or an Administrator
        /// </remarks>
        /// <param name="id">The ID of the Catalog to be copied</param>
        /// <param name="ct"></param>
        [HttpPost("catalogs/{id}/copy")]
        [ProducesResponseType(typeof(Catalog), (int)HttpStatusCode.Created)]
        [SwaggerOperation(OperationId = "copyCatalog")]
        public async Task<IActionResult> Copy(Guid id, CancellationToken ct)
        {
            var createdCatalog = await _catalogService.CopyAsync(id, ct);
            return CreatedAtAction(nameof(this.Get), new { id = createdCatalog.Id }, createdCatalog);
        }

        /// <summary>
        /// Updates a Catalog
        /// </summary>
        /// <remarks>
        /// Updates a Catalog with the attributes specified.
        /// The ID from the route MUST MATCH the ID contained in the catalog parameter
        /// <para />
        /// Accessible only to a ContentDeveloper or an Administrator
        /// </remarks>
        /// <param name="id">The Id of the Catalog to update</param>
        /// <param name="catalog">The updated Catalog values</param>
        /// <param name="ct"></param>
        [HttpPut("catalogs/{id}")]
        [ProducesResponseType(typeof(Catalog), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "updateCatalog")]
        public async Task<IActionResult> Update([FromRoute] Guid id, [FromBody] Catalog catalog, CancellationToken ct)
        {
            catalog.ModifiedBy = User.GetId();
            var updatedCatalog = await _catalogService.UpdateAsync(id, catalog, ct);
            return Ok(updatedCatalog);
        }

        /// <summary>
        /// Deletes a Catalog
        /// </summary>
        /// <remarks>
        /// Deletes a Catalog with the specified id
        /// <para />
        /// Accessible only to a ContentDeveloper or an Administrator
        /// </remarks>
        /// <param name="id">The id of the Catalog to delete</param>
        /// <param name="ct"></param>
        [HttpDelete("catalogs/{id}")]
        [ProducesResponseType((int)HttpStatusCode.NoContent)]
        [SwaggerOperation(OperationId = "deleteCatalog")]
        public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
        {
            await _catalogService.DeleteAsync(id, ct);
            return NoContent();
        }

        /// <summary> Upload a json Catalog file </summary>
        /// <param name="form"> The files to upload and their settings </param>
        /// <param name="ct"></param>
        [HttpPost("catalogs/json")]
        [ProducesResponseType(typeof(Catalog), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "uploadJsonFiles")]
        public async Task<IActionResult> UploadJsonAsync([FromForm] FileForm form, CancellationToken ct)
        {
            var result = await _catalogService.UploadJsonAsync(form, ct);
            return Ok(result);
        }

        /// <summary> Download a catalog by id as json file </summary>
        /// <param name="id"> The id of the catalog </param>
        /// <param name="ct"></param>
        [HttpGet("catalogs/{id}/json")]
        [ProducesResponseType(typeof(FileResult), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "downloadJson")]
        public async Task<IActionResult> DownloadJsonAsync(Guid id, CancellationToken ct)
        {
            (var stream, var fileName) = await _catalogService.DownloadJsonAsync(id, ct);

            // If this is wrapped in an Ok, it throws an exception
            return File(stream, "application/octet-stream", fileName);
        }

    }
}
