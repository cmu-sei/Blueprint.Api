// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using Blueprint.Api.Data.Models;
using Blueprint.Api.ViewModels;

namespace Blueprint.Api.Infrastructure.Mappings
{
    public class DataOptionProfile : AutoMapper.Profile
    {
        public DataOptionProfile()
        {
            CreateMap<DataOptionEntity, DataOption>();

            // The write maps copy exactly the request's fields.
            CreateMap<DataOptionCreate, DataOptionEntity>(AutoMapper.MemberList.Source);
            CreateMap<DataOptionEdit, DataOptionEntity>(AutoMapper.MemberList.Source);
            // An option sent with its field: the service decides its id and its field.
            CreateMap<DataFieldOption, DataOptionEntity>(AutoMapper.MemberList.Source)
                .ForMember(x => x.Id, opt => opt.Ignore())
                .ForSourceMember(x => x.Id, opt => opt.DoNotValidate());

        }
    }
}


