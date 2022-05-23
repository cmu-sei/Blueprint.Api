// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using Microsoft.AspNetCore.Authorization;
using System.Security.Claims;
using System.Threading.Tasks;

namespace Blueprint.Api.Infrastructure.Authorization
{
    public class FullRightsRequirement : IAuthorizationRequirement
    {
    }

    public class FullRightsHandler : AuthorizationHandler<FullRightsRequirement>, IAuthorizationHandler
    {
        protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, FullRightsRequirement requirement)
        {
            if(context.User.HasClaim(c => c.Type == BlueprintClaimTypes.SystemAdmin.ToString()))
            {
                context.Succeed(requirement);
            }

            return Task.CompletedTask;
        }
    }
}

