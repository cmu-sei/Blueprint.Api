// Copyright 2024 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Blueprint.Api.Data.Enumerations;

namespace Blueprint.Api.Data.Models
{
    public class UserTeamRoleEntity : BaseEntity
    {
        public UserTeamRoleEntity() { }

        public UserTeamRoleEntity(Guid userId, Guid teamId, TeamRole role)
        {
            TeamId = teamId;
            UserId = userId;
            Role = role;
        }

        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public Guid Id { get; set; }
        public Guid TeamId { get; set; }
        public TeamEntity Team { get; set; }
        public Guid UserId { get; set; }
        public UserEntity User { get; set; }
        public TeamRole Role { get; set; }
    }

    public class UserTeamRoleConfiguration : IEntityTypeConfiguration<UserTeamRoleEntity>
    {
        public void Configure(EntityTypeBuilder<UserTeamRoleEntity> builder)
        {
            builder.HasIndex(x => new { x.TeamId, x.UserId, x.Role }).IsUnique();
            builder
                .HasOne(u => u.Team)
                .WithMany(p => p.UserTeamRoles)
                .HasForeignKey(x => x.TeamId)
                .OnDelete(DeleteBehavior.Cascade);
        }
    }
}

