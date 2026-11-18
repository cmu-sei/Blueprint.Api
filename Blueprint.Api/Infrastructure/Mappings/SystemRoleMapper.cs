// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System.Collections.Generic;
using System.Linq;
using Blueprint.Api.Data.Enumerations;
using Blueprint.Api.Data.Models;
using Blueprint.Api.ViewModels;
using Riok.Mapperly.Abstractions;

namespace Blueprint.Api.Infrastructure.Mappings;

/// <summary>System roles' mappings. Missing permissions are an empty list in both directions, never null.</summary>
[Mapper]
public static partial class SystemRoleMapper
{
    public static partial SystemRole ToSystemRole(this SystemRoleEntity source);

    [MapperRequiredMapping(RequiredMappingStrategy.Source)]
    public static partial SystemRoleEntity ToEntity(this SystemRole source);

    /// <remarks>
    /// Writes the body's id onto the row as well, so a body whose id is not the route's (or has none) tries
    /// to change the key and fails the save.
    /// </remarks>
    [MapperRequiredMapping(RequiredMappingStrategy.Source)]
    public static partial void ApplyTo(this SystemRole source, SystemRoleEntity target);

    /// <remarks>The stored role written back onto the update's body, which is what the update answers.</remarks>
    public static partial void ApplyTo(this SystemRoleEntity source, SystemRole target);

    private static List<SystemPermission> MapPermissions(List<SystemPermission> source) =>
        source == null ? [] : source.ToList();
}
