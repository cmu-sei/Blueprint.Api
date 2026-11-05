// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System.Collections.Generic;
using System.Linq;
using Blueprint.Api.Data.Models;
using Blueprint.Api.ViewModels;
using Riok.Mapperly.Abstractions;

namespace Blueprint.Api.Infrastructure.Mappings;

/// <summary>Proficiency levels' mappings.</summary>
[Mapper]
public static partial class ProficiencyLevelMapper
{
    public static partial ProficiencyLevel ToProficiencyLevel(this ProficiencyLevelEntity source);

    /// <remarks>A level nested in a scale's create or update body.</remarks>
    [MapperRequiredMapping(RequiredMappingStrategy.Source)]
    public static partial ProficiencyLevelEntity ToEntity(this ProficiencyLevel source);

    [MapperRequiredMapping(RequiredMappingStrategy.Source)]
    public static partial ProficiencyLevelEntity ToEntity(this ProficiencyLevelCreate source);

    [MapperRequiredMapping(RequiredMappingStrategy.Source)]
    public static partial void ApplyTo(this ProficiencyLevelEdit source, ProficiencyLevelEntity target);
}
