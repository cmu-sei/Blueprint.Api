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

        /// <summary>
        /// Gets the Player views the caller can see
        /// </summary>
        /// <remarks>
        /// The views an automation task can be tested against: Player answers as the caller, so the list is theirs.
        /// </remarks>
        [HttpGet("player/views")]
        [ProducesResponseType(typeof(IEnumerable<View>), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "getMyPlayerViews")]
        public async Task<IActionResult> GetMyViews(CancellationToken ct)
        {
            return Ok(await _playerService.GetMyViewsAsync(ct));
        }

    }

}
