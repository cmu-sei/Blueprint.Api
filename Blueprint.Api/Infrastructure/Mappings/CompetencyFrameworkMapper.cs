// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System.Collections.Generic;
using System.Linq;
using Blueprint.Api.Data.Models;
using Blueprint.Api.ViewModels;
using Riok.Mapperly.Abstractions;

namespace Blueprint.Api.Infrastructure.Mappings;

/// <summary>Competency frameworks' mappings.</summary>
[Mapper]
[UseStaticMapper(typeof(CompetencyMapper))]
public static partial class CompetencyFrameworkMapper
{
    /// <remarks>
    /// One competency mapping for the whole framework, so a competency listed and also reached as another's
    /// child is the same view model, as under AutoMapper: <c>CompetencyFrameworkService</c> fills each listed
    /// competency's related id numbers afterwards.
    /// </remarks>
    public static CompetencyFramework ToCompetencyFramework(this CompetencyFrameworkEntity source)
    {
        if (source == null)
            return null;

        var target = MapFrameworkFields(source);
        var mapped = CompetencyMapper.NewCache();
        target.Competencies = source.Competencies == null
            ? []
            : source.Competencies.Select(x => CompetencyMapper.ToCompetency(x, mapped)).ToList();

        return target;
    }

    [MapperIgnoreTarget(nameof(CompetencyFramework.Competencies))]
    private static partial CompetencyFramework MapFrameworkFields(CompetencyFrameworkEntity source);

    [MapperRequiredMapping(RequiredMappingStrategy.Source)]
    public static partial CompetencyFrameworkEntity ToEntity(this CompetencyFramework source);

    private static ICollection<CompetencyEntity> MapCompetencies(ICollection<Competency> source) =>
        source == null ? [] : source.Select(x => x.ToEntity()).ToList();
}
