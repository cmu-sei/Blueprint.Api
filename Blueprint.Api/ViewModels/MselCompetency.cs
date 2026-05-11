// Copyright 2024 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;

namespace Blueprint.Api.ViewModels
{
    public class MselCompetency
    {
        public MselCompetency() { }

        public MselCompetency(Guid mselId, Guid competencyId)
        {
            MselId = mselId;
            CompetencyId = competencyId;
        }

        public Guid Id { get; set; }
        public Guid MselId { get; set; }
        public Guid CompetencyId { get; set; }
        public virtual Competency Competency { get; set; }
    }
}
