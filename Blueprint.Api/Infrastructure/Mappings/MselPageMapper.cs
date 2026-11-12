// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System.Collections.Generic;
using System.Linq;
using Blueprint.Api.Data.Models;
using Blueprint.Api.ViewModels;
using Riok.Mapperly.Abstractions;

namespace Blueprint.Api.Infrastructure.Mappings;

/// <summary>MSEL pages' mappings.</summary>
[Mapper]
public static partial class MselPageMapper
{
    public static partial MselPage ToMselPage(this MselPageEntity source);

    [MapperRequiredMapping(RequiredMappingStrategy.Source)]
    public static partial MselPageEntity ToEntity(this MselPageCreate source);

    [MapperRequiredMapping(RequiredMappingStrategy.Source)]
    public static partial void ApplyTo(this MselPageEdit source, MselPageEntity target);
}
