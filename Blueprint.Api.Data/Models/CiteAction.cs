// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Blueprint.Api.Data.Models
{
    public class CiteActionEntity : BaseEntity
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public Guid Id { get; set; }
        public Guid? MselId { get; set; }
        public virtual MselEntity Msel { get; set; }
        public Guid TeamId { get; set; }
        public virtual TeamEntity Team { get; set; }
        public int MoveNumber { get; set; }
        public int InjectNumber { get; set; }
        public int ActionNumber { get; set; }
        public string Description { get; set; }
        public bool IsTemplate { get; set; }
    }

    public class CiteActionEntityConfiguration : IEntityTypeConfiguration<CiteActionEntity>
    {
        public void Configure(EntityTypeBuilder<CiteActionEntity> builder)
        {
            builder
                .HasOne(d => d.Msel)
                .WithMany(d => d.CiteActions)
                .OnDelete(DeleteBehavior.Cascade);
        }
    }

}

