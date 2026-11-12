// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System.Collections.Generic;
using System.Linq;
using Blueprint.Api.Data.Models;
using Blueprint.Api.ViewModels;
using Riok.Mapperly.Abstractions;

namespace Blueprint.Api.Infrastructure.Mappings;

/// <summary>Data options' mappings. The writes copy exactly the request's fields.</summary>
[Mapper]
public static partial class DataOptionMapper
{
    public static partial DataOption ToDataOption(this DataOptionEntity source);

    [MapperRequiredMapping(RequiredMappingStrategy.Source)]
    public static partial DataOptionEntity ToEntity(this DataOptionCreate source);

    [MapperRequiredMapping(RequiredMappingStrategy.Source)]
    public static partial void ApplyTo(this DataOptionEdit source, DataOptionEntity target);

    /// <remarks>An option sent with its field: the service decides its id and its field.</remarks>
    [MapperRequiredMapping(RequiredMappingStrategy.Source)]
    [MapperIgnoreSource(nameof(DataFieldOption.Id))]
    public static partial DataOptionEntity ToEntity(this DataFieldOption source);

    /// <remarks>An option sent with its field, onto the stored option with its id.</remarks>
    [MapperRequiredMapping(RequiredMappingStrategy.Source)]
    [MapperIgnoreSource(nameof(DataFieldOption.Id))]
    public static partial void ApplyTo(this DataFieldOption source, DataOptionEntity target);
}
