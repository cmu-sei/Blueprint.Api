// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System.Collections.Generic;
using System.Linq;
using Blueprint.Api.Data.Models;
using Blueprint.Api.ViewModels;
using Riok.Mapperly.Abstractions;

namespace Blueprint.Api.Infrastructure.Mappings;

/// <summary>Cards' mappings. The writes copy exactly the request's fields.</summary>
[Mapper]
public static partial class CardMapper
{
    public static partial Card ToCard(this CardEntity source);

    /// <remarks>The JSON template upload.</remarks>
    [MapperRequiredMapping(RequiredMappingStrategy.Source)]
    public static partial CardEntity ToEntity(this Card source);

    [MapperRequiredMapping(RequiredMappingStrategy.Source)]
    public static partial CardEntity ToEntity(this CardCreate source);

    [MapperRequiredMapping(RequiredMappingStrategy.Source)]
    public static partial void ApplyTo(this CardEdit source, CardEntity target);
}
