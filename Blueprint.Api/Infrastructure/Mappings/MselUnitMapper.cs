// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System.Collections.Generic;
using System.Linq;
using Blueprint.Api.Data.Models;
using Blueprint.Api.ViewModels;
using Riok.Mapperly.Abstractions;

namespace Blueprint.Api.Infrastructure.Mappings;

/// <summary>MSEL units' mappings. The edit request carries no fields, so it maps nothing.</summary>
[Mapper]
[UseStaticMapper(typeof(UnitMapper))]
public static partial class MselUnitMapper
{
    public static partial MselUnit ToMselUnit(this MselUnitEntity source);

    [MapperRequiredMapping(RequiredMappingStrategy.Source)]
    public static partial MselUnitEntity ToEntity(this MselUnitCreate source);
}
