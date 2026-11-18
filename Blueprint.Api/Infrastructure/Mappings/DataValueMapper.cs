// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System.Collections.Generic;
using System.Linq;
using Blueprint.Api.Data.Models;
using Blueprint.Api.ViewModels;
using Riok.Mapperly.Abstractions;

namespace Blueprint.Api.Infrastructure.Mappings;

/// <summary>Data values' mappings.</summary>
[Mapper]
public static partial class DataValueMapper
{
    public static partial DataValue ToDataValue(this DataValueEntity source);

    /// <remarks>A value nested in an inject's body.</remarks>
    [MapperRequiredMapping(RequiredMappingStrategy.Source)]
    public static partial DataValueEntity ToEntity(this DataValue source);

    [MapperRequiredMapping(RequiredMappingStrategy.Source)]
    public static partial DataValueEntity ToEntity(this DataValueCreate source);

    [MapperRequiredMapping(RequiredMappingStrategy.Source)]
    public static partial void ApplyTo(this DataValueEdit source, DataValueEntity target);
}
