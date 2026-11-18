// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Linq;
using Blueprint.Api.Data.Models;
using Blueprint.Api.ViewModels;

namespace Blueprint.Api.Infrastructure.Mappings
{
    public class TechniqueProfile : AutoMapper.Profile
    {
        public TechniqueProfile()
        {
            CreateMap<TechniqueReleaseEntity, TechniqueRelease>()
                .ForMember(r => r.TacticCount, opt => opt.MapFrom(x => x.Tactics.Count))
                .ForMember(r => r.TechniqueCount, opt => opt.MapFrom(x => x.Techniques.Count))
                .ForMember(r => r.CountermeasureCount, opt => opt.MapFrom(x => x.Countermeasures.Count))
                .ForMember(r => r.TechniqueFrameworkName, opt => opt.MapFrom(x => x.TechniqueFramework.Name))
                // from the framework's settings, filled in after the query
                .ForMember(r => r.TacticOrder, opt => opt.Ignore())
                .ForMember(r => r.WeaknessUrlTemplate, opt => opt.Ignore())
                .ForMember(r => r.MappingRelation, opt => opt.Ignore())
                .ForMember(r => r.MappingsDownloadUrl, opt => opt.Ignore())
                .ForMember(r => r.MappingsDownloadHint, opt => opt.Ignore());

            CreateMap<TechniqueFrameworkEntity, TechniqueFramework>()
                .ForMember(f => f.ReleaseCount, opt => opt.MapFrom(x => x.Releases.Count));

            CreateMap<TechniqueTacticEntity, TechniqueTactic>();

            CreateMap<TechniqueEntity, Technique>()
                .ForMember(t => t.Domain, opt => opt.MapFrom(x => x.TechniqueRelease.Domain))
                .ForMember(t => t.IsDefensive, opt => opt.MapFrom(x => x.TechniqueRelease.IsDefensive))
                .ForMember(t => t.IsSubTechnique, opt => opt.MapFrom(x => x.ParentId != null))
                .ForMember(t => t.Tactics, opt => opt.MapFrom(x => SplitList(x.Tactics)))
                .ForMember(t => t.Platforms, opt => opt.MapFrom(x => SplitList(x.Platforms)))
                .ForMember(t => t.Weaknesses, opt => opt.MapFrom(x => SplitList(x.Weaknesses)))
                .ForMember(t => t.ReferenceLinks, opt => opt.MapFrom(x => SplitLinks(x.ReferenceLinks)));

            CreateMap<MselTechniqueEntity, MselTechnique>();

            CreateMap<TechniqueCountermeasureEntity, TechniqueCountermeasure>()
                .ForMember(c => c.TechniqueReleaseId, opt => opt.MapFrom(x => x.ReleaseId))
                .ForMember(c => c.InheritedFromParent, opt => opt.Ignore());
        }

        /// <summary>The "source|label|url" lines <c>StixBundleReader</c> stores a technique's links as.</summary>
        private static System.Collections.Generic.List<TechniqueLink> SplitLinks(string value) =>
            string.IsNullOrEmpty(value)
                ? []
                : value.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(line => line.Split('|'))
                    .Where(parts => parts.Length == 3)
                    .Select(parts => new TechniqueLink { Source = parts[0], Label = parts[1], Url = parts[2] })
                    .ToList();

        private static System.Collections.Generic.List<string> SplitList(string value) =>
            string.IsNullOrEmpty(value)
                ? []
                : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
    }
}
