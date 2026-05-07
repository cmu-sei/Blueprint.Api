// Copyright 2025 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Blueprint.Api.Data.Models
{
    public class ProficiencyLevelEntity : BaseEntity
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public Guid Id { get; set; }
        public Guid ProficiencyScaleId { get; set; }
        public virtual ProficiencyScaleEntity ProficiencyScale { get; set; }
        public string Name { get; set; }
        public int Value { get; set; }
        public string Description { get; set; }
        public int DisplayOrder { get; set; }
    }

    public class ProficiencyLevelConfiguration : IEntityTypeConfiguration<ProficiencyLevelEntity>
    {
        public void Configure(EntityTypeBuilder<ProficiencyLevelEntity> builder)
        {
            builder.HasOne(x => x.ProficiencyScale)
                .WithMany(x => x.ProficiencyLevels)
                .HasForeignKey(x => x.ProficiencyScaleId)
                .OnDelete(DeleteBehavior.Cascade);
        }
    }
}
