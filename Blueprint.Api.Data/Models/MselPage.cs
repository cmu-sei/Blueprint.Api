// Copyright 2023 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Blueprint.Api.Data.Models
{
    public class MselPageEntity
    {
        public MselPageEntity() { }

        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public Guid Id { get; set; }
        public Guid MselId { get; set; }
        public MselEntity Msel { get; set; }
        public string Name { get; set; }
        public string Content { get; set; }
    }

    public class MselPageConfiguration : IEntityTypeConfiguration<MselPageEntity>
    {
        public void Configure(EntityTypeBuilder<MselPageEntity> builder)
        {
            builder.HasIndex(x => x.Id).IsUnique();

            builder
                .HasOne(u => u.Msel)
                .WithMany(p => p.Pages)
                .HasForeignKey(x => x.MselId)
                .OnDelete(DeleteBehavior.Cascade);
        }
    }
}

