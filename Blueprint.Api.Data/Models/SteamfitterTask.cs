// Copyright 2025 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Blueprint.Api.Data.Enumerations;

namespace Blueprint.Api.Data.Models
{
    public class SteamfitterTaskEntity : BaseEntity
    {
        public SteamfitterTaskEntity() { }
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public Guid Id { get; set; }
        public Guid ScenarioEventId { get; set; }
        public virtual ScenarioEventEntity ScenarioEvent { get; set; }
        public SteamfitterIntegrationType TaskType { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public SteamfitterTaskAction Action { get; set; }
        public string VmMask { get; set; }
        public string ApiUrl { get; set; }
        public Dictionary<string, string> ActionParameters { get; set; }
        public string ExpectedOutput { get; set; }
        public int ExpirationSeconds { get; set; }
        public int DelaySeconds { get; set; }
        public int IntervalSeconds { get; set; }
        public int Iterations { get; set; }
        public SteamfitterTaskTrigger TriggerCondition { get; set; }
        public bool UserExecutable { get; set; }
        public bool Repeatable { get; set; }
    }

    public class SteamfitterTaskEntityConfiguration : IEntityTypeConfiguration<ScenarioEventEntity>
    {
        public void Configure(EntityTypeBuilder<ScenarioEventEntity> builder)
        {
            builder.HasIndex(x => x.Id).IsUnique();
            builder
                .HasOne(m => m.SteamfitterTask)
                .WithOne(t => t.ScenarioEvent)
                .HasForeignKey<SteamfitterTaskEntity>(t => t.ScenarioEventId)
                .OnDelete(DeleteBehavior.Cascade);
        }
    }


}
