// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Blueprint.Api.Data.Models
{
    /// <summary>An ATT&amp;CK technique or sub-technique (T1059, T1059.001) in one release.</summary>
    public class AttackTechniqueEntity
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.None)]
        public Guid Id { get; set; }
        public Guid AttackReleaseId { get; set; }
        public AttackReleaseEntity AttackRelease { get; set; }
        public string StixId { get; set; }
        public string ExternalId { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public string Url { get; set; }
        /// <summary>The technique a sub-technique belongs to; null for a technique.</summary>
        public Guid? ParentId { get; set; }
        public AttackTechniqueEntity Parent { get; set; }
        public virtual ICollection<AttackTechniqueEntity> SubTechniques { get; set; } = new HashSet<AttackTechniqueEntity>();
        /// <summary>Comma-separated tactic short names, in the order the bundle lists them.</summary>
        public string Tactics { get; set; }
        /// <summary>Comma-separated platforms, e.g. "Windows,Linux,macOS".</summary>
        public string Platforms { get; set; }
    }

    public class AttackTechniqueConfiguration : IEntityTypeConfiguration<AttackTechniqueEntity>
    {
        public void Configure(EntityTypeBuilder<AttackTechniqueEntity> builder)
        {
            builder.HasIndex(x => new { x.AttackReleaseId, x.ExternalId }).IsUnique();

            builder
                .HasOne(x => x.AttackRelease)
                .WithMany(x => x.Techniques)
                .HasForeignKey(x => x.AttackReleaseId)
                .OnDelete(DeleteBehavior.Cascade);
            // NO ACTION rather than RESTRICT: PostgreSQL checks it at the end of the statement, so the release's
            // cascade can take a technique and its sub-techniques together.
            builder
                .HasOne(x => x.Parent)
                .WithMany(x => x.SubTechniques)
                .HasForeignKey(x => x.ParentId)
                .OnDelete(DeleteBehavior.NoAction);
        }
    }
}
