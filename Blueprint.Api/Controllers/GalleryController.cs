// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Blueprint.Api.Services;
using GAC = Gallery.Api.Client;
using Swashbuckle.AspNetCore.Annotations;

namespace Blueprint.Api.Controllers
{
    public class GalleryController : BaseController
    {
        private readonly IGalleryService _galleryService;
        private readonly IAuthorizationService _authorizationService;

        public GalleryController(IGalleryService galleryService, IAuthorizationService authorizationService)
        {
            _galleryService = galleryService;
            _authorizationService = authorizationService;
        }

        /// <summary>
        /// Gets the user'scollections
        /// </summary>
        /// <remarks>
        /// Returns the collections.
        /// </remarks>
        /// <param name="ct"></param>
        /// <returns></returns>
        [HttpGet("collections")]
        [ProducesResponseType(typeof(IEnumerable<GAC.Collection>), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "getCollections")]
        public async Task<IActionResult> GetCollections(CancellationToken ct)
        {
            var collections = await _galleryService.GetCollectionsAsync(ct);
            return Ok(collections);
        }

        /// <summary>
        /// Gets the exhibits for a collection
        /// </summary>
        /// <remarks>
        /// Returns the exhibits.
        /// </remarks>
        /// <param name="id"></param>
        /// <param name="ct"></param>
        /// <returns></returns>
        [HttpGet("collection/{id}/exhibits")]
        [ProducesResponseType(typeof(IEnumerable<GAC.Exhibit>), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "getExhibits")]
        public async Task<IActionResult> GetExhibits(Guid id, CancellationToken ct)
        {
            var collections = await _galleryService.GetExhibitsAsync(id, ct);
            return Ok(collections);
        }

    }

}

