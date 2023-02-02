// Copyright 2023 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using Blueprint.Api.Data.Models;
using Blueprint.Api.ViewModels;

namespace Blueprint.Api.Infrastructure.Mappings
{
    public class CiteActionProfile : AutoMapper.Profile
    {
        public CiteActionProfile()
        {
            CreateMap<CiteActionEntity, CiteAction>();

            CreateMap<CiteAction, CiteActionEntity>()
                .ForMember(ca => ca.Msel, opt => opt.Ignore())
                .ForMember(ca => ca.Team, Options => Options.Ignore());

        }
    }
}


