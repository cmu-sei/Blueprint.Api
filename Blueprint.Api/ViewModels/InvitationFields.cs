// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;

namespace Blueprint.Api.ViewModels
{
    /// <summary>
    /// The fields an Invitation's create and edit requests share. The owning MSEL is not one of them:
    /// only <see cref="InvitationCreate"/> names it, so an edit cannot move an Invitation to another
    /// MSEL. The use count is not one of them either; joins keep it.
    /// </summary>
    public abstract class InvitationFields
    {
        /// <summary>A team of the Invitation's MSEL.</summary>
        public Guid TeamId { get; set; }
        public string EmailDomain { get; set; }
        public DateTime? ExpirationDateTime { get; set; }
        public int MaxUsersAllowed { get; set; }
        public bool IsTeamLeader { get; set; }
        public bool WasDeactivated { get; set; }
    }

    public class InvitationCreate : InvitationFields
    {
        public Guid Id { get; set; }
        public Guid MselId { get; set; }
    }

    public class InvitationEdit : InvitationFields
    {
    }
}
