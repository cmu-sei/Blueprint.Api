// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using Blueprint.Api.Data.Models;
using Blueprint.Api.ViewModels;

namespace Blueprint.Api.Infrastructure.Mappings
{
    public class MselProfile : AutoMapper.Profile
    {
        public MselProfile()
        {
            CreateMap<MselEntity, Msel>();

            CreateMap<Msel, MselEntity>();

            CreateMap<MselEntity, MselEntity>()
                .ForMember(e => e.Id, opt => opt.Ignore());

        }
    }
}


