// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System.Collections.Generic;
using System.Linq;
using Blueprint.Api.Data.Models;
using Blueprint.Api.ViewModels;
using Riok.Mapperly.Abstractions;

namespace Blueprint.Api.Infrastructure.Mappings;

/// <summary>Competencies' mappings. A competency's related id numbers are filled by the service, never mapped.</summary>
[Mapper]
public static partial class CompetencyMapper
{
    /// <remarks>
    /// <see cref="Competency.Children"/> holds whatever children the context has loaded. A competency
    /// reached twice is mapped once: see <see cref="ToCompetency(CompetencyEntity, Dictionary{CompetencyEntity, Competency})"/>.
    /// </remarks>
    public static Competency ToCompetency(this CompetencyEntity source) => ToCompetency(source, NewCache());

    /// <summary>The competencies one mapping has made so far, by the entity each was made from.</summary>
    internal static Dictionary<CompetencyEntity, Competency> NewCache() => new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// A competency and its children, each mapped once per <paramref name="mapped"/>: a competency reached
    /// again (one that is its own parent, or a parent cycle, both of which an update or an import can store)
    /// is the view model already made for it, as AutoMapper's inferred reference preservation answered for
    /// this self-referencing map. Without it the mapping would recurse until the stack overflows.
    /// </summary>
    /// <remarks>
    /// Hand-written because Mapperly's reference handling needs <c>Riok.Mapperly.Abstractions</c> at
    /// runtime, which the package reference leaves out.
    /// </remarks>
    internal static Competency ToCompetency(CompetencyEntity source, Dictionary<CompetencyEntity, Competency> mapped)
    {
        if (source == null)
            return null;
        if (mapped.TryGetValue(source, out var existing))
            return existing;

        var target = MapCompetencyFields(source);
        mapped.Add(source, target);
        target.Children = source.Children == null ? [] : source.Children.Select(x => ToCompetency(x, mapped)).ToList();

        return target;
    }

    [MapperIgnoreTarget(nameof(Competency.Children))]
    [MapperIgnoreTarget(nameof(Competency.RelatedIdNumbers))]
    private static partial Competency MapCompetencyFields(CompetencyEntity source);

    /// <remarks>
    /// Children nested in the body are created with the competency. A request body cannot reach one object
    /// twice, so this direction needs no reference preservation.
    /// </remarks>
    [MapperRequiredMapping(RequiredMappingStrategy.Source)]
    [MapperIgnoreSource(nameof(Competency.RelatedIdNumbers))]
    public static partial CompetencyEntity ToEntity(this Competency source);

    private static ICollection<CompetencyEntity> MapChildren(ICollection<Competency> source) =>
        source == null ? [] : source.Select(x => x.ToEntity()).ToList();
}
