// Copyright 2024 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using Blueprint.Api.Data.Models;
using Blueprint.Api.ViewModels;

namespace Blueprint.Api.Infrastructure.Mappings
{
    public class InjectTypeProfile : AutoMapper.Profile
    {
        public InjectTypeProfile()
        {
            CreateMap<InjectTypeEntity, InjectType>();

            CreateMap<InjectType, InjectTypeEntity>();

            // The write maps copy exactly the request's fields; a new inject type's fields are created by the service.
            CreateMap<InjectTypeCreate, InjectTypeEntity>(AutoMapper.MemberList.Source)
                .ForMember(x => x.DataFields, opt => opt.Ignore())
                .ForSourceMember(x => x.DataFields, opt => opt.DoNotValidate());
            CreateMap<InjectTypeEdit, InjectTypeEntity>(AutoMapper.MemberList.Source);
        }
    }
}
