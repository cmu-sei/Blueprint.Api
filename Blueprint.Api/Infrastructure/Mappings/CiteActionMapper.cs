// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System.Collections.Generic;
using System.Linq;
using Blueprint.Api.Data.Models;
using Blueprint.Api.ViewModels;
using Riok.Mapperly.Abstractions;

namespace Blueprint.Api.Infrastructure.Mappings;

/// <summary>CITE actions' mappings.</summary>
[Mapper]
[UseStaticMapper(typeof(TeamMapper))]
public static partial class CiteActionMapper
{
    public static partial CiteAction ToCiteAction(this CiteActionEntity source);

    /// <remarks>The JSON template upload. The team is never written from the file.</remarks>
    [MapperRequiredMapping(RequiredMappingStrategy.Source)]
    [MapperIgnoreSource(nameof(CiteAction.Team))]
    public static partial CiteActionEntity ToEntity(this CiteAction source);

    [MapperRequiredMapping(RequiredMappingStrategy.Source)]
    public static partial CiteActionEntity ToEntity(this CiteActionCreate source);

    [MapperRequiredMapping(RequiredMappingStrategy.Source)]
    public static partial void ApplyTo(this CiteActionEdit source, CiteActionEntity target);
}
