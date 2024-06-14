// Copyright 2024 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Blueprint.Api.Data.Models
{
    public class CatalogUnitEntity
    {
        public CatalogUnitEntity() { }

        public CatalogUnitEntity(Guid unitId, Guid catalogId)
        {
            UnitId = unitId;
            CatalogId = catalogId;
        }

        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public Guid Id { get; set; }

        public Guid UnitId { get; set; }
        public UnitEntity Unit { get; set; }

        public Guid CatalogId { get; set; }
        public CatalogEntity Catalog { get; set; }
    }

    public class CatalogUnitConfiguration : IEntityTypeConfiguration<CatalogUnitEntity>
    {
        public void Configure(EntityTypeBuilder<CatalogUnitEntity> builder)
        {
            builder.HasIndex(x => new { x.UnitId, x.CatalogId }).IsUnique();

            builder
                .HasOne(u => u.Unit)
                .WithMany(p => p.CatalogUnits)
                .HasForeignKey(x => x.UnitId)
                .OnDelete(DeleteBehavior.Cascade);
            builder
                .HasOne(u => u.Catalog)
                .WithMany(p => p.CatalogUnits)
                .HasForeignKey(x => x.CatalogId)
                .OnDelete(DeleteBehavior.Cascade);
        }
    }
}

