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
                .ForMember(m => m.Pages, opt => opt.ExplicitExpansion());

            CreateMap<Msel, MselEntity>()
                .ForMember(m => m.Cards, opt => opt.Ignore())
                .ForMember(m => m.DataFields, opt => opt.Ignore())
                .ForMember(m => m.Moves, opt => opt.Ignore())
                .ForMember(m => m.Organizations, opt => opt.Ignore())
                .ForMember(m => m.Pages, opt => opt.Ignore())
                .ForMember(m => m.ScenarioEvents, opt => opt.Ignore())
                .ForMember(m => m.Teams, opt => opt.Ignore())
                .ForMember(m => m.MselUnits, opt => opt.Ignore())
                .ForMember(m => m.UserMselRoles, opt => opt.Ignore());

            CreateMap<MselEntity, MselEntity>()
                .ForMember(e => e.Id, opt => opt.Ignore());

        }
    }
}
