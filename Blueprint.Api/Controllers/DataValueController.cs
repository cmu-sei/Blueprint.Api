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
using Blueprint.Api.Services;
using Blueprint.Api.ViewModels;
using Swashbuckle.AspNetCore.Annotations;

namespace Blueprint.Api.Controllers
{
    public class DataValueController : BaseController
    {
        private readonly IDataValueService _dataValueService;
        private readonly IAuthorizationService _authorizationService;

        public DataValueController(IDataValueService dataValueService, IAuthorizationService authorizationService)
        {
            _dataValueService = dataValueService;
            _authorizationService = authorizationService;
        }

        /// <summary>
        /// Gets DataValues by msel
        /// </summary>
        /// <remarks>
        /// Returns a list of DataValues for the msel.
        /// </remarks>
        /// <param name="mselId"></param>
        /// <param name="ct"></param>
        /// <returns></returns>
        [HttpGet("msels/{mselId}/datavalues")]
        [ProducesResponseType(typeof(IEnumerable<DataValue>), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "getDataValuesByMsel")]
        public async Task<IActionResult> GetByMsel(Guid mselId, CancellationToken ct)
        {
            var list = await _dataValueService.GetByMselAsync(mselId, ct);
            return Ok(list);
        }

        /// <summary>
        /// Gets a specific DataValue by id
        /// </summary>
        /// <remarks>
        /// Returns the DataValue with the id specified
        /// </remarks>
        /// <param name="id">The id of the DataValue</param>
        /// <param name="ct"></param>
        /// <returns></returns>
        [HttpGet("dataValues/{id}")]
        [ProducesResponseType(typeof(DataValue), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "getDataValue")]
        public async Task<IActionResult> Get(Guid id, CancellationToken ct)
        {
            var dataValue = await _dataValueService.GetAsync(id, ct);

            if (dataValue == null)
                throw new EntityNotFoundException<DataValue>();

            return Ok(dataValue);
        }

        /// <summary>
        /// Creates a new DataValue
        /// </summary>
        /// <remarks>
        /// Creates a new DataValue with the attributes specified
        /// <para />
        /// Accessible only to a ContentDeveloper or an Administrator
        /// </remarks>
        /// <param name="dataValue">The data used to create the DataValue</param>
        /// <param name="ct"></param>
        [HttpPost("dataValues")]
        [ProducesResponseType(typeof(DataValue), (int)HttpStatusCode.Created)]
        [SwaggerOperation(OperationId = "createDataValue")]
        public async Task<IActionResult> Create([FromBody] DataValue dataValue, CancellationToken ct)
        {
            dataValue.CreatedBy = User.GetId();
            var createdDataValue = await _dataValueService.CreateAsync(dataValue, ct);
            return CreatedAtAction(nameof(this.Get), new { id = createdDataValue.Id }, createdDataValue);
        }

        /// <summary>
        /// Updates a  DataValue
        /// </summary>
        /// <remarks>
        /// Updates a DataValue with the attributes specified.
        /// The ID from the route MUST MATCH the ID contained in the dataValue parameter
        /// <para />
        /// Accessible only to a ContentDeveloper or an Administrator
        /// </remarks>
        /// <param name="id">The Id of the DataValue to update</param>
        /// <param name="dataValue">The updated DataValue values</param>
        /// <param name="ct"></param>
        [HttpPut("dataValues/{id}")]
        [ProducesResponseType(typeof(DataValue), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "updateDataValue")]
        public async Task<IActionResult> Update([FromRoute] Guid id, [FromBody] DataValue dataValue, CancellationToken ct)
        {
            dataValue.ModifiedBy = User.GetId();
            var updatedDataValue = await _dataValueService.UpdateAsync(id, dataValue, ct);
            return Ok(updatedDataValue);
        }

        /// <summary>
        /// Deletes a  DataValue
        /// </summary>
        /// <remarks>
        /// Deletes a  DataValue with the specified id
        /// <para />
        /// Accessible only to a ContentDeveloper or an Administrator
        /// </remarks>
        /// <param name="id">The id of the DataValue to delete</param>
        /// <param name="ct"></param>
        [HttpDelete("dataValues/{id}")]
        [ProducesResponseType((int)HttpStatusCode.NoContent)]
        [SwaggerOperation(OperationId = "deleteDataValue")]
        public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
        {
            await _dataValueService.DeleteAsync(id, ct);
            return NoContent();
        }

    }
}
