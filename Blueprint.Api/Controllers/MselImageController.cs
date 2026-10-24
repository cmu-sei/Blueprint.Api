// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Blueprint.Api.Data.Enumerations;
using Blueprint.Api.Infrastructure.Authorization;
using Blueprint.Api.Services;
using Blueprint.Api.ViewModels;
using Swashbuckle.AspNetCore.Annotations;

namespace Blueprint.Api.Controllers
{
    public class MselImageController : BaseController
    {
        private readonly IMselImageService _mselImageService;
        private readonly IBlueprintAuthorizationService _authorizationService;

        public MselImageController(IMselImageService mselImageService, IBlueprintAuthorizationService authorizationService)
        {
            _mselImageService = mselImageService;
            _authorizationService = authorizationService;
        }

        /// <summary>
        /// Gets the image of a Msel
        /// </summary>
        /// <remarks>
        /// Returns the image shown on the Msel's card. A Msel without one answers 404.
        /// </remarks>
        /// <param name="id">The id of the Msel</param>
        /// <param name="ct"></param>
        [HttpGet("msels/{id}/image")]
        [ProducesResponseType(typeof(FileResult), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "getMselImage")]
        public async Task<IActionResult> Get(Guid id, CancellationToken ct)
        {
            var hasSystemPermission = await _authorizationService.AuthorizeAsync([SystemPermission.ViewMsels], ct);
            var image = await _mselImageService.GetAsync(id, hasSystemPermission, ct);
            Response.Headers.CacheControl = "private, max-age=3600";
            return File(image.Data, image.ContentType);
        }

        /// <summary>
        /// Sets the image of a Msel
        /// </summary>
        /// <remarks>
        /// Replaces the Msel's image with a PNG, JPEG, GIF or WebP file of 2 MB or less, and returns the updated Msel.
        /// </remarks>
        /// <param name="id">The id of the Msel</param>
        /// <param name="form">The image to upload</param>
        /// <param name="ct"></param>
        [HttpPut("msels/{id}/image")]
        [ProducesResponseType(typeof(Msel), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "setMselImage")]
        [RequestSizeLimit(MselImageService.MaxImageBytes + 64 * 1024)]
        public async Task<IActionResult> Set(Guid id, [FromForm] FileForm form, CancellationToken ct)
        {
            var hasSystemPermission = await _authorizationService.AuthorizeAsync([SystemPermission.EditMsels], ct);
            var msel = await _mselImageService.SetAsync(id, form.ToUpload, hasSystemPermission, ct);
            return Ok(msel);
        }

        /// <summary>
        /// Removes the image of a Msel
        /// </summary>
        /// <remarks>
        /// Removes the Msel's image, so its card falls back to the generated pattern, and returns the updated Msel.
        /// </remarks>
        /// <param name="id">The id of the Msel</param>
        /// <param name="ct"></param>
        [HttpDelete("msels/{id}/image")]
        [ProducesResponseType(typeof(Msel), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "deleteMselImage")]
        public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
        {
            var hasSystemPermission = await _authorizationService.AuthorizeAsync([SystemPermission.EditMsels], ct);
            var msel = await _mselImageService.DeleteAsync(id, hasSystemPermission, ct);
            return Ok(msel);
        }
    }
}
