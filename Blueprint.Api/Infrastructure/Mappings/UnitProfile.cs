// Copyright 2024 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using Blueprint.Api.Data.Models;
using Blueprint.Api.ViewModels;
using System.Linq;

namespace Blueprint.Api.Infrastructure.Mappings
{
    public class UnitProfile : AutoMapper.Profile
    {
        public UnitProfile()
        {
            CreateMap<UnitEntity, Unit>()
                .ForMember(m => m.Users, opt => opt.MapFrom(x => x.UnitUsers.Select(y => y.User)))
                .ForMember(m => m.Users, opt => opt.ExplicitExpansion());

            CreateMap<Unit, UnitEntity>()
                .ForMember(m => m.UnitUsers, opt => opt.Ignore())
                .ForMember(m => m.MselUnits, opt => opt.Ignore());
        }
    }
}


