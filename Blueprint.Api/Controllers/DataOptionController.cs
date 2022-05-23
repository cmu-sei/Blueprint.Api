// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
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
using Blueprint.Api.Infrastructure.QueryParameters;
using Blueprint.Api.Services;
using Blueprint.Api.ViewModels;
using Swashbuckle.AspNetCore.Annotations;

namespace Blueprint.Api.Controllers
{
    public class DataOptionController : BaseController
    {
        private readonly IDataOptionService _dataOptionService;
        private readonly IAuthorizationService _authorizationService;

        public DataOptionController(IDataOptionService dataOptionService, IAuthorizationService authorizationService)
        {
            _dataOptionService = dataOptionService;
            _authorizationService = authorizationService;
        }

        /// <summary>
        /// Gets DataOptions by msel
        /// </summary>
        /// <remarks>
        /// Returns a list of DataOptions for the msel.
        /// </remarks>
        /// <param name="dataFieldId"></param>
        /// <param name="ct"></param>
        /// <returns></returns>
        [HttpGet("msels/{dataFieldId}/dataOptions")]
        [ProducesResponseType(typeof(IEnumerable<DataOption>), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "getByDataField")]
        public async Task<IActionResult> GetByDataField(Guid dataFieldId, CancellationToken ct)
        {
            var list = await _dataOptionService.GetByDataFieldAsync(dataFieldId, ct);
            return Ok(list);
        }

        /// <summary>
        /// Gets a specific DataOption by id
        /// </summary>
        /// <remarks>
        /// Returns the DataOption with the id specified
        /// </remarks>
        /// <param name="id">The id of the DataOption</param>
        /// <param name="ct"></param>
        /// <returns></returns>
        [HttpGet("dataOptions/{id}")]
        [ProducesResponseType(typeof(DataOption), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "getDataOption")]
        public async Task<IActionResult> Get(Guid id, CancellationToken ct)
        {
            var dataOption = await _dataOptionService.GetAsync(id, ct);

            if (dataOption == null)
                throw new EntityNotFoundException<DataOption>();

            return Ok(dataOption);
        }

        /// <summary>
        /// Creates a new DataOption
        /// </summary>
        /// <remarks>
        /// Creates a new DataOption with the attributes specified
        /// <para />
        /// Accessible only to a ContentDeveloper or an Administrator
        /// </remarks>
        /// <param name="dataOption">The data used to create the DataOption</param>
        /// <param name="ct"></param>
        [HttpPost("dataOptions")]
        [ProducesResponseType(typeof(DataOption), (int)HttpStatusCode.Created)]
        [SwaggerOperation(OperationId = "createDataOption")]
        public async Task<IActionResult> Create([FromBody] DataOption dataOption, CancellationToken ct)
        {
            dataOption.CreatedBy = User.GetId();
            var createdDataOption = await _dataOptionService.CreateAsync(dataOption, ct);
            return CreatedAtAction(nameof(this.Get), new { id = createdDataOption.Id }, createdDataOption);
        }

        /// <summary>
        /// Updates a  DataOption
        /// </summary>
        /// <remarks>
        /// Updates a DataOption with the attributes specified.
        /// The ID from the route MUST MATCH the ID contained in the dataOption parameter
        /// <para />
        /// Accessible only to a ContentDeveloper or an Administrator
        /// </remarks>
        /// <param name="id">The Id of the DataOption to update</param>
        /// <param name="dataOption">The updated DataOption values</param>
        /// <param name="ct"></param>
        [HttpPut("dataOptions/{id}")]
        [ProducesResponseType(typeof(DataOption), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "updateDataOption")]
        public async Task<IActionResult> Update([FromRoute] Guid id, [FromBody] DataOption dataOption, CancellationToken ct)
        {
            dataOption.ModifiedBy = User.GetId();
            var updatedDataOption = await _dataOptionService.UpdateAsync(id, dataOption, ct);
            return Ok(updatedDataOption);
        }

        /// <summary>
        /// Deletes a  DataOption
        /// </summary>
        /// <remarks>
        /// Deletes a  DataOption with the specified id
        /// <para />
        /// Accessible only to a ContentDeveloper or an Administrator
        /// </remarks>
        /// <param name="id">The id of the DataOption to delete</param>
        /// <param name="ct"></param>
        [HttpDelete("dataOptions/{id}")]
        [ProducesResponseType((int)HttpStatusCode.NoContent)]
        [SwaggerOperation(OperationId = "deleteDataOption")]
        public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
        {
            await _dataOptionService.DeleteAsync(id, ct);
            return NoContent();
        }

    }
}

