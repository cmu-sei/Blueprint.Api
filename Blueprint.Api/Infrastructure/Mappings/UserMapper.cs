// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System.Collections.Generic;
using System.Linq;
using Blueprint.Api.Data.Models;
using Blueprint.Api.ViewModels;
using Riok.Mapperly.Abstractions;

namespace Blueprint.Api.Infrastructure.Mappings;

/// <summary>Users' mappings.</summary>
[Mapper]
public static partial class UserMapper
{
    /// <remarks><see cref="User.Permissions"/> is left null: a user's permissions come from its system role.</remarks>
    [MapperIgnoreTarget(nameof(User.Permissions))]
    public static partial User ToUser(this UserEntity source);

    /// <remarks>The same members as <see cref="ToUser"/>, read in the query.</remarks>
    public static partial IQueryable<User> ProjectToUser(this IQueryable<UserEntity> query);

    [MapperRequiredMapping(RequiredMappingStrategy.Source)]
    [MapperIgnoreSource(nameof(User.Permissions))]
    public static partial UserEntity ToEntity(this User source);

    /// <remarks>
    /// Writes the body's id onto the row as well, so a body whose id is not the route's (or has none) tries
    /// to change the key and fails the save. A body without a role id takes the role away.
    /// </remarks>
    [MapperRequiredMapping(RequiredMappingStrategy.Source)]
    [MapperIgnoreSource(nameof(User.Permissions))]
    public static partial void ApplyTo(this User source, UserEntity target);
}
