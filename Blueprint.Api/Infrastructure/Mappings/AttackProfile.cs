// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Linq;
using Blueprint.Api.Data.Models;
using Blueprint.Api.ViewModels;

namespace Blueprint.Api.Infrastructure.Mappings
{
    public class AttackProfile : AutoMapper.Profile
    {
        public AttackProfile()
        {
            CreateMap<AttackReleaseEntity, AttackRelease>()
                .ForMember(r => r.TacticCount, opt => opt.MapFrom(x => x.Tactics.Count))
                .ForMember(r => r.TechniqueCount, opt => opt.MapFrom(x => x.Techniques.Count))
                .ForMember(r => r.CountermeasureCount, opt => opt.MapFrom(x => x.Countermeasures.Count));

            CreateMap<AttackTacticEntity, AttackTactic>();

            CreateMap<AttackTechniqueEntity, AttackTechnique>()
                .ForMember(t => t.Domain, opt => opt.MapFrom(x => x.AttackRelease.Domain))
                .ForMember(t => t.IsDefensive, opt => opt.MapFrom(x => x.AttackRelease.IsDefensive))
                .ForMember(t => t.IsSubTechnique, opt => opt.MapFrom(x => x.ParentId != null))
                .ForMember(t => t.Tactics, opt => opt.MapFrom(x => SplitList(x.Tactics)))
                .ForMember(t => t.Platforms, opt => opt.MapFrom(x => SplitList(x.Platforms)));

            CreateMap<MselAttackTechniqueEntity, MselAttackTechnique>();

            CreateMap<AttackTechniqueCountermeasureEntity, AttackTechniqueCountermeasure>()
                .ForMember(c => c.AttackReleaseId, opt => opt.MapFrom(x => x.ReleaseId))
                .ForMember(c => c.InheritedFromParent, opt => opt.Ignore());
        }

        private static System.Collections.Generic.List<string> SplitList(string value) =>
            string.IsNullOrEmpty(value)
                ? []
                : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
    }
}
