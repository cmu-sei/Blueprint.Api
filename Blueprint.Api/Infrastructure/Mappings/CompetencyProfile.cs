// Copyright 2024 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System.Linq;
using Blueprint.Api.Data.Models;
using Blueprint.Api.ViewModels;

namespace Blueprint.Api.Infrastructure.Mappings
{
    public class CompetencyProfile : AutoMapper.Profile
    {
        public CompetencyProfile()
        {
            CreateMap<CompetencyEntity, Competency>()
                .ForMember(dest => dest.RelatedIdNumbers, opt => opt.Ignore());

            CreateMap<Competency, CompetencyEntity>()
                .ForMember(dest => dest.Relationships, opt => opt.Ignore())
                .ForMember(dest => dest.InverseRelationships, opt => opt.Ignore());
        }
    }
}
