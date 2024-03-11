// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using Blueprint.Api.Data.Models;
using Blueprint.Api.ViewModels;
using System.Linq;

namespace Blueprint.Api.Infrastructure.Mappings
{
    public class TeamProfile : AutoMapper.Profile
    {
        public TeamProfile()
        {
            CreateMap<TeamEntity, Team>()
                .ForMember(m => m.Users, opt => opt.MapFrom(x => x.TeamUsers.Select(y => y.User)))
                .ForMember(m => m.Users, opt => opt.ExplicitExpansion());

            CreateMap<Team, TeamEntity>()
                .ForMember(m => m.TeamUsers, opt => opt.Ignore());
        }
    }
}


