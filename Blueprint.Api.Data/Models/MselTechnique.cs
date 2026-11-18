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
    /// A technique in a MSEL's pool - the techniques its Technique and DefensiveTechnique columns offer. The
    /// pool can hold several domains (one per imported framework), and the rows of one domain are
    /// all from the same release.
    /// </summary>
    public class MselTechniqueEntity
    {
        public MselTechniqueEntity() { }

        public MselTechniqueEntity(Guid mselId, Guid techniqueId)
        {
            MselId = mselId;
            TechniqueId = techniqueId;
        }

        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public Guid Id { get; set; }

        public Guid MselId { get; set; }
        public MselEntity Msel { get; set; }

        public Guid TechniqueId { get; set; }
        public TechniqueEntity Technique { get; set; }
    }

    public class MselTechniqueConfiguration : IEntityTypeConfiguration<MselTechniqueEntity>
    {
        public void Configure(EntityTypeBuilder<MselTechniqueEntity> builder)
        {
            builder.HasIndex(x => new { x.MselId, x.TechniqueId }).IsUnique();

            builder
                .HasOne(u => u.Msel)
                .WithMany(p => p.MselTechniques)
                .HasForeignKey(x => x.MselId)
                .OnDelete(DeleteBehavior.Cascade);
            // A release in use by a MSEL can't be deleted out from under it.
            builder
                .HasOne(u => u.Technique)
                .WithMany()
                .HasForeignKey(x => x.TechniqueId)
                .OnDelete(DeleteBehavior.Restrict);
        }
    }
}
