// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System.Collections.Generic;
using System.Linq;
using Blueprint.Api.Data.Models;
using Blueprint.Api.ViewModels;
using Riok.Mapperly.Abstractions;

namespace Blueprint.Api.Infrastructure.Mappings;

/// <summary>Card teams' mappings.</summary>
[Mapper]
public static partial class CardTeamMapper
{
    public static partial CardTeam ToCardTeam(this CardTeamEntity source);

    [MapperRequiredMapping(RequiredMappingStrategy.Source)]
    public static partial CardTeamEntity ToEntity(this CardTeamCreate source);

    [MapperRequiredMapping(RequiredMappingStrategy.Source)]
    public static partial void ApplyTo(this CardTeamEdit source, CardTeamEntity target);
}
