// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using System.Linq;
using System.Net;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Blueprint.Api.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Swashbuckle.AspNetCore.Annotations;

namespace Blueprint.Api.Controllers
{
    
    [AllowAnonymous]
    public class HealthCheckController : BaseController
    {
        private readonly HealthCheckService healthCheckService;

        public HealthCheckController(HealthCheckService healthCheckService)
        {
            this.healthCheckService = healthCheckService;
        }

        /// <summary>
        /// Checks the liveliness health endpoint
        /// </summary>
        /// <remarks>
        /// Returns a HealthReport of the liveliness health check
        /// </remarks>
        /// <returns></returns>
        [HttpGet("health/live")]
        [ProducesResponseType(typeof(HealthReport), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "getLiveliness")]
        public async Task<IActionResult> GetLiveliness(CancellationToken ct)
        {
            HealthReport report = await this.healthCheckService.CheckHealthAsync((check) => check.Tags.Contains("live"));
            var result = new
            {
                status = report.Status.ToString()
            };
            return report.Status == HealthStatus.Healthy ? this.Ok(result) : this.StatusCode((int)HttpStatusCode.ServiceUnavailable, result);
        }

        /// <summary>
        /// Checks the readiness health endpoint
        /// </summary>
        /// <remarks>
        /// Returns a HealthReport of the readiness health check
        /// </remarks>
        /// <returns></returns>
        [HttpGet("health/ready")]
        [ProducesResponseType(typeof(HealthReport), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "getReadiness")]
        public async Task<IActionResult> GetReadiness(CancellationToken ct)
        {
            HealthReport report = await this.healthCheckService.CheckHealthAsync((check) => check.Tags.Contains("ready"));
            var result = new
            {
                status = report.Status.ToString()
            };
            return report.Status == HealthStatus.Healthy ? this.Ok(result) : this.StatusCode((int)HttpStatusCode.ServiceUnavailable, result);
        }

        /// <summary>
        /// Returns the current version of the API
        /// </summary>
        /// <remarks>
        /// Returns the version.
        /// </remarks>
        /// <returns></returns>
        [HttpGet("version")]
        [ProducesResponseType(typeof(string), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "getVersion")]
        public async Task<IActionResult> GetVersion(CancellationToken ct)
        {
            var version = (AssemblyInformationalVersionAttribute)Assembly
                .GetExecutingAssembly()
                .GetCustomAttributes(typeof(AssemblyInformationalVersionAttribute), false)
                .FirstOrDefault();

            return Ok(version.InformationalVersion);
        }

    }
}
