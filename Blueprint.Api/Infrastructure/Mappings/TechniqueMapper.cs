// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Blueprint.Api.Data.Models;
using Blueprint.Api.ViewModels;
using Riok.Mapperly.Abstractions;

namespace Blueprint.Api.Infrastructure.Mappings;

/// <summary>Technique frameworks', releases', techniques' and MSEL techniques' mappings.</summary>
[Mapper]
public static partial class TechniqueMapper
{
    /// <remarks>
    /// Read in the query, with the release's counts and its framework's name. What the framework's settings say
    /// (tactic order, weakness links, mappings) is filled in by <c>TechniqueReleaseService</c> after the query.
    /// </remarks>
    [MapPropertyFromSource(nameof(TechniqueRelease.TacticCount), Use = nameof(CountTactics))]
    [MapPropertyFromSource(nameof(TechniqueRelease.TechniqueCount), Use = nameof(CountTechniques))]
    [MapPropertyFromSource(nameof(TechniqueRelease.CountermeasureCount), Use = nameof(CountCountermeasures))]
    [MapProperty("TechniqueFramework.Name", nameof(TechniqueRelease.TechniqueFrameworkName))]
    [MapperIgnoreTarget(nameof(TechniqueRelease.TacticOrder))]
    [MapperIgnoreTarget(nameof(TechniqueRelease.WeaknessUrlTemplate))]
    [MapperIgnoreTarget(nameof(TechniqueRelease.MappingRelation))]
    [MapperIgnoreTarget(nameof(TechniqueRelease.MappingsDownloadUrl))]
    [MapperIgnoreTarget(nameof(TechniqueRelease.MappingsDownloadHint))]
    private static partial TechniqueRelease MapTechniqueRelease(TechniqueReleaseEntity source);

    public static partial IQueryable<TechniqueRelease> ProjectToTechniqueRelease(this IQueryable<TechniqueReleaseEntity> query);

    /// <remarks>Read in the query, with how many releases were read as the framework.</remarks>
    [MapPropertyFromSource(nameof(TechniqueFramework.ReleaseCount), Use = nameof(CountReleases))]
    private static partial TechniqueFramework MapTechniqueFramework(TechniqueFrameworkEntity source);

    public static partial IQueryable<TechniqueFramework> ProjectToTechniqueFramework(this IQueryable<TechniqueFrameworkEntity> query);

    // inlined into the projections as plain counts: a null check on a collection would make the query load it
    private static int CountTactics(TechniqueReleaseEntity source) => source.Tactics.Count;
    private static int CountTechniques(TechniqueReleaseEntity source) => source.Techniques.Count;
    private static int CountCountermeasures(TechniqueReleaseEntity source) => source.Countermeasures.Count;
    private static int CountReleases(TechniqueFrameworkEntity source) => source.Releases.Count;

    public static partial TechniqueTactic ToTechniqueTactic(this TechniqueTacticEntity source);

    /// <remarks>
    /// The domain and family are the release's, so a technique whose release is not loaded has none. The lists
    /// are stored as text: comma-separated, and links as a JSON array.
    /// </remarks>
    [UserMapping(Default = true)]
    public static Technique ToTechnique(this TechniqueEntity source)
    {
        if (source == null)
            return null;

        var target = MapTechniqueFields(source);
        target.Domain = source.TechniqueRelease?.Domain;
        target.IsDefensive = source.TechniqueRelease?.IsDefensive ?? false;
        target.IsSubTechnique = source.ParentId != null;
        target.Tactics = SplitList(source.Tactics);
        target.Platforms = SplitList(source.Platforms);
        target.Weaknesses = SplitList(source.Weaknesses);
        target.ReferenceLinks = ReadLinks(source.ReferenceLinks);
        return target;
    }

    [MapperIgnoreTarget(nameof(Technique.Domain))]
    [MapperIgnoreTarget(nameof(Technique.IsDefensive))]
    [MapperIgnoreTarget(nameof(Technique.IsSubTechnique))]
    [MapperIgnoreTarget(nameof(Technique.Tactics))]
    [MapperIgnoreTarget(nameof(Technique.Platforms))]
    [MapperIgnoreTarget(nameof(Technique.Weaknesses))]
    [MapperIgnoreTarget(nameof(Technique.ReferenceLinks))]
    [UserMapping(Default = false)]
    private static partial Technique MapTechniqueFields(TechniqueEntity source);

    /// <remarks>The technique is mapped by <see cref="ToTechnique"/>.</remarks>
    public static partial MselTechnique ToMselTechnique(this MselTechniqueEntity source);

    /// <remarks><see cref="TechniqueCountermeasure.InheritedFromParent"/> is set by <c>TechniqueCountermeasureService</c>.</remarks>
    [MapProperty(nameof(TechniqueCountermeasureEntity.ReleaseId), nameof(TechniqueCountermeasure.TechniqueReleaseId))]
    [MapperIgnoreTarget(nameof(TechniqueCountermeasure.InheritedFromParent))]
    public static partial TechniqueCountermeasure ToTechniqueCountermeasure(this TechniqueCountermeasureEntity source);

    /// <summary>The JSON array <c>StixBundleReader</c> stores a technique's links as.</summary>
    private static List<TechniqueLink> ReadLinks(string value) =>
        string.IsNullOrEmpty(value) ? [] : JsonSerializer.Deserialize<List<TechniqueLink>>(value, JsonSerializerOptions.Web) ?? [];

    private static List<string> SplitList(string value) =>
        string.IsNullOrEmpty(value)
            ? []
            : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
}
