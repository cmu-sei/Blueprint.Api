// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System.Collections.Generic;
using System.Linq;
using Blueprint.Api.Data.Models;
using Blueprint.Api.ViewModels;
using Riok.Mapperly.Abstractions;

namespace Blueprint.Api.Infrastructure.Mappings;

/// <summary>Proficiency scales' mappings. Missing levels are an empty list in both directions, never null.</summary>
[Mapper]
[UseStaticMapper(typeof(ProficiencyLevelMapper))]
public static partial class ProficiencyScaleMapper
{
    public static partial ProficiencyScale ToProficiencyScale(this ProficiencyScaleEntity source);

    /// <remarks>The create and the JSON upload. Levels in the body are created with the scale.</remarks>
    [MapperRequiredMapping(RequiredMappingStrategy.Source)]
    public static partial ProficiencyScaleEntity ToEntity(this ProficiencyScale source);

    /// <remarks>
    /// Writes the body's id onto the row as well, so a body whose id is not the route's (or has none) tries
    /// to change the key and fails the save. The body's levels replace the unloaded collection, so the
    /// update writes each over the stored level with its id and leaves the scale's other levels alone.
    /// </remarks>
    [MapperRequiredMapping(RequiredMappingStrategy.Source)]
    public static partial void ApplyTo(this ProficiencyScale source, ProficiencyScaleEntity target);

    /// <remarks>The stored scale written back onto the update's body, which is what the update answers.</remarks>
    public static partial void ApplyTo(this ProficiencyScaleEntity source, ProficiencyScale target);

    private static ICollection<ProficiencyLevel> MapLevels(ICollection<ProficiencyLevelEntity> source) =>
        source == null ? [] : source.Select(x => x.ToProficiencyLevel()).ToList();

    private static ICollection<ProficiencyLevelEntity> MapLevels(ICollection<ProficiencyLevel> source) =>
        source == null ? [] : source.Select(x => x.ToEntity()).ToList();
}
