// Copyright 2023 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using Blueprint.Api.Data.Models;
using Blueprint.Api.ViewModels;

namespace Blueprint.Api.Infrastructure.Mappings
{
    public class MselTeamProfile : AutoMapper.Profile
    {
        public MselTeamProfile()
        {
            CreateMap<MselTeamEntity, MselTeam>();

            CreateMap<MselTeam, MselTeamEntity>();
        }
    }
}


