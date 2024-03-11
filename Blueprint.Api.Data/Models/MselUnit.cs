// Copyright 2024 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Blueprint.Api.Data.Models
{
    public class MselUnitEntity
    {
        public MselUnitEntity() { }

        public MselUnitEntity(Guid unitId, Guid mselId)
        {
            UnitId = unitId;
            MselId = mselId;
        }

        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public Guid Id { get; set; }

        public Guid UnitId { get; set; }
        public UnitEntity Unit { get; set; }

        public Guid MselId { get; set; }
        public MselEntity Msel { get; set; }
    }

    public class MselUnitConfiguration : IEntityTypeConfiguration<MselUnitEntity>
    {
        public void Configure(EntityTypeBuilder<MselUnitEntity> builder)
        {
            builder.HasIndex(x => new { x.UnitId, x.MselId }).IsUnique();

            builder
                .HasOne(u => u.Unit)
                .WithMany(p => p.MselUnits)
                .HasForeignKey(x => x.UnitId)
                .OnDelete(DeleteBehavior.Cascade);
            builder
                .HasOne(u => u.Msel)
                .WithMany(p => p.MselUnits)
                .HasForeignKey(x => x.MselId)
                .OnDelete(DeleteBehavior.Cascade);
        }
    }
}

