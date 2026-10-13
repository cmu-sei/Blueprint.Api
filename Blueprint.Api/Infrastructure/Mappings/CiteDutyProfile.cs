// Copyright 2023 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using Blueprint.Api.Data.Models;
using Blueprint.Api.ViewModels;

namespace Blueprint.Api.Infrastructure.Mappings
{
    public class CiteDutyProfile : AutoMapper.Profile
    {
        public CiteDutyProfile()
        {
            CreateMap<CiteDutyEntity, CiteDuty>();

            // the JSON template upload
            CreateMap<CiteDuty, CiteDutyEntity>()
                .ForMember(cr => cr.Msel, opt => opt.Ignore())
                .ForMember(cr => cr.Team, opt => opt.Ignore());
            CreateMap<CiteDutyCreate, CiteDutyEntity>(AutoMapper.MemberList.Source);
            CreateMap<CiteDutyEdit, CiteDutyEntity>(AutoMapper.MemberList.Source);

        }
    }
}
