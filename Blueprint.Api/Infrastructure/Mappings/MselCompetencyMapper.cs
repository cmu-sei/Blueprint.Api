// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System.Collections.Generic;
using System.Linq;
using Blueprint.Api.Data.Models;
using Blueprint.Api.ViewModels;
using Riok.Mapperly.Abstractions;

namespace Blueprint.Api.Infrastructure.Mappings;

/// <summary>MSEL competencies' mappings.</summary>
[Mapper]
[UseStaticMapper(typeof(CompetencyMapper))]
public static partial class MselCompetencyMapper
{
    public static partial MselCompetency ToMselCompetency(this MselCompetencyEntity source);

    /// <remarks>A competency nested in the body is created with the row, which is then put on it.</remarks>
    [MapperRequiredMapping(RequiredMappingStrategy.Source)]
    public static partial MselCompetencyEntity ToEntity(this MselCompetency source);
}
