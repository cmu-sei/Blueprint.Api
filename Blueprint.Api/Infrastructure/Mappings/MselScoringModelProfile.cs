// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System.Linq;
using Blueprint.Api.Data.Models;
using Blueprint.Api.ViewModels;

namespace Blueprint.Api.Infrastructure.Mappings
{
    public class MselScoringModelProfile : AutoMapper.Profile
    {
        public MselScoringModelProfile()
        {
            // a replace is reconciled by MselScoringModelService, so only the read maps are here
            CreateMap<MselScoringModelEntity, MselScoringModel>()
                .ForMember(m => m.Categories, opt => opt.MapFrom(x => x.Categories.OrderBy(c => c.DisplayOrder)));
            CreateMap<MselScoringCategoryEntity, MselScoringCategory>()
                .ForMember(c => c.Options, opt => opt.MapFrom(x => x.Options.OrderBy(o => o.DisplayOrder)));
            CreateMap<MselScoringOptionEntity, MselScoringOption>();
        }
    }
}
