// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Blueprint.Api.Services;
using Blueprint.Api.ViewModels;
using Swashbuckle.AspNetCore.Annotations;

namespace Blueprint.Api.Controllers
{
    public class CasterController : BaseController
    {
        private readonly ICasterService _casterService;

        public CasterController(ICasterService casterService)
        {
            _casterService = casterService;
        }

        /// <summary>
        /// Gets the Caster directories the user can deploy from
        /// </summary>
        /// <remarks>
        /// Returns every directory, in every Caster project, that the signed-in user can view in Caster.
        /// The user's token must carry the caster scope.
        /// </remarks>
        /// <param name="ct"></param>
        /// <returns></returns>
        [HttpGet("caster/directories")]
        [ProducesResponseType(typeof(IEnumerable<CasterDirectory>), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "getCasterDirectories")]
        public async Task<IActionResult> GetDirectories(CancellationToken ct)
        {
            var list = await _casterService.GetDirectoriesAsync(ct);
            return Ok(list);
        }

        /// <summary>
        /// Gets the variables a MSEL team can be mapped to in a Caster directory
        /// </summary>
        /// <remarks>
        /// Returns the string variables the directory's template, including its parent directories,
        /// declares and does not already assign: the topology teams a MSEL team's Player team ID can be
        /// written to. The user's token must carry the caster scope.
        /// </remarks>
        /// <param name="id">The Caster directory ID</param>
        /// <param name="ct"></param>
        /// <returns></returns>
        [HttpGet("caster/directories/{id}/team-variables")]
        [ProducesResponseType(typeof(IEnumerable<string>), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "getCasterTeamVariables")]
        public async Task<IActionResult> GetTeamVariables([FromRoute] System.Guid id, CancellationToken ct)
        {
            var list = await _casterService.GetTeamVariablesAsync(id, ct);
            return Ok(list);
        }

    }

}
