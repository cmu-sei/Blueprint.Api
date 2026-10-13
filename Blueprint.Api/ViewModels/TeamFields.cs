// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;

namespace Blueprint.Api.ViewModels
{
    /// <summary>
    /// The fields a Team's create and edit requests share. The owning MSEL is not one of them: only
    /// <see cref="TeamCreate"/> names it, so an edit cannot move a Team to another MSEL. Nor are the
    /// Team's users, roles, invitations or card and application assignments, which have their own
    /// endpoints.
    /// </summary>
    public abstract class TeamFields
    {
        public string Name { get; set; }
        public string ShortName { get; set; }
        public string Description { get; set; }
        public Guid? CiteTeamTypeId { get; set; }
        public string Email { get; set; }
        public bool canTeamLeaderInvite { get; set; }
        public bool canTeamMemberInvite { get; set; }
    }

    public class TeamCreate : TeamFields
    {
        public Guid Id { get; set; }
        public Guid? MselId { get; set; }
    }

    public class TeamEdit : TeamFields
    {
    }
}
