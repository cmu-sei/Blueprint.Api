// Copyright 2024 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using Blueprint.Api.Data.Models;
using Blueprint.Api.ViewModels;

namespace Blueprint.Api.Infrastructure.Mappings
{
    public class UserTeamRoleProfile : AutoMapper.Profile
    {
        public UserTeamRoleProfile()
        {
            CreateMap<UserTeamRoleEntity, UserTeamRole>();

            CreateMap<UserTeamRole, UserTeamRoleEntity>();
        }
    }
}


