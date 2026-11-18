// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System.Collections.Generic;
using System.Linq;
using Blueprint.Api.Data.Models;
using Blueprint.Api.ViewModels;
using Riok.Mapperly.Abstractions;

namespace Blueprint.Api.Infrastructure.Mappings;

/// <summary>User team roles' mappings.</summary>
[Mapper]
public static partial class UserTeamRoleMapper
{
    public static partial UserTeamRole ToUserTeamRole(this UserTeamRoleEntity source);

    [MapperRequiredMapping(RequiredMappingStrategy.Source)]
    public static partial UserTeamRoleEntity ToEntity(this UserTeamRole source);
}
