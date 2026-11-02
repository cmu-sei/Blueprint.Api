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
    /// One "countered by" mapping from MITRE D3FEND: an offensive technique (ATT&amp;CK T1003.005, ICS T0800, SPARTA
    /// EX-0001.01) and the D3FEND technique that counters it, from a release's d3fend-full-mappings.json.
    /// </summary>
    /// <remarks>
    /// The offensive side is the technique's id, not a key: the mappings name ATT&amp;CK techniques by id across
    /// every domain and version, and a MSEL's attack release may be any of them. The defensive side is the D3FEND
    /// technique in the release the mappings file was imported onto. D3FEND lists the same pair once per artifact
    /// path (the defensive technique hardens a Credential; T1003.005 accesses an Encrypted Credential), so a row
    /// here is the pair with the number of distinct paths as its weight and the most common path as its rationale.
    /// </remarks>
    public class AttackTechniqueCountermeasureEntity
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public Guid Id { get; set; }

        /// <summary>The D3FEND release the mappings file belongs to.</summary>
        public Guid ReleaseId { get; set; }
        public AttackReleaseEntity Release { get; set; }

        /// <summary>The offensive technique's id (T1003.005), as the mappings file gives it.</summary>
        public string OffensiveExternalId { get; set; }

        /// <summary>The D3FEND technique that counters it, in the same release.</summary>
        public Guid DefensiveTechniqueId { get; set; }
        public AttackTechniqueEntity DefensiveTechnique { get; set; }

        /// <summary>The D3FEND tactic the mapping is filed under (Harden, Detect, ...), as labelled in the file.</summary>
        public string DefensiveTactic { get; set; }

        /// <summary>Why, in a line: the most common artifact path, e.g. "hardens Credential; T1003.005 accesses Encrypted Credential".</summary>
        public string Rationale { get; set; }

        /// <summary>The number of distinct artifact paths that link the pair; more paths, a stronger countermeasure.</summary>
        public int Weight { get; set; }
    }

    public class AttackTechniqueCountermeasureConfiguration : IEntityTypeConfiguration<AttackTechniqueCountermeasureEntity>
    {
        public void Configure(EntityTypeBuilder<AttackTechniqueCountermeasureEntity> builder)
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
