// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using Microsoft.AspNetCore.Authorization;
using System.Threading.Tasks;

namespace Blueprint.Api.Infrastructure.Authorization
{
    public class FacilitatorRequirement : IAuthorizationRequirement
    {
        public FacilitatorRequirement()
        {
        }
    }

    public class FacilitatorHandler : AuthorizationHandler<FacilitatorRequirement>, IAuthorizationHandler
    {
        protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, FacilitatorRequirement requirement)
        {
            if (context.User.HasClaim(c => c.Type == BlueprintClaimTypes.SystemAdmin.ToString()) ||
                context.User.HasClaim(c => c.Type == BlueprintClaimTypes.ContentDeveloper.ToString()) ||
                context.User.HasClaim(c => c.Type == BlueprintClaimTypes.Facilitator.ToString()))
            {
                context.Succeed(requirement);
            }

            return Task.CompletedTask;
        }
    }
}

