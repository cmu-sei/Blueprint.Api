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
    /// An ATT&amp;CK technique in a MSEL's pool - the techniques its AttackTechnique columns offer. Every row on
    /// a MSEL is from the same release.
    /// </summary>
    public class MselAttackTechniqueEntity
    {
        public MselAttackTechniqueEntity() { }

        public MselAttackTechniqueEntity(Guid mselId, Guid attackTechniqueId)
        {
            MselId = mselId;
            AttackTechniqueId = attackTechniqueId;
        }

        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public Guid Id { get; set; }

        public Guid MselId { get; set; }
        public MselEntity Msel { get; set; }

        public Guid AttackTechniqueId { get; set; }
        public AttackTechniqueEntity AttackTechnique { get; set; }
    }

    public class MselAttackTechniqueConfiguration : IEntityTypeConfiguration<MselAttackTechniqueEntity>
    {
        public void Configure(EntityTypeBuilder<MselAttackTechniqueEntity> builder)
        {
            builder.HasIndex(x => new { x.MselId, x.AttackTechniqueId }).IsUnique();

            builder
                .HasOne(u => u.Msel)
                .WithMany(p => p.MselAttackTechniques)
                .HasForeignKey(x => x.MselId)
                .OnDelete(DeleteBehavior.Cascade);
            // A release in use by a MSEL can't be deleted out from under it.
            builder
                .HasOne(u => u.AttackTechnique)
                .WithMany()
                .HasForeignKey(x => x.AttackTechniqueId)
                .OnDelete(DeleteBehavior.Restrict);
        }
    }
}
