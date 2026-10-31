// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using Blueprint.Api.Data.Models;
using Blueprint.Api.ViewModels;
using System.Linq;

namespace Blueprint.Api.Infrastructure.Mappings
{
    public class MselProfile : AutoMapper.Profile
    {
        public MselProfile()
        {
            CreateMap<MselEntity, Msel>()
                .ForMember(m => m.Units, opt => opt.MapFrom(x => x.MselUnits.Select(y => y.Unit)))
                .ForMember(m => m.HasScoringModel, opt => opt.MapFrom(x => x.ScoringModel != null))
                .ForMember(m => m.Pages, opt => opt.ExplicitExpansion());

            // The write maps copy exactly the request's fields, so they are validated from the source
            // side: everything else on the entity is left as it is.
            CreateMap<MselCreate, MselEntity>(AutoMapper.MemberList.Source);
            CreateMap<MselEdit, MselEntity>(AutoMapper.MemberList.Source);

            CreateMap<MselEntity, MselEntity>()
                .ForMember(e => e.Id, opt => opt.Ignore());

        }
    }
}
