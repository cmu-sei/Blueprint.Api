// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace Blueprint.Api.Data.Models
{
    public class TeamEntity : BaseEntity
    {
        [Key]
        public Guid Id { get; set; }
        public string Name { get; set; }
        public string ShortName { get; set; }
        public Guid? MselId { get; set; }
        public virtual MselEntity Msel { get; set; }
        public Guid? CiteTeamTypeId { get; set; }
        public string Email { get; set; }
        public Guid? PlayerTeamId { get; set; }
        public Guid? GalleryTeamId { get; set; }
        public Guid? CiteTeamId { get; set; }
        public bool canTeamLeaderInvite { get; set; }
        public bool canTeamMemberInvite { get; set; }
        public ICollection<TeamUserEntity> TeamUsers { get; set; } = new List<TeamUserEntity>();
        public virtual ICollection<CardTeamEntity> CardTeams { get; set; } = new HashSet<CardTeamEntity>();
        public virtual ICollection<PlayerApplicationTeamEntity> PlayerApplicationTeams { get; set; } = new HashSet<PlayerApplicationTeamEntity>();
        public virtual ICollection<InvitationEntity> Invitations { get; set; } = new HashSet<InvitationEntity>();
        public Guid? OldTeamId { get; set; }
        public virtual ICollection<UserTeamRoleEntity> UserTeamRoles { get; set; } = new HashSet<UserTeamRoleEntity>();
        public virtual ICollection<CiteActionEntity> CiteActions { get; set; } = new HashSet<CiteActionEntity>();
        public virtual ICollection<CiteRoleEntity> CiteRoles { get; set; } = new HashSet<CiteRoleEntity>();
    }

    public class TeamConfiguration : IEntityTypeConfiguration<TeamEntity>
    {
        public void Configure(EntityTypeBuilder<TeamEntity> builder)
        {
            builder.HasIndex(e => e.Id).IsUnique();

            builder
                .HasOne(d => d.Msel)
                .WithMany(d => d.Teams)
                .OnDelete(DeleteBehavior.Cascade);
        }
    }
}
