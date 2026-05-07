// Copyright 2024 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Blueprint.Api.Data.Models
{
    public class MselCompetencyEntity
    {
        public MselCompetencyEntity() { }

        public MselCompetencyEntity(Guid mselId, Guid competencyId)
        {
            MselId = mselId;
            CompetencyId = competencyId;
        }

        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public Guid Id { get; set; }

        public Guid MselId { get; set; }
        public MselEntity Msel { get; set; }

        public Guid CompetencyId { get; set; }
        public CompetencyEntity Competency { get; set; }
    }

    public class MselCompetencyConfiguration : IEntityTypeConfiguration<MselCompetencyEntity>
    {
        public void Configure(EntityTypeBuilder<MselCompetencyEntity> builder)
        {
            builder.HasIndex(x => new { x.MselId, x.CompetencyId }).IsUnique();

            builder
                .HasOne(u => u.Msel)
                .WithMany(p => p.MselCompetencies)
                .HasForeignKey(x => x.MselId)
                .OnDelete(DeleteBehavior.Cascade);
            builder
                .HasOne(u => u.Competency)
                .WithMany()
                .HasForeignKey(x => x.CompetencyId)
                .OnDelete(DeleteBehavior.Cascade);
        }
    }
}
