// Copyright 2024 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;

namespace Blueprint.Api.ViewModels
{
    public class MselUnit
    {
        public MselUnit() {
        }

        public MselUnit(Guid mselId, Guid unitId)
        {
            MselId = mselId;
            UnitId = unitId;
        }

        public Guid Id { get; set; }
        public Guid MselId { get; set; }
        public Guid UnitId { get; set; }
        public virtual Unit Unit { get; set; }
    }

}

