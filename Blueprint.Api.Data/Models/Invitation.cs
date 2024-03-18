// Copyright 2024 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Blueprint.Api.Data.Models
{
    public class InvitationEntity
    {
        public InvitationEntity() { }

        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public Guid Id { get; set; }
        public Guid MselId { get; set; }
        public MselEntity Msel { get; set; }
        public Guid TeamId { get; set; }
        public TeamEntity Team { get; set; }
        public string EmailDomain { get; set; }
        public DateTime? ExpirationDateTime { get; set; }
        public int MaxUsersAllowed { get; set; }
        public int UserCount { get; set; }
        public bool IsTeamLeader { get; set; }
        public bool WasDeactivated { get; set; }
    }

    public class InvitationConfiguration : IEntityTypeConfiguration<InvitationEntity>
    {
        public void Configure(EntityTypeBuilder<InvitationEntity> builder)
        {
            builder.HasIndex(x => x.Id).IsUnique();

            builder
                .HasOne(u => u.Msel)
                .WithMany(p => p.Invitations)
                .HasForeignKey(x => x.MselId)
                .OnDelete(DeleteBehavior.Cascade);

            builder
                .HasOne(u => u.Team)
                .WithMany(p => p.Invitations)
                .HasForeignKey(x => x.TeamId)
                .OnDelete(DeleteBehavior.Cascade);

        }
    }
}

