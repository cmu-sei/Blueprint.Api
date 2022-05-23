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
using Blueprint.Api.Infrastructure.Extensions;
using Blueprint.Api.Infrastructure.Exceptions;
using Blueprint.Api.Infrastructure.QueryParameters;
using Blueprint.Api.Services;
using Blueprint.Api.ViewModels;
using Swashbuckle.AspNetCore.Annotations;

namespace Blueprint.Api.Controllers
{
    public class MselController : BaseController
    {
        private readonly IMselService _mselService;
        private readonly IAuthorizationService _authorizationService;

        public MselController(IMselService mselService, IAuthorizationService authorizationService)
        {
            _mselService = mselService;
            _authorizationService = authorizationService;
        }

        /// <summary>
        /// Gets Msels
        /// </summary>
        /// <remarks>
        /// Returns a list of Msels.
        /// </remarks>
        /// <param name="queryParameters">Result filtering criteria</param>
        /// <param name="ct"></param>
        /// <returns></returns>
        [HttpGet("msels")]
        [ProducesResponseType(typeof(IEnumerable<Msel>), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "getMsels")]
        public async Task<IActionResult> Get([FromQuery] MselGet queryParameters, CancellationToken ct)
        {
            var list = await _mselService.GetAsync(queryParameters, ct);
            return Ok(list);
        }

        /// <summary>
        /// Gets Msels for the current user
        /// </summary>
        /// <remarks>
        /// Returns a list of the current user's active Msels.
        /// </remarks>
        /// <param name="ct"></param>
        /// <returns></returns>
        [HttpGet("msels/mine")]
        [ProducesResponseType(typeof(IEnumerable<Msel>), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "getMyMsels")]
        public async Task<IActionResult> GetMine(CancellationToken ct)
        {
            var list = await _mselService.GetMineAsync(ct);
            return Ok(list);
        }

        /// <summary>
        /// Gets a specific Msel by id
        /// </summary>
        /// <remarks>
        /// Returns the Msel with the id specified
        /// </remarks>
        /// <param name="id">The id of the Msel</param>
        /// <param name="ct"></param>
        /// <returns></returns>
        [HttpGet("msels/{id}")]
        [ProducesResponseType(typeof(Msel), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "getMsel")]
        public async Task<IActionResult> Get(Guid id, CancellationToken ct)
        {
            var msel = await _mselService.GetAsync(id, ct);
            return Ok(msel);
        }

        /// <summary>
        /// Gets specific Msel data by id
        /// </summary>
        /// <remarks>
        /// Returns a DataTable for the Msel with the id specified
        /// </remarks>
        /// <param name="id">The id of the Msel</param>
        /// <param name="ct"></param>
        /// <returns></returns>
        [HttpGet("msels/{id}/data")]
        [ProducesResponseType(typeof(DataTable), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "getMselData")]
        public async Task<IActionResult> GetData(Guid id, CancellationToken ct)
        {
            var mselData = await _mselService.GetDataTableAsync(id, ct);
            return Ok(mselData);
        }

        /// <summary>
        /// Creates a new Msel
        /// </summary>
        /// <remarks>
        /// Creates a new Msel with the attributes specified
        /// <para />
        /// Accessible only to a ContentDeveloper or an Administrator
        /// </remarks>
        /// <param name="msel">The data used to create the Msel</param>
        /// <param name="ct"></param>
        [HttpPost("msels")]
        [ProducesResponseType(typeof(Msel), (int)HttpStatusCode.Created)]
        [SwaggerOperation(OperationId = "createMsel")]
        public async Task<IActionResult> Create([FromBody] Msel msel, CancellationToken ct)
        {
            msel.CreatedBy = User.GetId();
            var createdMsel = await _mselService.CreateAsync(msel, ct);
            return CreatedAtAction(nameof(this.Get), new { id = createdMsel.Id }, createdMsel);
        }

        /// <summary>
        /// Updates a Msel
        /// </summary>
        /// <remarks>
        /// Updates a Msel with the attributes specified.
        /// The ID from the route MUST MATCH the ID contained in the msel parameter
        /// <para />
        /// Accessible only to a ContentDeveloper or an Administrator
        /// </remarks>
        /// <param name="id">The Id of the Msel to update</param>
        /// <param name="msel">The updated Msel values</param>
        /// <param name="ct"></param>
        [HttpPut("msels/{id}")]
        [ProducesResponseType(typeof(Msel), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "updateMsel")]
        public async Task<IActionResult> Update([FromRoute] Guid id, [FromBody] Msel msel, CancellationToken ct)
        {
            msel.ModifiedBy = User.GetId();
            var updatedMsel = await _mselService.UpdateAsync(id, msel, ct);
            return Ok(updatedMsel);
        }

        /// <summary>
        /// Deletes a Msel
        /// </summary>
        /// <remarks>
        /// Deletes a Msel with the specified id
        /// <para />
        /// Accessible only to a ContentDeveloper or an Administrator
        /// </remarks>
        /// <param name="id">The id of the Msel to delete</param>
        /// <param name="ct"></param>
        [HttpDelete("msels/{id}")]
        [ProducesResponseType((int)HttpStatusCode.NoContent)]
        [SwaggerOperation(OperationId = "deleteMsel")]
        public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
        {
            await _mselService.DeleteAsync(id, ct);
            return NoContent();
        }

        /// <summary> Upload file(s) </summary>
        /// <remarks> File objects will be returned in the same order as their respective files within the form. </remarks>
        /// <param name="form"> The files to upload and their settings </param>
        /// <param name="ct"></param>
        [HttpPost("msels/xlsx")]
        [ProducesResponseType((int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "uploadXlsxFiles")]
        public async Task<IActionResult> UploadAsync([FromForm] FileForm form, CancellationToken ct)
        {
            var result = await _mselService.UploadAsync(form, ct);
            return Ok(result);
        }

        /// <summary> Replace a msel by id with data in xlsx file </summary>
        /// <param name="form"> The file to upload</param>
        /// <param name="id"> The id of the msel </param>
        /// <param name="ct"></param>
        [HttpPut("msels/{id}/xlsx")]
        [ProducesResponseType(typeof(Guid), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "replaceWithXlsxFile")]
        public async Task<IActionResult> ReplaceAsync([FromForm] FileForm form, Guid id, CancellationToken ct)
        {
            var result = await _mselService.ReplaceAsync(form, id, ct);
            return Ok(result);
        }

        /// <summary> Download a msel by id as xlsx file </summary>
        /// <param name="id"> The id of the msel </param>
        /// <param name="ct"></param>
        [HttpGet("msels/{id}/xlsx")]
        [ProducesResponseType(typeof(FileResult), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "download")]
        public async Task<IActionResult> DownloadAsync(Guid id, CancellationToken ct)
        {
            (var stream, var fileName) = await _mselService.DownloadAsync(id, ct);

            // If this is wrapped in an Ok, it throws an exception
            return File(stream, "application/octet-stream", fileName);
        }

    }
}

