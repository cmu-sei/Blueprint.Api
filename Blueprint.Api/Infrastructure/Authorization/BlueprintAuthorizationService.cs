// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using System.Threading;
using System.Threading.Tasks;
using Blueprint.Api.Data.Enumerations;
using Blueprint.Api.Services;
using Microsoft.AspNetCore.Authorization;

namespace Blueprint.Api.Infrastructure.Authorization
{
    public interface IBlueprintAuthorizationService
    {
        Task<bool> AuthorizeAsync(SystemPermission[] requiredSystemPermissions, CancellationToken cancellationToken);
    }

    public class BlueprintAuthorizationService : IBlueprintAuthorizationService
    {
        private readonly IAuthorizationService _authorizationService;
        private readonly IUserClaimsService _userClaimsService;

        public BlueprintAuthorizationService(
            IAuthorizationService authorizationService,
            IUserClaimsService userClaimsService)
        {
            _authorizationService = authorizationService;
            _userClaimsService = userClaimsService;
        }

        public async Task<bool> AuthorizeAsync(SystemPermission[] requiredSystemPermissions, CancellationToken cancellationToken)
        {
            var claimsPrincipal = _userClaimsService.GetCurrentClaimsPrincipal();
            var permissionRequirement = new SystemPermissionRequirement(requiredSystemPermissions);
            var permissionResult = await _authorizationService.AuthorizeAsync(claimsPrincipal, null, permissionRequirement);

            return permissionResult.Succeeded;
        }
    }
}
