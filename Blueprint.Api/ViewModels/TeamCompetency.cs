// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;

namespace Blueprint.Api.ViewModels
{
    public class TeamCompetency
    {
        public TeamCompetency() { }

        public TeamCompetency(Guid teamId, Guid competencyId)
        {
            TeamId = teamId;
            CompetencyId = competencyId;
        }

        public Guid Id { get; set; }
        public Guid TeamId { get; set; }
        public Guid CompetencyId { get; set; }
        public virtual Competency Competency { get; set; }
    }
}
