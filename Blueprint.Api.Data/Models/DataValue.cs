// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Blueprint.Api.Data.Models
{
    public class DataValueEntity : BaseEntity
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public Guid Id { get; set; }
        public string Value { get; set; }
        public Guid ScenarioEventId { get; set; }
        public virtual ScenarioEventEntity ScenarioEvent { get; set; }
        public Guid DataFieldId { get; set; }
        public virtual DataFieldEntity DataField { get; set; }
        public string CellMetadata { get; set; }
    }

    public class DataValueEntityConfiguration : IEntityTypeConfiguration<DataValueEntity>
    {
        public void Configure(EntityTypeBuilder<DataValueEntity> builder)
        {
            builder.HasIndex(e => e.Id).IsUnique();
            builder.HasIndex(e => new { e.ScenarioEventId, e.DataFieldId }).IsUnique();
            builder
                .HasOne(d => d.ScenarioEvent)
                .WithMany(d => d.DataValues)
                .OnDelete(DeleteBehavior.Cascade);
        }
    }

}

