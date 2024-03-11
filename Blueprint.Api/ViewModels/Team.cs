// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using System;
using System.Collections.Generic;

namespace Blueprint.Api.ViewModels
{
    public class Team : Base
    {
        public Guid Id { get; set; }
        public string Name { get; set; }
        public string ShortName { get; set; }
        public Guid? MselId { get; set; }
        public virtual Msel Msel { get; set; }
        public Guid? CiteTeamTypeId { get; set; }
        public string Email { get; set; }
        public Guid? PlayerTeamId { get; set; }
        public Guid? GalleryTeamId { get; set; }
        public Guid? CiteTeamId { get; set; }
        public bool canTeamLeaderInvite { get; set; }
        public bool canTeamMemberInvite { get; set; }
        public ICollection<User> Users { get; set; } = new List<User>();
        public virtual ICollection<CardTeam> CardTeams { get; set; } = new HashSet<CardTeam>();
        public virtual ICollection<PlayerApplicationTeam> PlayerApplicationTeams { get; set; } = new HashSet<PlayerApplicationTeam>();
        public virtual ICollection<Invitation> Invitations { get; set; } = new HashSet<Invitation>();
        public Guid? OldTeamId { get; set; }

    }

}
