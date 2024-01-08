// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Blueprint.Api.Data.Models
{
    public class PlayerApplicationTeamEntity
    {
        public PlayerApplicationTeamEntity() { }

        public PlayerApplicationTeamEntity(Guid playerApplicationId, Guid teamId)
        {
            TeamId = teamId;
            PlayerApplicationId = playerApplicationId;
        }

        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public Guid Id { get; set; }

        public Guid TeamId { get; set; }
        public TeamEntity Team { get; set; }

        public Guid PlayerApplicationId { get; set; }
        public PlayerApplicationEntity PlayerApplication { get; set; }
    }

    public class PlayerApplicationTeamConfiguration : IEntityTypeConfiguration<PlayerApplicationTeamEntity>
    {
        public void Configure(EntityTypeBuilder<PlayerApplicationTeamEntity> builder)
        {
            builder.HasIndex(x => new { x.TeamId, x.PlayerApplicationId }).IsUnique();

            builder
                .HasOne(u => u.Team)
                .WithMany(p => p.PlayerApplicationTeams)
                .HasForeignKey(x => x.TeamId)
                .OnDelete(DeleteBehavior.Cascade);
            builder
                .HasOne(u => u.PlayerApplication)
                .WithMany(p => p.PlayerApplicationTeams)
                .HasForeignKey(x => x.PlayerApplicationId)
                .OnDelete(DeleteBehavior.Cascade);
        }
    }
}

