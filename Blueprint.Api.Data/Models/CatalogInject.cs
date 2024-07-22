// Copyright 2024 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Blueprint.Api.Data.Models
{
    public class CatalogInjectEntity
    {
        public CatalogInjectEntity() { }

        public CatalogInjectEntity(Guid catalogId, Guid injectId)
        {
            InjectId = injectId;
            CatalogId = catalogId;
        }

        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public Guid Id { get; set; }

        public Guid InjectId { get; set; }
        public InjectEntity Inject { get; set; }

        public Guid CatalogId { get; set; }
        public CatalogEntity Catalog { get; set; }
        public bool IsNew { get; set; }
        public int DisplayOrder { get; set; }
    }

    public class CatalogInjectConfiguration : IEntityTypeConfiguration<CatalogInjectEntity>
    {
        public void Configure(EntityTypeBuilder<CatalogInjectEntity> builder)
        {
            builder.HasIndex(x => new { x.InjectId, x.CatalogId }).IsUnique();

            builder
                .HasOne(u => u.Inject)
                .WithMany(p => p.CatalogInjects)
                .HasForeignKey(x => x.InjectId)
                .OnDelete(DeleteBehavior.Cascade);
            builder
                .HasOne(u => u.Catalog)
                .WithMany(p => p.CatalogInjects)
                .HasForeignKey(x => x.CatalogId)
                .OnDelete(DeleteBehavior.Cascade);
        }
    }
}
