// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System.Collections.Generic;
using System.Linq;
using Blueprint.Api.Data.Models;
using Blueprint.Api.ViewModels;
using Riok.Mapperly.Abstractions;

namespace Blueprint.Api.Infrastructure.Mappings;

/// <summary>Player application teams' mappings.</summary>
[Mapper]
public static partial class PlayerApplicationTeamMapper
{
    public static partial PlayerApplicationTeam ToPlayerApplicationTeam(this PlayerApplicationTeamEntity source);

    [MapperRequiredMapping(RequiredMappingStrategy.Source)]
    public static partial PlayerApplicationTeamEntity ToEntity(this PlayerApplicationTeamCreate source);

    [MapperRequiredMapping(RequiredMappingStrategy.Source)]
    public static partial void ApplyTo(this PlayerApplicationTeamEdit source, PlayerApplicationTeamEntity target);
}
