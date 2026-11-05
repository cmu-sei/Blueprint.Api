// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System.Collections.Generic;
using System.Linq;
using Blueprint.Api.Data.Models;
using Blueprint.Api.ViewModels;
using Riok.Mapperly.Abstractions;

namespace Blueprint.Api.Infrastructure.Mappings;

/// <summary>Injects' mappings.</summary>
[Mapper]
[UseStaticMapper(typeof(DataValueMapper))]
public static partial class InjectMapper
{
    public static partial Injectm ToInjectm(this InjectEntity source);

    /// <remarks>The create, and an inject nested in a catalog inject's body. Its data values are created with it.</remarks>
    [MapperRequiredMapping(RequiredMappingStrategy.Source)]
    public static partial InjectEntity ToEntity(this Injectm source);

    /// <remarks>
    /// Writes the body's id onto the row as well, so a body whose id is not the route's (or has none) tries
    /// to change the key and fails the save. The body's data values replace the unloaded collection, so the
    /// update writes each over the stored value with its id.
    /// </remarks>
    [MapperRequiredMapping(RequiredMappingStrategy.Source)]
    public static partial void ApplyTo(this Injectm source, InjectEntity target);

    private static ICollection<DataValue> MapDataValues(ICollection<DataValueEntity> source) =>
        source == null ? [] : source.Select(x => x.ToDataValue()).ToList();

    private static ICollection<DataValueEntity> MapDataValues(ICollection<DataValue> source) =>
        source == null ? [] : source.Select(x => x.ToEntity()).ToList();
}
