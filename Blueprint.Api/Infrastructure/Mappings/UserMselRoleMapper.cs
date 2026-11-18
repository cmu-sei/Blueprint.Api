// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System.Collections.Generic;
using System.Linq;
using Blueprint.Api.Data.Models;
using Blueprint.Api.ViewModels;
using Riok.Mapperly.Abstractions;

namespace Blueprint.Api.Infrastructure.Mappings;

/// <summary>User MSEL roles' mappings.</summary>
[Mapper]
public static partial class UserMselRoleMapper
{
    public static partial UserMselRole ToUserMselRole(this UserMselRoleEntity source);

    [MapperRequiredMapping(RequiredMappingStrategy.Source)]
    public static partial UserMselRoleEntity ToEntity(this UserMselRole source);
}
