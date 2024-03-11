// Copyright 2023 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;

namespace Blueprint.Api.ViewModels
{
    public class MselTeam
    {
        public MselTeam() {
        }

        public MselTeam(Guid mselId, Guid teamId)
        {
            MselId = mselId;
            TeamId = teamId;
        }

        public Guid Id { get; set; }
        public Guid MselId { get; set; }
        public Guid TeamId { get; set; }
        public virtual Team Team { get; set; }
        public Guid? CiteTeamTypeId { get; set; }
        public string Email { get; set; }
    }

}

