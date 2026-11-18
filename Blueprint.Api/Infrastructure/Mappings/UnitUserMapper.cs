// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System.Collections.Generic;
using System.Linq;
using Blueprint.Api.Data.Models;
using Blueprint.Api.ViewModels;
using Riok.Mapperly.Abstractions;

namespace Blueprint.Api.Infrastructure.Mappings;

/// <summary>Unit memberships' mappings. The entity has no audit fields, so the view model's are always empty.</summary>
[Mapper]
[UseStaticMapper(typeof(UserMapper))]
[UseStaticMapper(typeof(UnitMapper))]
public static partial class UnitUserMapper
{
    [MapperIgnoreTarget(nameof(UnitUser.DateCreated))]
        [MapperIgnoreTarget(nameof(UnitUser.DateModified))]
        [MapperIgnoreTarget(nameof(UnitUser.CreatedBy))]
        [MapperIgnoreTarget(nameof(UnitUser.ModifiedBy))]
    public static partial UnitUser ToUnitUser(this UnitUserEntity source);

    /// <remarks>A user or unit nested in the body is created with the membership, which is then put on it.</remarks>
    [MapperRequiredMapping(RequiredMappingStrategy.Source)]
    [MapperIgnoreSource(nameof(UnitUser.DateCreated))]
        [MapperIgnoreSource(nameof(UnitUser.DateModified))]
        [MapperIgnoreSource(nameof(UnitUser.CreatedBy))]
        [MapperIgnoreSource(nameof(UnitUser.ModifiedBy))]
    public static partial UnitUserEntity ToEntity(this UnitUser source);
}
