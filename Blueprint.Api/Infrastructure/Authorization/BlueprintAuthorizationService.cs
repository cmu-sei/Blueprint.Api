// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Blueprint.Api.Data.Enumerations;
using Blueprint.Api.Infrastructure.Identity;
using Blueprint.Api.Services;
using Microsoft.AspNetCore.Authorization;

namespace Blueprint.Api.Infrastructure.Authorization
{
    public interface IBlueprintAuthorizationService
    {
        Task<bool> AuthorizeAsync(SystemPermission[] requiredSystemPermissions, CancellationToken cancellationToken);
        IEnumerable<SystemPermission> GetSystemPermissions();
    }

    public class BlueprintAuthorizationService : IBlueprintAuthorizationService
    {
        private readonly IAuthorizationService _authorizationService;
        private readonly IUserClaimsService _userClaimsService;
        private readonly IIdentityResolver _identityResolver;

        public BlueprintAuthorizationService(
            IAuthorizationService authorizationService,
            IUserClaimsService userClaimsService,
            IIdentityResolver identityResolver)
        {
            _authorizationService = authorizationService;
            _userClaimsService = userClaimsService;
            _identityResolver = identityResolver;
        }

        public async Task<bool> AuthorizeAsync(SystemPermission[] requiredSystemPermissions, CancellationToken cancellationToken)
        {
            var claimsPrincipal = _userClaimsService.GetCurrentClaimsPrincipal();

            // Fallback to identity resolver if current principal is null (e.g., during SignalR hub connection)
            if (claimsPrincipal == null)
            {
                claimsPrincipal = _identityResolver.GetClaimsPrincipal();
            }

            var permissionRequirement = new SystemPermissionRequirement(requiredSystemPermissions);
            var permissionResult = await _authorizationService.AuthorizeAsync(claimsPrincipal, null, permissionRequirement);

            return permissionResult.Succeeded;
        }

        public IEnumerable<SystemPermission> GetSystemPermissions()
        {
            var principal = _identityResolver.GetClaimsPrincipal();
            var claims = principal.Claims;
            var permissions = claims
               .Where(x => x.Type == AuthorizationConstants.PermissionClaimType)
               .Select(x =>
               {
                   if (Enum.TryParse<SystemPermission>(x.Value, out var permission))
                       return permission;

                   return (SystemPermission?)null;
               })
               .Where(x => x.HasValue)
               .Select(x => x.Value)
               .ToList();
            return permissions;
        }

    }
}
