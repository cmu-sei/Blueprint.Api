// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System.Collections.Generic;
using System.Linq;
using Blueprint.Api.Data.Models;
using Blueprint.Api.ViewModels;
using Riok.Mapperly.Abstractions;

namespace Blueprint.Api.Infrastructure.Mappings;

/// <summary>Catalog units' mappings.</summary>
[Mapper]
[UseStaticMapper(typeof(UnitMapper))]
public static partial class CatalogUnitMapper
{
    public static partial CatalogUnit ToCatalogUnit(this CatalogUnitEntity source);

    /// <remarks>A unit nested in the body is created with the row.</remarks>
    [MapperRequiredMapping(RequiredMappingStrategy.Source)]
    public static partial CatalogUnitEntity ToEntity(this CatalogUnit source);

    /// <remarks>
    /// Writes the body's id onto the row as well, so a body whose id is not the route's (or has none) tries
    /// to change the key and fails the save.
    /// </remarks>
    [MapperRequiredMapping(RequiredMappingStrategy.Source)]
    public static partial void ApplyTo(this CatalogUnit source, CatalogUnitEntity target);
}
