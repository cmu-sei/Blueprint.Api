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
    public class MselController : BaseController
    {
        private readonly IMselService _mselService;
        private readonly IAuthorizationService _authorizationService;

        public MselController(
            IMselService mselService,
            ICiteService citeService,
            IPlayerService playerService,
            IAuthorizationService authorizationService)
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
        [HttpGet("my-msels")]
        [ProducesResponseType(typeof(IEnumerable<Msel>), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "getMyMsels")]
        public async Task<IActionResult> GetMine(CancellationToken ct)
        {
            var list = await _mselService.GetMineAsync(ct);
            return Ok(list);
        }

        /// <summary>
        /// Gets Msels the current user can join
        /// </summary>
        /// <remarks>
        /// Returns a list of the current user's Msels to join.
        /// </remarks>
        /// <param name="ct"></param>
        /// <returns></returns>
        [HttpGet("my-join-msels")]
        [ProducesResponseType(typeof(IEnumerable<Msel>), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "getMyJoinMsels")]
        public async Task<IActionResult> GetMyJoinMsels(CancellationToken ct)
        {
            var list = await _mselService.GetMyJoinInvitationMselsAsync(ct);
            return Ok(list);
        }

        /// <summary>
        /// Gets Msels the current user can launch
        /// </summary>
        /// <remarks>
        /// Returns a list of the current user's Msels to launch.
        /// </remarks>
        /// <param name="ct"></param>
        /// <returns></returns>
        [HttpGet("my-launch-msels")]
        [ProducesResponseType(typeof(IEnumerable<Msel>), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "getMyLaunchMsels")]
        public async Task<IActionResult> GetMyLaunchMsels(CancellationToken ct)
        {
            var list = await _mselService.GetMyLaunchInvitationMselsAsync(ct);
            return Ok(list);
        }

        /// <summary>
        /// Gets Msels for requested user
        /// </summary>
        /// <remarks>
        /// Returns a list of the requested user's active Msels.
        /// </remarks>
        /// <param name="userId"></param>
        /// <param name="ct"></param>
        /// <returns></returns>
        [HttpGet("users/{userId}/msels")]
        [ProducesResponseType(typeof(IEnumerable<Msel>), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "getUserMsels")]
        public async Task<IActionResult> GetUserMsels(Guid userId, CancellationToken ct)
        {
            var list = await _mselService.GetUserMselsAsync(userId, ct);
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
        /// Creates a new MSEL by copying an existing MSEL
        /// </summary>
        /// <remarks>
        /// Creates a new MSEL from the specified existing MSEL
        /// <para />
        /// Accessible only to a ContentDeveloper or an Administrator
        /// </remarks>
        /// <param name="id">The ID of the MSEL to be copied</param>
        /// <param name="ct"></param>
        [HttpPost("msels/{id}/copy")]
        [ProducesResponseType(typeof(Msel), (int)HttpStatusCode.Created)]
        [SwaggerOperation(OperationId = "copyMsel")]
        public async Task<IActionResult> Copy(Guid id, CancellationToken ct)
        {
            var createdMsel = await _mselService.CopyAsync(id, ct);
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
        /// Adds a Team to a Msel
        /// </summary>
        /// <remarks>
        /// Adds the team specified to the MSEL specified
        /// <para />
        /// Accessible only to a ContentDeveloper or a MSEL owner
        /// </remarks>
        /// <param name="mselId">The ID of the Msel to update</param>
        /// <param name="teamId">The ID of the Team</param>
        /// <param name="ct"></param>
        [HttpPut("msels/{mselId}/addteam/{teamId}")]
        [ProducesResponseType(typeof(Msel), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "AddTeamToMsel")]
        public async Task<IActionResult> AddTeamToMsel([FromRoute] Guid mselId, [FromRoute] Guid teamId, CancellationToken ct)
        {
            var msel = await _mselService.AddTeamToMselAsync(mselId, teamId, ct);
            return Ok(msel);
        }

        /// <summary>
        /// Removes a Team from a Msel
        /// </summary>
        /// <remarks>
        /// Removes the team specified from the MSEL specified
        /// <para />
        /// Accessible only to a ContentDeveloper or a MSEL owner
        /// </remarks>
        /// <param name="mselId">The ID of the Msel to update</param>
        /// <param name="teamId">The ID of the Team</param>
        /// <param name="ct"></param>
        [HttpPut("msels/{mselId}/removeteam/{teamId}")]
        [ProducesResponseType(typeof(Msel), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "RemoveTeamFromMsel")]
        public async Task<IActionResult> RemoveTeamFromMsel([FromRoute] Guid mselId, [FromRoute] Guid teamId, CancellationToken ct)
        {
            var msel = await _mselService.RemoveTeamFromMselAsync(mselId, teamId, ct);
            return Ok(msel);
        }

        /// <summary>
        /// Adds a User Role to a Msel
        /// </summary>
        /// <remarks>
        /// Adds the User Role specified to the MSEL specified
        /// <para />
        /// Accessible only to a ContentDeveloper or a MSEL owner
        /// </remarks>
        /// <param name="userId">The ID of the User</param>
        /// <param name="mselId">The ID of the Msel to update</param>
        /// <param name="mselRole">The MSEL Role to add</param>
        /// <param name="ct"></param>
        [HttpPut("msels/{mselId}/user/{userId}/role/{mselRole}/add")]
        [ProducesResponseType(typeof(Msel), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "AddUserMselRole")]
        public async Task<IActionResult> AddUserMselRole([FromRoute] Guid userId, [FromRoute] Guid mselId, [FromRoute] MselRole mselRole, CancellationToken ct)
        {
            var msel = await _mselService.AddUserMselRoleAsync(userId, mselId, mselRole, ct);
            return Ok(msel);
        }

        /// <summary>
        /// Removes a User Role from a Msel
        /// </summary>
        /// <remarks>
        /// Removes the User Role specified from the MSEL specified
        /// <para />
        /// Accessible only to a ContentDeveloper or a MSEL owner
        /// </remarks>
        /// <param name="userId">The ID of the User</param>
        /// <param name="mselId">The ID of the Msel to update</param>
        /// <param name="mselRole">The MSEL Role to add</param>
        /// <param name="ct"></param>
        [HttpPut("msels/{mselId}/user/{userId}/role/{mselRole}/remove")]
        [ProducesResponseType(typeof(Msel), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "RemoveUserMselRole")]
        public async Task<IActionResult> RemoveUserMselRole([FromRoute] Guid userId, [FromRoute] Guid mselId, [FromRoute] MselRole mselRole, CancellationToken ct)
        {
            var msel = await _mselService.RemoveUserMselRoleAsync(userId, mselId, mselRole, ct);
            return Ok(msel);
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

        /// <summary> Upload file </summary>
        /// <remarks> File objects will be returned in the same order as their respective files within the form. </remarks>
        /// <param name="form"> The files to upload and their settings </param>
        /// <param name="ct"></param>
        [HttpPost("msels/xlsx")]
        [ProducesResponseType((int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "uploadXlsx")]
        public async Task<IActionResult> UploadXlsxAsync([FromForm] FileForm form, CancellationToken ct)
        {
            var result = await _mselService.UploadXlsxAsync(form, ct);
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
        [SwaggerOperation(OperationId = "downloadXlsx")]
        public async Task<IActionResult> DownloadXlsxAsync(Guid id, CancellationToken ct)
        {
            (var stream, var fileName) = await _mselService.DownloadXlsxAsync(id, ct);

            // If this is wrapped in an Ok, it throws an exception
            return File(stream, "application/octet-stream", fileName);
        }

        /// <summary> Upload a json MSEL file </summary>
        /// <param name="form"> The files to upload and their settings </param>
        /// <param name="ct"></param>
        [HttpPost("msels/json")]
        [ProducesResponseType(typeof(Msel), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "uploadJsonFiles")]
        public async Task<IActionResult> UploadJsonAsync([FromForm] FileForm form, CancellationToken ct)
        {
            var result = await _mselService.UploadJsonAsync(form, ct);
            return Ok(result);
        }

        /// <summary> Download a msel by id as json file </summary>
        /// <param name="id"> The id of the msel </param>
        /// <param name="ct"></param>
        [HttpGet("msels/{id}/json")]
        [ProducesResponseType(typeof(FileResult), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "downloadJson")]
        public async Task<IActionResult> DownloadJsonAsync(Guid id, CancellationToken ct)
        {
            (var stream, var fileName) = await _mselService.DownloadJsonAsync(id, ct);

            // If this is wrapped in an Ok, it throws an exception
            return File(stream, "application/octet-stream", fileName);
        }

        //
        // Integration Section
        //

        /// <summary>
        /// Push Integrations
        /// </summary>
        /// <remarks>
        /// Pushes all MSEL Integrations to the associated applications
        /// <para />
        /// Accessible only to a ContentDeveloper or MSEL owner
        /// </remarks>
        /// <param name="id"></param>
        /// <param name="ct"></param>
        [HttpPost("msels/{id}/integrations")]
        [ProducesResponseType(typeof(ViewModels.Msel), (int)HttpStatusCode.Created)]
        [SwaggerOperation(OperationId = "pushIntegrations")]
        public async Task<IActionResult> PushIntegrations(Guid id, CancellationToken ct)
        {
            var msel = await _mselService.PushIntegrationsAsync(id, ct);
            return Ok(msel);
        }

        /// <summary>
        /// Pull Integrations
        /// </summary>
        /// <remarks>
        /// Pulls all Integrations from the associated applications
        /// <para />
        /// Accessible only to a ContentDeveloper or an Administrator
        /// </remarks>
        /// <param name="id">The id of the MSEL</param>
        /// <param name="ct"></param>
        [HttpDelete("msels/{id}/integrations")]
        [ProducesResponseType(typeof(ViewModels.Msel), (int)HttpStatusCode.NoContent)]
        [SwaggerOperation(OperationId = "pullIntegrations")]
        public async Task<IActionResult> PullIntegrations(Guid id, CancellationToken ct)
        {
            var msel = await _mselService.PullIntegrationsAsync(id, ct);
            return Ok(msel);
        }

        /// <summary>
        /// Join a MSEL by invitation
        /// </summary>
        /// <remarks>
        /// Joins the user to the msel based on a valid invitation
        /// <para />
        /// </remarks>
        /// <param name="id"></param>
        /// <param name="ct"></param>
        [HttpPost("msels/{id}/join")]
        [ProducesResponseType(typeof(Msel), (int)HttpStatusCode.Created)]
        [SwaggerOperation(OperationId = "joinMselByInvitation")]
        public async Task<IActionResult> JoinMsel(Guid id, CancellationToken ct)
        {
            var msel = await _mselService.JoinMselByInvitationAsync(id, ct);
            return Ok(msel);
        }

        /// <summary>
        /// Launch a MSEL by invitation
        /// </summary>
        /// <remarks>
        /// Launches the msel and joins the user to the team indicated by a valid invitation
        /// <para />
        /// </remarks>
        /// <param name="id"></param>
        /// <param name="ct"></param>
        [HttpPost("msels/{id}/launch")]
        [ProducesResponseType(typeof(Msel), (int)HttpStatusCode.Created)]
        [SwaggerOperation(OperationId = "launchMselByInvitation")]
        public async Task<IActionResult> LaunchMsel(Guid id, CancellationToken ct)
        {
            var msel = await _mselService.LaunchMselByInvitationAsync(id, ct);
            return Ok(msel);
        }

    }
}

