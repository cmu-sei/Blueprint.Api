// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System.Collections.Generic;
using System.Linq;
using Blueprint.Api.Data.Models;
using Blueprint.Api.ViewModels;
using Riok.Mapperly.Abstractions;

namespace Blueprint.Api.Infrastructure.Mappings;

/// <summary>Catalog injects' mappings.</summary>
[Mapper]
[UseStaticMapper(typeof(InjectMapper))]
public static partial class CatalogInjectMapper
{
    public static partial CatalogInject ToCatalogInject(this CatalogInjectEntity source);

    /// <remarks>An inject nested in the body is created with the row, with its data values.</remarks>
    [MapperRequiredMapping(RequiredMappingStrategy.Source)]
    public static partial CatalogInjectEntity ToEntity(this CatalogInject source);
}
