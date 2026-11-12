// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System.Collections.Generic;
using System.Linq;
using Blueprint.Api.Data.Models;
using Blueprint.Api.ViewModels;
using Riok.Mapperly.Abstractions;

namespace Blueprint.Api.Infrastructure.Mappings;

/// <summary>Scenario events' mappings. The writes copy exactly the request's scalar fields: the cells and the task are written by ScenarioEventService for the stored event.</summary>
[Mapper]
[UseStaticMapper(typeof(DataValueMapper))]
[UseStaticMapper(typeof(SteamfitterTaskMapper))]
public static partial class ScenarioEventMapper
{
    public static partial ScenarioEvent ToScenarioEvent(this ScenarioEventEntity source);

    [MapperRequiredMapping(RequiredMappingStrategy.Source)]
    [MapperIgnoreSource(nameof(ScenarioEventCreate.DataValues))]
    [MapperIgnoreSource(nameof(ScenarioEventCreate.SteamfitterTask))]
    public static partial ScenarioEventEntity ToEntity(this ScenarioEventCreate source);

    [MapperRequiredMapping(RequiredMappingStrategy.Source)]
    [MapperIgnoreSource(nameof(ScenarioEventEdit.DataValues))]
    [MapperIgnoreSource(nameof(ScenarioEventEdit.SteamfitterTask))]
    public static partial void ApplyTo(this ScenarioEventEdit source, ScenarioEventEntity target);

    private static ICollection<DataValue> MapDataValues(ICollection<DataValueEntity> source) =>
        source == null ? [] : source.Select(x => x.ToDataValue()).ToList();
}
