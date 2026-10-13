// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using Blueprint.Api.Data.Models;
using Blueprint.Api.ViewModels;

namespace Blueprint.Api.Infrastructure.Mappings
{
    public class DataFieldProfile : AutoMapper.Profile
    {
        public DataFieldProfile()
        {
            CreateMap<DataFieldEntity, DataField>();

            CreateMap<DataField, DataFieldEntity>()
                .ForMember(df => df.DataOptions, opt => opt.Ignore())
                .ForMember(df => df.Msel, opt => opt.Ignore());

            // The write maps copy exactly the request's fields; the services reconcile the options.
            CreateMap<DataFieldCreate, DataFieldEntity>(AutoMapper.MemberList.Source)
                .ForMember(df => df.DataOptions, opt => opt.Ignore())
                .ForSourceMember(df => df.DataOptions, opt => opt.DoNotValidate());
            CreateMap<DataFieldEdit, DataFieldEntity>(AutoMapper.MemberList.Source)
                .ForMember(df => df.DataOptions, opt => opt.Ignore())
                .ForSourceMember(df => df.DataOptions, opt => opt.DoNotValidate());
            CreateMap<InjectTypeDataField, DataFieldEntity>(AutoMapper.MemberList.Source)
                .ForMember(df => df.DataOptions, opt => opt.Ignore())
                .ForSourceMember(df => df.DataOptions, opt => opt.DoNotValidate());

        }
    }
}


