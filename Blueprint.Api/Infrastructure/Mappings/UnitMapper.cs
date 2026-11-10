// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System.Collections.Generic;
using System.Linq;
using Blueprint.Api.Data.Models;
using Blueprint.Api.ViewModels;
using Riok.Mapperly.Abstractions;

namespace Blueprint.Api.Infrastructure.Mappings;

/// <summary>Units' mappings.</summary>
[Mapper]
[UseStaticMapper(typeof(UserMapper))]
public static partial class UnitMapper
{
    /// <remarks><see cref="Unit.Users"/> holds the user of each membership the context has loaded.</remarks>
    [MapProperty(nameof(UnitEntity.UnitUsers), nameof(Unit.Users), Use = nameof(MapUsers))]
    public static partial Unit ToUnit(this UnitEntity source);

    /// <remarks>The JSON upload and the create. A unit's users are never written from the body.</remarks>
    [MapperRequiredMapping(RequiredMappingStrategy.Source)]
    [MapperIgnoreSource(nameof(Unit.Users))]
    public static partial UnitEntity ToEntity(this Unit source);

    /// <remarks>
    /// Writes the body's id onto the row as well, so a body whose id is not the route's (or has none) tries
    /// to change the key and fails the save.
    /// </remarks>
    [MapperRequiredMapping(RequiredMappingStrategy.Source)]
    [MapperIgnoreSource(nameof(Unit.Users))]
    public static partial void ApplyTo(this Unit source, UnitEntity target);

    // A membership whose user is not loaded is a null user, as AutoMapper left it.
    private static ICollection<User> MapUsers(ICollection<UnitUserEntity> unitUsers) =>
        unitUsers == null ? [] : unitUsers.Select(x => x.User == null ? null : x.User.ToUser()).ToList();
}
