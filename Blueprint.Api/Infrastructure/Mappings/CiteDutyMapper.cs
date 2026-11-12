// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System.Collections.Generic;
using System.Linq;
using Blueprint.Api.Data.Models;
using Blueprint.Api.ViewModels;
using Riok.Mapperly.Abstractions;

namespace Blueprint.Api.Infrastructure.Mappings;

/// <summary>CITE duties' mappings.</summary>
[Mapper]
[UseStaticMapper(typeof(TeamMapper))]
public static partial class CiteDutyMapper
{
    public static partial CiteDuty ToCiteDuty(this CiteDutyEntity source);

    /// <remarks>The JSON template upload. The team is never written from the file.</remarks>
    [MapperRequiredMapping(RequiredMappingStrategy.Source)]
    [MapperIgnoreSource(nameof(CiteDuty.Team))]
    public static partial CiteDutyEntity ToEntity(this CiteDuty source);

    [MapperRequiredMapping(RequiredMappingStrategy.Source)]
    public static partial CiteDutyEntity ToEntity(this CiteDutyCreate source);

    [MapperRequiredMapping(RequiredMappingStrategy.Source)]
    public static partial void ApplyTo(this CiteDutyEdit source, CiteDutyEntity target);
}
