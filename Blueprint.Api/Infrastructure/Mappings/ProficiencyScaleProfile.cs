// Copyright 2025 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using Blueprint.Api.Data.Models;
using Blueprint.Api.ViewModels;

namespace Blueprint.Api.Infrastructure.Mappings
{
    public class ProficiencyScaleProfile : AutoMapper.Profile
    {
        public ProficiencyScaleProfile()
        {
            CreateMap<ProficiencyScaleEntity, ProficiencyScale>();

            CreateMap<ProficiencyScale, ProficiencyScaleEntity>();
        }
    }
}
