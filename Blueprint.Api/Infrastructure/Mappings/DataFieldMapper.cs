// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System.Collections.Generic;
using System.Linq;
using Blueprint.Api.Data.Models;
using Blueprint.Api.ViewModels;
using Riok.Mapperly.Abstractions;

namespace Blueprint.Api.Infrastructure.Mappings;

/// <summary>Data fields' mappings. The writes never map a field's options; the services reconcile them.</summary>
[Mapper]
[UseStaticMapper(typeof(DataOptionMapper))]
public static partial class DataFieldMapper
{
    public static partial DataField ToDataField(this DataFieldEntity source);

    /// <remarks>The JSON template upload and an inject type's upload.</remarks>
    [MapperRequiredMapping(RequiredMappingStrategy.Source)]
    [MapperIgnoreSource(nameof(DataField.DataOptions))]
    public static partial DataFieldEntity ToEntity(this DataField source);

    [MapperRequiredMapping(RequiredMappingStrategy.Source)]
    [MapperIgnoreSource(nameof(DataFieldCreate.DataOptions))]
    public static partial DataFieldEntity ToEntity(this DataFieldCreate source);

    [MapperRequiredMapping(RequiredMappingStrategy.Source)]
    [MapperIgnoreSource(nameof(DataFieldEdit.DataOptions))]
    public static partial void ApplyTo(this DataFieldEdit source, DataFieldEntity target);

    /// <remarks>A field sent with a new inject type.</remarks>
    [MapperRequiredMapping(RequiredMappingStrategy.Source)]
    [MapperIgnoreSource(nameof(InjectTypeDataField.DataOptions))]
    public static partial DataFieldEntity ToEntity(this InjectTypeDataField source);

    private static ICollection<DataOption> MapDataOptions(ICollection<DataOptionEntity> source) =>
        source == null ? [] : source.Select(x => x.ToDataOption()).ToList();
}
