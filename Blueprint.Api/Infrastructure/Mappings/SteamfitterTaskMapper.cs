// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System.Collections.Generic;
using System.Linq;
using Blueprint.Api.Data.Models;
using Blueprint.Api.ViewModels;
using Riok.Mapperly.Abstractions;

namespace Blueprint.Api.Infrastructure.Mappings;

/// <summary>Steamfitter tasks' mappings. Missing action parameters are an empty dictionary in both directions, never null.</summary>
[Mapper]
public static partial class SteamfitterTaskMapper
{
    /// <remarks><see cref="SteamfitterTask.ScenarioEvent"/> is left null.</remarks>
    [MapperIgnoreTarget(nameof(SteamfitterTask.ScenarioEvent))]
    public static partial SteamfitterTask ToSteamfitterTask(this SteamfitterTaskEntity source);

    [MapperRequiredMapping(RequiredMappingStrategy.Source)]
    public static partial SteamfitterTaskEntity ToEntity(this SteamfitterTaskFields source);

    [MapperRequiredMapping(RequiredMappingStrategy.Source)]
    public static partial void ApplyTo(this SteamfitterTaskFields source, SteamfitterTaskEntity target);

    // A copy, as AutoMapper made one: the entity never shares the request's dictionary, or the reverse.
    private static Dictionary<string, string> MapActionParameters(Dictionary<string, string> source) =>
        source == null ? [] : new Dictionary<string, string>(source);
}
