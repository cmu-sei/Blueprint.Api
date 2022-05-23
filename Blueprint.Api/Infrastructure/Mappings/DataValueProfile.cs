// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using Blueprint.Api.Data.Models;
using Blueprint.Api.ViewModels;

namespace Blueprint.Api.Infrastructure.Mappings
{
    public class DataValueProfile : AutoMapper.Profile
    {
        public DataValueProfile()
        {
            CreateMap<DataValueEntity, DataValue>();

            CreateMap<DataValue, DataValueEntity>()
                .ForMember(x => x.DataField, opt => opt.Ignore());

        }
    }
}


