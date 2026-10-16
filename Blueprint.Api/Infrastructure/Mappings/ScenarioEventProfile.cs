// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using Blueprint.Api.Data.Models;
using Blueprint.Api.ViewModels;

namespace Blueprint.Api.Infrastructure.Mappings
{
    public class ScenarioEventProfile : AutoMapper.Profile
    {
        public ScenarioEventProfile()
        {
            CreateMap<ScenarioEventEntity, ScenarioEvent>();

            // The write maps copy exactly the request's scalar fields. The cells and the task are
            // written by ScenarioEventService for the stored event.
            CreateMap<ScenarioEventCreate, ScenarioEventEntity>(AutoMapper.MemberList.Source)
                .ForSourceMember(s => s.DataValues, opt => opt.DoNotValidate())
                .ForSourceMember(s => s.SteamfitterTask, opt => opt.DoNotValidate())
                .ForMember(e => e.DataValues, opt => opt.Ignore())
                .ForMember(e => e.SteamfitterTask, opt => opt.Ignore());
            CreateMap<ScenarioEventEdit, ScenarioEventEntity>(AutoMapper.MemberList.Source)
                .ForSourceMember(s => s.DataValues, opt => opt.DoNotValidate())
                .ForSourceMember(s => s.SteamfitterTask, opt => opt.DoNotValidate())
                .ForMember(e => e.DataValues, opt => opt.Ignore())
                .ForMember(e => e.SteamfitterTask, opt => opt.Ignore());

            CreateMap<ScenarioEventEntity, ScenarioEventEntity>()
                .ForMember(e => e.Id, opt => opt.Ignore());

        }
    }
}


