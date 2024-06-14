// Copyright 2024 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;

namespace Blueprint.Api.ViewModels
{
    public class CatalogUnit
    {
        public CatalogUnit() {
        }

        public CatalogUnit(Guid catalogId, Guid unitId)
        {
            CatalogId = catalogId;
            UnitId = unitId;
        }

        public Guid Id { get; set; }
        public Guid CatalogId { get; set; }
        public Guid UnitId { get; set; }
        public virtual Unit Unit { get; set; }
    }

}

