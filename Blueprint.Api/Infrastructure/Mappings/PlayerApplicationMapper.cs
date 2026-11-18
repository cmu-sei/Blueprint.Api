// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System.Collections.Generic;
using System.Linq;
using Blueprint.Api.Data.Models;
using Blueprint.Api.ViewModels;
using Riok.Mapperly.Abstractions;

namespace Blueprint.Api.Infrastructure.Mappings;

/// <summary>Player applications' mappings.</summary>
[Mapper]
public static partial class PlayerApplicationMapper
{
    public static partial PlayerApplication ToPlayerApplication(this PlayerApplicationEntity source);

    /// <remarks>The JSON template upload.</remarks>
    [MapperRequiredMapping(RequiredMappingStrategy.Source)]
    public static partial PlayerApplicationEntity ToEntity(this PlayerApplication source);

    [MapperRequiredMapping(RequiredMappingStrategy.Source)]
    public static partial PlayerApplicationEntity ToEntity(this PlayerApplicationCreate source);

    [MapperRequiredMapping(RequiredMappingStrategy.Source)]
    public static partial void ApplyTo(this PlayerApplicationEdit source, PlayerApplicationEntity target);
}
