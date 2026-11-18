// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System.Collections.Generic;
using System.Linq;
using Blueprint.Api.Data.Models;
using Blueprint.Api.ViewModels;
using Riok.Mapperly.Abstractions;

namespace Blueprint.Api.Infrastructure.Mappings;

/// <summary>Inject types' mappings.</summary>
[Mapper]
[UseStaticMapper(typeof(DataFieldMapper))]
public static partial class InjectTypeMapper
{
    public static partial InjectType ToInjectType(this InjectTypeEntity source);

    /// <remarks>The JSON template upload. Fields left on the body are created with the type, without their options.</remarks>
    [MapperRequiredMapping(RequiredMappingStrategy.Source)]
    public static partial InjectTypeEntity ToEntity(this InjectType source);

    /// <remarks>A new inject type's fields are created by the service.</remarks>
    [MapperRequiredMapping(RequiredMappingStrategy.Source)]
    [MapperIgnoreSource(nameof(InjectTypeCreate.DataFields))]
    public static partial InjectTypeEntity ToEntity(this InjectTypeCreate source);

    [MapperRequiredMapping(RequiredMappingStrategy.Source)]
    public static partial void ApplyTo(this InjectTypeEdit source, InjectTypeEntity target);

    private static ICollection<DataField> MapDataFields(ICollection<DataFieldEntity> source) =>
        source == null ? [] : source.Select(x => x.ToDataField()).ToList();

    private static ICollection<DataFieldEntity> MapDataFields(ICollection<DataField> source) =>
        source == null ? [] : source.Select(x => x.ToEntity()).ToList();
}
