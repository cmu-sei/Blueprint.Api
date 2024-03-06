// Copyright 2024 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;

namespace Blueprint.Api.ViewModels
{
    public class Invitation
    {
        public Invitation() {
        }

        public Guid Id { get; set; }
        public Guid MselId { get; set; }
        public Msel Msel { get; set; }
        public Guid TeamId { get; set; }
        public Team Team { get; set; }
        public string EmailDomain { get; set; }
        public DateTime? ExpirationDateTime { get; set; }
        public int MaxUsersAllowed { get; set; }
        public int UserCount { get; set; }
        public bool IsTeamLeader { get; set; }
        public bool WasDeactivated { get; set; }
    }

}

