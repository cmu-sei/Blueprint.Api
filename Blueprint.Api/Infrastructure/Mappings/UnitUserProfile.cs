// Copyright 2024 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using Blueprint.Api.Data.Models;
using Blueprint.Api.ViewModels;

namespace Blueprint.Api.Infrastructure.Mappings
{
    public class UnitUserProfile : AutoMapper.Profile
    {
        public UnitUserProfile()
        {
            CreateMap<UnitUserEntity, UnitUser>();

            CreateMap<UnitUser, UnitUserEntity>();
        }
    }
}


