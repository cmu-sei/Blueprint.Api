// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Blueprint.Api.Data.Models
{
    public class ScenarioEventTeamEntity
    {
        public ScenarioEventTeamEntity() { }

        public ScenarioEventTeamEntity(Guid teamId, Guid scenarioEventId)
        {
            TeamId = teamId;
            ScenarioEventId = scenarioEventId;
        }

        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public Guid Id { get; set; }

        public Guid TeamId { get; set; }
        public TeamEntity Team { get; set; }

        public Guid ScenarioEventId { get; set; }
        public ScenarioEventEntity ScenarioEvent { get; set; }
    }

    public class ScenarioEventTeamConfiguration : IEntityTypeConfiguration<ScenarioEventTeamEntity>
    {
        public void Configure(EntityTypeBuilder<ScenarioEventTeamEntity> builder)
        {
            builder.HasIndex(x => new { x.TeamId, x.ScenarioEventId }).IsUnique();

            builder
                .HasOne(u => u.Team)
                .WithMany(p => p.ScenarioEventTeams)
                .HasForeignKey(x => x.TeamId);
            builder
                .HasOne(u => u.ScenarioEvent)
                .WithMany(p => p.ScenarioEventTeams)
                .HasForeignKey(x => x.ScenarioEventId);
        }
    }
}

