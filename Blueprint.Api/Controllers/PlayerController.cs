// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Blueprint.Api.Services;
using Swashbuckle.AspNetCore.Annotations;
using Player.Api.Client;
using System;

namespace Blueprint.Api.Controllers
{
    public class PlayerController : BaseController
    {
        private readonly IPlayerService _playerService;
        private readonly IAuthorizationService _authorizationService;

        public PlayerController(IPlayerService playerService, IAuthorizationService authorizationService)
        {
            _playerService = playerService;
            _authorizationService = authorizationService;
        }

        /// <summary>
        /// Gets all Views
        /// </summary>
        /// <remarks>
        /// Returns a list of all of the Views.
        /// </remarks>
        /// <param name="ct"></param>
        /// <returns></returns>
        [HttpGet("views")]
        [ProducesResponseType(typeof(IEnumerable<View>), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "getViews")]
        public async Task<IActionResult> GetViews(CancellationToken ct)
        {
            var list = await _playerService.GetViewsAsync(ct);
            return Ok(list);
        }

        /// <summary>
        /// Get View Teams
        /// </summary>
        /// <remarks>
        /// Returns a list of the View's Teams.
        /// </remarks>
        /// <param name="id"></param>
        /// <param name="ct"></param>
        /// <returns></returns>
        [HttpGet("views/{id}/teams")]
        [ProducesResponseType(typeof(IEnumerable<Team>), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "getViewTeams")]
        public async Task<IActionResult> GetViewTeams(Guid id, CancellationToken ct)
        {
            var list = await _playerService.GetViewTeamsAsync(id, ct);
            return Ok(list);
        }

        /// <summary>
        /// GetTeam Users
        /// </summary>
        /// <remarks>
        /// Returns a list of the Team's Users.
        /// </remarks>
        /// <param name="id"></param>
        /// <param name="ct"></param>
        /// <returns></returns>
        [HttpGet("views/teams/{id}/users")]
        [ProducesResponseType(typeof(IEnumerable<User>), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "getViewTeamUsers")]
        public async Task<IActionResult> GetViewTeamUsers(Guid id, CancellationToken ct)
        {
            var list = await _playerService.GetViewTeamUsersAsync(id, ct);
            return Ok(list);
        }

        /// <summary>
        /// Adds Player View Teams to MSEL
        /// </summary>
        /// <remarks>
        /// Adds the Player View's Teams to the specified MSEL.
        /// </remarks>
        /// <param name="id"></param>
        /// <param name="ct"></param>
        /// <returns></returns>
        [HttpPut("views/teams/msels/{id}")]
        [ProducesResponseType((int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "addViewTeamsToMsel")]
        public async Task<IActionResult> AddViewTeamsToMsel(Guid id, CancellationToken ct)
        {
            await _playerService.AddPlayerTeamsToMselAsync(id, ct);
            return Ok();
        }

        /// <summary>
        /// Gets all ApplicationTemplates
        /// </summary>
        /// <remarks>
        /// Returns a list of all of the ApplicationTemplates.
        /// </remarks>
        /// <param name="ct"></param>
        /// <returns></returns>
        [HttpGet("applicationTemplates")]
        [ProducesResponseType(typeof(IEnumerable<ApplicationTemplate>), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "getApplicationTemplates")]
        public async Task<IActionResult> GetApplicationTemplates(CancellationToken ct)
        {
            var list = await _playerService.GetApplicationTemplatesAsync(ct);
            return Ok(list);
        }

    }

}
