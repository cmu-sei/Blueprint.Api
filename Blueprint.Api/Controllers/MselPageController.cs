// Copyright 2023 Carnegie Mellon University. All Rights Reserved.
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
    public class MselPageController : BaseController
    {
        private readonly IMselPageService _mselPageService;
        private readonly IAuthorizationService _authorizationService;

        public MselPageController(IMselPageService mselPageService, IAuthorizationService authorizationService)
        {
            _mselPageService = mselPageService;
            _authorizationService = authorizationService;
        }

        /// <summary>
        /// Gets all MselPages for a MSEL
        /// </summary>
        /// <remarks>
        /// Returns a list of all of the MselPages for the msel.
        /// </remarks>
        /// <param name="mselId">The id of the Msel</param>
        /// <param name="ct"></param>
        /// <returns></returns>
        [HttpGet("msels/{mselId}/mselpages")]
        [ProducesResponseType(typeof(IEnumerable<MselPage>), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "getMselPages")]
        public async Task<IActionResult> GetByMsel(Guid mselId, CancellationToken ct)
        {
            var list = await _mselPageService.GetByMselAsync(mselId, ct);
            return Ok(list);
        }

        /// <summary>
        /// Gets a specific MselPage by id
        /// </summary>
        /// <remarks>
        /// Returns the MselPage with the id specified
        /// <para />
        /// </remarks>
        /// <param name="id">The id of the MselPage</param>
        /// <param name="ct"></param>
        /// <returns></returns>
        [HttpGet("mselpages/{id}")]
        [ProducesResponseType(typeof(MselPage), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "getMselPage")]
        public async Task<IActionResult> Get(Guid id, CancellationToken ct)
        {
            var page = await _mselPageService.GetAsync(id, ct);

            if (page == null)
                throw new EntityNotFoundException<MselPage>();

            return Ok(page);
        }

        /// <summary>
        /// Creates a new MselPage
        /// </summary>
        /// <remarks>
        /// Creates a new MselPage with the attributes specified
        /// <para />
        /// </remarks>
        /// <param name="mselPage">The data to create the MselPage with</param>
        /// <param name="ct"></param>
        [HttpPost("mselpages")]
        [ProducesResponseType(typeof(MselPage), (int)HttpStatusCode.Created)]
        [SwaggerOperation(OperationId = "createMselPage")]
        public async Task<IActionResult> Create([FromBody] MselPage mselPage, CancellationToken ct)
        {
            var createdMselPage = await _mselPageService.CreateAsync(mselPage, ct);
            return CreatedAtAction(nameof(this.Get), new { id = createdMselPage.Id }, createdMselPage);
        }

        /// <summary>
        /// Updates a MselPage
        /// </summary>
        /// <remarks>
        /// Updates a MselPage with the attributes specified
        /// <para />
        /// </remarks>
        /// <param name="id">The Id of the MselPage to update</param>
        /// <param name="mselPage">The updated MselPage values</param>
        /// <param name="ct"></param>
        [HttpPut("mselpages/{id}")]
        [ProducesResponseType(typeof(MselPage), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "updateMselPage")]
        public async Task<IActionResult> Update([FromRoute] Guid id, [FromBody] MselPage mselPage, CancellationToken ct)
        {
            var updatedPage = await _mselPageService.UpdateAsync(id, mselPage, ct);
            return Ok(updatedPage);
        }

        /// <summary>
        /// Deletes a MselPage
        /// </summary>
        /// <remarks>
        /// Deletes a MselPage with the specified id
        /// <para />
        /// </remarks>
        /// <param name="id">The id of the MselPage to delete</param>
        /// <param name="ct"></param>
        [HttpDelete("mselpages/{id}")]
        [ProducesResponseType((int)HttpStatusCode.NoContent)]
        [SwaggerOperation(OperationId = "deleteMselPage")]
        public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
        {
            await _mselPageService.DeleteAsync(id, ct);
            return NoContent();
        }

    }
}

