// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Blueprint.Api.Data.Models
{
    public class ScenarioEventEntity : BaseEntity
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public Guid Id { get; set; }
        public Guid MselId { get; set; }
        public virtual MselEntity Msel { get; set; }
        public virtual ICollection<DataValueEntity> DataValues { get; set; } = new HashSet<DataValueEntity>();
        public int RowIndex { get; set; }
        public string RowMetadata { get; set; }
    }

    public class ScenarioEventEntityConfiguration : IEntityTypeConfiguration<ScenarioEventEntity>
    {
        public void Configure(EntityTypeBuilder<ScenarioEventEntity> builder)
        {
            builder
                .HasOne(d => d.Msel)
                .WithMany(d => d.ScenarioEvents)
                .OnDelete(DeleteBehavior.Cascade);
        }
    }

}

