// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System.Collections.Generic;
using System.Linq;
using Blueprint.Api.Data.Models;
using Blueprint.Api.ViewModels;
using Riok.Mapperly.Abstractions;

namespace Blueprint.Api.Infrastructure.Mappings;

/// <summary>Catalogs' mappings. A catalog's units are never mapped in either direction.</summary>
[Mapper]
public static partial class CatalogMapper
{
    /// <remarks>
    /// <see cref="Catalog.Parent"/> is whatever parent the context has loaded. A catalog reached again up the
    /// parent chain (one that is its own parent, or a parent cycle) is the view model already made for it, as
    /// AutoMapper's inferred reference preservation answered for this self-referencing map; without it the
    /// mapping would recurse until the stack overflows. Hand-written because Mapperly's reference handling
    /// needs <c>Riok.Mapperly.Abstractions</c> at runtime, which the package reference leaves out.
    /// </remarks>
    public static Catalog ToCatalog(this CatalogEntity source) =>
        ToCatalog(source, new Dictionary<CatalogEntity, Catalog>(ReferenceEqualityComparer.Instance));

    private static Catalog ToCatalog(CatalogEntity source, Dictionary<CatalogEntity, Catalog> mapped)
    {
        if (source == null)
            return null;
        if (mapped.TryGetValue(source, out var existing))
            return existing;

        var target = MapCatalogFields(source);
        mapped.Add(source, target);
        target.Parent = ToCatalog(source.Parent, mapped);

        return target;
    }

    [MapperIgnoreTarget(nameof(Catalog.Parent))]
    [MapperIgnoreTarget(nameof(Catalog.Units))]
    private static partial Catalog MapCatalogFields(CatalogEntity source);

    /// <remarks>
    /// A parent nested in the body is created with the catalog. A request body cannot reach one object twice,
    /// so this direction needs no reference preservation.
    /// </remarks>
    [MapperRequiredMapping(RequiredMappingStrategy.Source)]
    [MapperIgnoreSource(nameof(Catalog.Units))]
    public static partial CatalogEntity ToEntity(this Catalog source);

    /// <remarks>
    /// Writes the body's id onto the row as well, so a body whose id is not the route's (or has none) tries
    /// to change the key and fails the save. A parent nested in the body is a new object, which the update
    /// writes over the stored parent with that id.
    /// </remarks>
    [MapperRequiredMapping(RequiredMappingStrategy.Source)]
    [MapperIgnoreSource(nameof(Catalog.Units))]
    public static partial void ApplyTo(this Catalog source, CatalogEntity target);
}
