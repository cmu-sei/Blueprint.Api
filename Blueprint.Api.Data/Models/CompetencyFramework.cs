// Copyright 2024 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Blueprint.Api.Data.Models
{
    public class CompetencyFrameworkEntity : BaseEntity
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public Guid Id { get; set; }
        public string Name { get; set; }
        public string IdNumber { get; set; }
        public string Description { get; set; }
        public int DescriptionFormat { get; set; }
        public string Source { get; set; }
        public string Version { get; set; }
        public string ScaleValues { get; set; }
        public string ScaleConfiguration { get; set; }
        public string Taxonomies { get; set; }
        public Guid? DefaultProficiencyScaleId { get; set; }
        public virtual ProficiencyScaleEntity DefaultProficiencyScale { get; set; }
        public virtual ICollection<CompetencyEntity> Competencies { get; set; } = new HashSet<CompetencyEntity>();
    }

    public class CompetencyFrameworkConfiguration : IEntityTypeConfiguration<CompetencyFrameworkEntity>
    {
        public void Configure(EntityTypeBuilder<CompetencyFrameworkEntity> builder)
        {
            builder.HasIndex(x => x.IdNumber).IsUnique();

            builder.HasOne(x => x.DefaultProficiencyScale)
                .WithMany()
                .HasForeignKey(x => x.DefaultProficiencyScaleId)
                .OnDelete(DeleteBehavior.SetNull)
                .IsRequired(false);
        }
    }
}
