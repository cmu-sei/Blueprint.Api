// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Blueprint.Api.Data.Models
{
    // The image is kept out of the msels table, because every authorization check loads the whole
    // MSEL row. MselEntity.ImageDateModified says whether there is one.
    public class MselImageEntity
    {
        [Key]
        public Guid MselId { get; set; }
        public byte[] Data { get; set; }
        public string ContentType { get; set; }
    }

    public class MselImageConfiguration : IEntityTypeConfiguration<MselImageEntity>
    {
        public void Configure(EntityTypeBuilder<MselImageEntity> builder)
        {
            builder
                .HasOne<MselEntity>()
                .WithOne()
                .HasForeignKey<MselImageEntity>(x => x.MselId)
                .OnDelete(DeleteBehavior.Cascade);
        }
    }
}
