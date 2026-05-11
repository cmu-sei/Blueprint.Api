// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Blueprint.Api.Data.Models
{
    public class TeamCompetencyEntity
    {
        public TeamCompetencyEntity() { }

        public TeamCompetencyEntity(Guid teamId, Guid competencyId)
        {
            TeamId = teamId;
            CompetencyId = competencyId;
        }

        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public Guid Id { get; set; }

        public Guid TeamId { get; set; }
        public TeamEntity Team { get; set; }

        public Guid CompetencyId { get; set; }
        public CompetencyEntity Competency { get; set; }
    }

    public class TeamCompetencyConfiguration : IEntityTypeConfiguration<TeamCompetencyEntity>
    {
        public void Configure(EntityTypeBuilder<TeamCompetencyEntity> builder)
        {
            builder.HasIndex(x => new { x.TeamId, x.CompetencyId }).IsUnique();

            builder
                .HasOne(u => u.Team)
                .WithMany(p => p.TeamCompetencies)
                .HasForeignKey(x => x.TeamId)
                .OnDelete(DeleteBehavior.Cascade);
            builder
                .HasOne(u => u.Competency)
                .WithMany()
                .HasForeignKey(x => x.CompetencyId)
                .OnDelete(DeleteBehavior.Cascade);
        }
    }
}
