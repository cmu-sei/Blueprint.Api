// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Blueprint.Api.Data.Models
{
    /// <summary>
    /// One "countered by" mapping from a defensive framework: an offensive technique (T1003.005, T0800, EX-0001.01)
    /// and the defensive technique that counters it, from the mappings file imported onto a release.
    /// </summary>
    /// <remarks>
    /// The offensive side is the technique's id, not a key: the mappings name offensive techniques by id across
    /// every domain and version, and a MSEL's attack release may be any of them. The defensive side is the
    /// technique in the release the mappings file was imported onto. The file lists the same pair once per artifact
    /// path (the defensive technique hardens a Credential; T1003.005 accesses an Encrypted Credential), so a row
    /// here is the pair with the number of distinct paths as its weight and the most common path as its rationale.
    /// </remarks>
    public class TechniqueCountermeasureEntity
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public Guid Id { get; set; }

        /// <summary>The defensive release the mappings file belongs to.</summary>
        public Guid ReleaseId { get; set; }
        public TechniqueReleaseEntity Release { get; set; }

        /// <summary>The offensive technique's id (T1003.005), as the mappings file gives it.</summary>
        public string OffensiveExternalId { get; set; }

        /// <summary>The defensive technique that counters it, in the same release.</summary>
        public Guid DefensiveTechniqueId { get; set; }
        public TechniqueEntity DefensiveTechnique { get; set; }

        /// <summary>The defensive tactic the mapping is filed under (Harden, Detect, ...), as labelled in the file.</summary>
        public string DefensiveTactic { get; set; }

        /// <summary>Why, in a line: the most common artifact path, e.g. "hardens Credential; T1003.005 accesses Encrypted Credential".</summary>
        public string Rationale { get; set; }

        /// <summary>The number of distinct artifact paths that link the pair; more paths, a stronger countermeasure.</summary>
        public int Weight { get; set; }
    }

    public class TechniqueCountermeasureConfiguration : IEntityTypeConfiguration<TechniqueCountermeasureEntity>
    {
        public void Configure(EntityTypeBuilder<TechniqueCountermeasureEntity> builder)
        {
            builder.HasIndex(x => new { x.ReleaseId, x.OffensiveExternalId, x.DefensiveTechniqueId }).IsUnique();

            builder
                .HasOne(x => x.Release)
                .WithMany(x => x.Countermeasures)
                .HasForeignKey(x => x.ReleaseId)
                .OnDelete(DeleteBehavior.Cascade);
            builder
                .HasOne(x => x.DefensiveTechnique)
                .WithMany()
                .HasForeignKey(x => x.DefensiveTechniqueId)
                .OnDelete(DeleteBehavior.Cascade);
        }
    }
}
