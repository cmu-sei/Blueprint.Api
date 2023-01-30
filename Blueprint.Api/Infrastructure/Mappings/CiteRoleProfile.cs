// Copyright 2023 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using Blueprint.Api.Data.Models;
using Blueprint.Api.ViewModels;

namespace Blueprint.Api.Infrastructure.Mappings
{
    public class CiteRoleProfile : AutoMapper.Profile
    {
        public CiteRoleProfile()
        {
            CreateMap<CiteRoleEntity, CiteRole>();

            CreateMap<CiteRole, CiteRoleEntity>()
                .ForMember(cr => cr.Msel, opt => opt.Ignore());

        }
    }
}


