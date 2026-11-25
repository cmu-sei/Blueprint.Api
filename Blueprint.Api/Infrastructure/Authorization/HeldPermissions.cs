// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using Blueprint.Api.Data.Enumerations;

namespace Blueprint.Api.Infrastructure.Authorization
{
    /// <summary>
    /// Whether a caller holds every permission a role grants. Nobody may hand out a permission they do not hold
    /// themselves, by giving a user a role or by putting the permission on a role, so neither <c>ManageUsers</c>
    /// nor <c>ManageRoles</c> becomes every permission in the installation.
    /// </summary>
    public static class HeldPermissions
    {
        public static bool HoldsAll(ClaimsPrincipal caller, bool allPermissions, IEnumerable<SystemPermission> permissions)
        {
            var granted = allPermissions
                ? Enum.GetValues<SystemPermission>()
                : permissions ?? [];
            var held = caller.Claims
                .Where(c => c.Type == AuthorizationConstants.PermissionClaimType)
                .Select(c => c.Value)
                .ToHashSet();

            // a value the enum does not define grants nothing, so it asks nothing
            return granted.Where(Enum.IsDefined).All(p => held.Contains(p.ToString()));
        }
    }
}
