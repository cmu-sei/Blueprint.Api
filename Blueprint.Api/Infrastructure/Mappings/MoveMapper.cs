// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System.Collections.Generic;
using System.Linq;
using Blueprint.Api.Data.Models;
using Blueprint.Api.ViewModels;
using Riok.Mapperly.Abstractions;

namespace Blueprint.Api.Infrastructure.Mappings;

/// <summary>Moves' mappings.</summary>
[Mapper]
public static partial class MoveMapper
{
    public static partial Move ToMove(this MoveEntity source);

    [MapperRequiredMapping(RequiredMappingStrategy.Source)]
    public static partial MoveEntity ToEntity(this MoveCreate source);

    [MapperRequiredMapping(RequiredMappingStrategy.Source)]
    public static partial void ApplyTo(this MoveEdit source, MoveEntity target);
}
