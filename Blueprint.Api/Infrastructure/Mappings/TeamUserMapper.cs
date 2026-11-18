// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System.Collections.Generic;
using System.Linq;
using Blueprint.Api.Data.Models;
using Blueprint.Api.ViewModels;
using Riok.Mapperly.Abstractions;

namespace Blueprint.Api.Infrastructure.Mappings;

/// <summary>Team memberships' mappings. The entity has no audit fields, so the view model's are always empty.</summary>
[Mapper]
public static partial class TeamUserMapper
{
    [MapperIgnoreTarget(nameof(TeamUser.DateCreated))]
        [MapperIgnoreTarget(nameof(TeamUser.DateModified))]
        [MapperIgnoreTarget(nameof(TeamUser.CreatedBy))]
        [MapperIgnoreTarget(nameof(TeamUser.ModifiedBy))]
    public static partial TeamUser ToTeamUser(this TeamUserEntity source);

    [MapperRequiredMapping(RequiredMappingStrategy.Source)]
    [MapperIgnoreSource(nameof(TeamUser.DateCreated))]
        [MapperIgnoreSource(nameof(TeamUser.DateModified))]
        [MapperIgnoreSource(nameof(TeamUser.CreatedBy))]
        [MapperIgnoreSource(nameof(TeamUser.ModifiedBy))]
    public static partial TeamUserEntity ToEntity(this TeamUser source);
}
