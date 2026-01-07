// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using System.Linq;
using System.Threading.Tasks;
using Blueprint.Api.Data.Enumerations;
using Microsoft.AspNetCore.Authorization;

namespace Blueprint.Api.Infrastructure.Authorization
{
    public class SystemPermissionRequirement : IAuthorizationRequirement
    {
        public SystemPermission[] RequiredPermissions;

        public SystemPermissionRequirement(SystemPermission[] requiredPermissions)
        {
            RequiredPermissions = requiredPermissions;
        }
    }

    public class SystemPermissionHandler : AuthorizationHandler<SystemPermissionRequirement>, IAuthorizationHandler
    {
        protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, SystemPermissionRequirement requirement)
        {
            if (context.User == null)
            {
                context.Fail();
            }
            else if (requirement.RequiredPermissions == null || requirement.RequiredPermissions.Length == 0)
            {
                context.Succeed(requirement);
            }
            else if (requirement.RequiredPermissions.Any(p => context.User.HasClaim(AuthorizationConstants.PermissionClaimType, p.ToString())))
            {
                context.Succeed(requirement);
            }

            return Task.CompletedTask;
        }
    }
}
