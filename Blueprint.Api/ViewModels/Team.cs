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
        public Guid? PlayerTeamId { get; set; }
        public bool IsParticipantTeam { get; set; }
        public ICollection<User> Users { get; set; } = new List<User>();
        public ICollection<Msel> Msels { get; set; } = new List<Msel>();
    }

}
