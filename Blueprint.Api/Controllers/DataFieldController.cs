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
    public class DataFieldController : BaseController
    {
        private readonly IDataFieldService _dataFieldService;
        private readonly IAuthorizationService _authorizationService;

        public DataFieldController(IDataFieldService dataFieldService, IAuthorizationService authorizationService)
        {
            _dataFieldService = dataFieldService;
            _authorizationService = authorizationService;
        }

        /// <summary>
        /// Gets DataField Templates
        /// </summary>
        /// <remarks>
        /// Returns a list of DataField Templates
        /// </remarks>
        /// <param name="ct"></param>
        /// <returns></returns>
        [HttpGet("dataFields/templates")]
        [ProducesResponseType(typeof(IEnumerable<DataField>), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "getDataFieldTemplates")]
        public async Task<IActionResult> GetDataFieldTemplates(CancellationToken ct)
        {
            var list = await _dataFieldService.GetTemplatesAsync(ct);
            return Ok(list);
        }

        /// <summary>
        /// Gets DataFields by msel
        /// </summary>
        /// <remarks>
        /// Returns a list of DataFields for the msel.
        /// </remarks>
        /// <param name="mselId"></param>
        /// <param name="ct"></param>
        /// <returns></returns>
        [HttpGet("msels/{mselId}/dataFields")]
        [ProducesResponseType(typeof(IEnumerable<DataField>), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "getByMsel")]
        public async Task<IActionResult> GetByMsel(Guid mselId, CancellationToken ct)
        {
            var list = await _dataFieldService.GetByMselAsync(mselId, ct);
            return Ok(list);
        }

        /// <summary>
        /// Gets a specific DataField by id
        /// </summary>
        /// <remarks>
        /// Returns the DataField with the id specified
        /// </remarks>
        /// <param name="id">The id of the DataField</param>
        /// <param name="ct"></param>
        /// <returns></returns>
        [HttpGet("dataFields/{id}")]
        [ProducesResponseType(typeof(DataField), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "getDataField")]
        public async Task<IActionResult> Get(Guid id, CancellationToken ct)
        {
            var dataField = await _dataFieldService.GetAsync(id, ct);

            if (dataField == null)
                throw new EntityNotFoundException<DataField>();

            return Ok(dataField);
        }

        /// <summary>
        /// Creates a new DataField
        /// </summary>
        /// <remarks>
        /// Creates a new DataField with the attributes specified
        /// <para />
        /// Accessible only to a ContentDeveloper or an Administrator
        /// </remarks>
        /// <param name="dataField">The data used to create the DataField</param>
        /// <param name="ct"></param>
        [HttpPost("dataFields")]
        [ProducesResponseType(typeof(DataField), (int)HttpStatusCode.Created)]
        [SwaggerOperation(OperationId = "createDataField")]
        public async Task<IActionResult> Create([FromBody] DataField dataField, CancellationToken ct)
        {
            dataField.CreatedBy = User.GetId();
            var result = await _dataFieldService.CreateAsync(dataField, ct);
            return Ok(result);
        }

        /// <summary>
        /// Updates a  DataField
        /// </summary>
        /// <remarks>
        /// Updates a DataField with the attributes specified.
        /// The ID from the route MUST MATCH the ID contained in the dataField parameter
        /// <para />
        /// Accessible only to a ContentDeveloper or an Administrator
        /// </remarks>
        /// <param name="id">The Id of the DataField to update</param>
        /// <param name="dataField">The updated DataField values</param>
        /// <param name="ct"></param>
        [HttpPut("dataFields/{id}")]
        [ProducesResponseType(typeof(DataField), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "updateDataField")]
        public async Task<IActionResult> Update([FromRoute] Guid id, [FromBody] DataField dataField, CancellationToken ct)
        {
            dataField.ModifiedBy = User.GetId();
            var result = await _dataFieldService.UpdateAsync(id, dataField, ct);
            return Ok(result);
        }

        /// <summary>
        /// Deletes a  DataField
        /// </summary>
        /// <remarks>
        /// Deletes a  DataField with the specified id
        /// <para />
        /// Accessible only to a ContentDeveloper or an Administrator
        /// </remarks>
        /// <param name="id">The id of the DataField to delete</param>
        /// <param name="ct"></param>
        [HttpDelete("dataFields/{id}")]
        [ProducesResponseType(typeof(Guid), (int)HttpStatusCode.NoContent)]
        [SwaggerOperation(OperationId = "deleteDataField")]
        public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
        {
            var result = await _dataFieldService.DeleteAsync(id, ct);
            return Ok(result);
        }

    }
}

