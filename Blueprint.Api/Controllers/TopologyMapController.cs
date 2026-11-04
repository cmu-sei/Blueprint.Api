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
    public class TopologyMapController : BaseController
    {
        private readonly ITopologyMapService _topologyMapService;
        private readonly IBlueprintAuthorizationService _authorizationService;

        public TopologyMapController(ITopologyMapService topologyMapService, IBlueprintAuthorizationService authorizationService)
        {
            _topologyMapService = topologyMapService;
            _authorizationService = authorizationService;
        }

        /// <summary>
        /// Draws a MSEL's network map from its Caster workspace
        /// </summary>
        /// <remarks>
        /// Draws the workspace's machines and networks where Caster's topology view placed them, labelled
        /// with the machines' Player names, and the click points Player would be given. Nothing is sent to
        /// Player. Caster is read with the caller's own token.
        /// <para />
        /// Accessible only to a ContentDeveloper or MSEL owner
        /// </remarks>
        /// <param name="id">The id of the MSEL</param>
        /// <param name="ct"></param>
        [HttpGet("msels/{id}/topology-map/preview")]
        [ProducesResponseType(typeof(TopologyMapPreview), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "previewTopologyMap")]
        public async Task<IActionResult> Preview([FromRoute] Guid id, CancellationToken ct)
        {
            var hasSystemPermission = await _authorizationService.AuthorizeAsync([SystemPermission.ManageMsels], ct);
            var preview = await _topologyMapService.PreviewAsync(id, hasSystemPermission, ct);
            return Ok(preview);
        }

        /// <summary>
        /// Creates or updates a MSEL's network map in Player
        /// </summary>
        /// <remarks>
        /// Redraws the map from the Caster workspace's saved layout and writes it to the MSEL's Player view
        /// as a VM map, replacing the one an earlier update made, and adds a "Network Map" application for
        /// the map's teams if the view has none. Changes made in Player's map editor are overwritten.
        /// <para />
        /// Accessible only to a ContentDeveloper or MSEL owner
        /// </remarks>
        /// <param name="id">The id of the MSEL</param>
        /// <param name="ct"></param>
        [HttpPost("msels/{id}/topology-map")]
        [ProducesResponseType(typeof(TopologyMapResult), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "updateTopologyMap")]
        public async Task<IActionResult> Update([FromRoute] Guid id, CancellationToken ct)
        {
            var hasSystemPermission = await _authorizationService.AuthorizeAsync([SystemPermission.ManageMsels], ct);
            var result = await _topologyMapService.UpdateAsync(id, hasSystemPermission, ct);
            return Ok(result);
        }
    }
}
