// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Net;

namespace Blueprint.Api.Controllers
{
    [Authorize]
    [Route("api/")]
    [ApiController]
    public abstract class BaseController : ControllerBase
    {
    }
}
