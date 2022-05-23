// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Blueprint.Api.Data;
using Microsoft.EntityFrameworkCore;
using Swashbuckle.AspNetCore.Annotations;

namespace Blueprint.Api.Controllers
{
    [Route("api/")]
    [ApiController]
    public class HealthCheckController : ControllerBase
    {
        private readonly BlueprintContext _context;

        public HealthCheckController(
            BlueprintContext context
        )
        {
            _context = context;
        }

        /// <summary>
        /// Responds when this api is functional
        /// </summary>
        /// <remarks>
        /// Returns a health message.
        /// <para />
        /// No user authentication is required
        /// </remarks>
        /// <returns></returns>
        [HttpGet("healthcheck")]
        [ProducesResponseType(typeof(string), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "healthCheck")]
        public async Task<IActionResult> HealthCheck(CancellationToken ct)
        {
            var healthMessage = "It is well";
            try
            {
                var dbCheck = await _context.Users.FirstAsync(x => x.Id != null);
            }
            catch (System.Exception ex)
            {
                healthMessage = "I'm sorry, but I currently can't access the database.  " + ex.Message;
            }
            return Ok(healthMessage);
        }

    }
}
