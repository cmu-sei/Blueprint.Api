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
    /// <summary>A technique or sub-technique in one release.</summary>
    public class TechniqueEntity
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.None)]
        public Guid Id { get; set; }
        public Guid TechniqueReleaseId { get; set; }
        public TechniqueReleaseEntity TechniqueRelease { get; set; }
        public string StixId { get; set; }
        public string ExternalId { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public string Url { get; set; }
        /// <summary>The technique a sub-technique belongs to; null for a technique.</summary>
        public Guid? ParentId { get; set; }
        public TechniqueEntity Parent { get; set; }
        public virtual ICollection<TechniqueEntity> SubTechniques { get; set; } = new HashSet<TechniqueEntity>();
        /// <summary>Comma-separated tactic short names, in the order the bundle lists them.</summary>
        public string Tactics { get; set; }
        /// <summary>Comma-separated platforms, e.g. "Windows,Linux,macOS".</summary>
        public string Platforms { get; set; }
        /// <summary>Comma-separated ids of the weaknesses the technique exploits; null when the framework gives none.</summary>
        public string Weaknesses { get; set; }
        /// <summary>
        /// Links to other catalogues, one per line as "source|label|url"; null when the framework gives none.
        /// </summary>
        public string ReferenceLinks { get; set; }
    }

    public class TechniqueConfiguration : IEntityTypeConfiguration<TechniqueEntity>
    {
        public void Configure(EntityTypeBuilder<TechniqueEntity> builder)
        {
            builder.HasIndex(x => new { x.TechniqueReleaseId, x.ExternalId }).IsUnique();

            builder
                .HasOne(x => x.TechniqueRelease)
                .WithMany(x => x.Techniques)
                .HasForeignKey(x => x.TechniqueReleaseId)
                .OnDelete(DeleteBehavior.Cascade);
            // NO ACTION, not RESTRICT, so the release's cascade can take a technique and its sub-techniques together
            builder
                .HasOne(x => x.Parent)
                .WithMany(x => x.SubTechniques)
                .HasForeignKey(x => x.ParentId)
                .OnDelete(DeleteBehavior.NoAction);
        }
    }
}
