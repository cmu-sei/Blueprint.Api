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
        public Guid? CiteTeamTypeId { get; set; }
        public string Email { get; set; }
        public Guid? PlayerTeamId { get; set; }
        public Guid? GalleryTeamId { get; set; }
        public Guid? CiteTeamId { get; set; }
        public bool canTeamLeaderInvite { get; set; }
        public bool canTeamMemberInvite { get; set; }
        public User[] Users { get; set; }
        public CardTeam[] CardTeams { get; set; }
        public PlayerApplicationTeam[] PlayerApplicationTeams { get; set; }
        public Invitation[] Invitations { get; set; }
        public ICollection<UserTeamRole> UserTeamRoles { get; set; } = new List<UserTeamRole>();

    }

}
