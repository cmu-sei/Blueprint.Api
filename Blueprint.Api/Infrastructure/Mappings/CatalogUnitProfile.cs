// Copyright 2024 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using Blueprint.Api.Data.Models;
using Blueprint.Api.ViewModels;

namespace Blueprint.Api.Infrastructure.Mappings
{
    public class CatalogUnitProfile : AutoMapper.Profile
    {
        public CatalogUnitProfile()
        {
            CreateMap<CatalogUnitEntity, CatalogUnit>();

            CreateMap<CatalogUnit, CatalogUnitEntity>();
        }
    }
}
