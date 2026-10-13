// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using Blueprint.Api.Data.Models;
using Blueprint.Api.ViewModels;

namespace Blueprint.Api.Infrastructure.Mappings
{
    public class PlayerApplicationProfile : AutoMapper.Profile
    {
        public PlayerApplicationProfile()
        {
            CreateMap<PlayerApplicationEntity, PlayerApplication>();

            CreateMap<PlayerApplicationCreate, PlayerApplicationEntity>(AutoMapper.MemberList.Source);
            CreateMap<PlayerApplicationEdit, PlayerApplicationEntity>(AutoMapper.MemberList.Source);
        }
    }
}


