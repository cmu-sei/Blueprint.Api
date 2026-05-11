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
    public class CompetencyEntity : BaseEntity
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public Guid Id { get; set; }
        public Guid CompetencyFrameworkId { get; set; }
        public virtual CompetencyFrameworkEntity CompetencyFramework { get; set; }
        public Guid? ParentId { get; set; }
        public virtual CompetencyEntity Parent { get; set; }
        public string IdNumber { get; set; }
        public string ShortName { get; set; }
        public string Description { get; set; }
        public int DescriptionFormat { get; set; }
        public string Path { get; set; }
        public int SortOrder { get; set; }
        public string RuleType { get; set; }
        public int RuleOutcome { get; set; }
        public string RuleConfig { get; set; }
        public string ScaleValues { get; set; }
        public string ScaleConfiguration { get; set; }
        public virtual ICollection<CompetencyEntity> Children { get; set; } = new HashSet<CompetencyEntity>();
        public virtual ICollection<CompetencyRelationshipEntity> Relationships { get; set; } = new HashSet<CompetencyRelationshipEntity>();
        public virtual ICollection<CompetencyRelationshipEntity> InverseRelationships { get; set; } = new HashSet<CompetencyRelationshipEntity>();
    }

    public class CompetencyConfiguration : IEntityTypeConfiguration<CompetencyEntity>
    {
        public void Configure(EntityTypeBuilder<CompetencyEntity> builder)
        {
            builder.HasIndex(x => new { x.CompetencyFrameworkId, x.IdNumber }).IsUnique();

            builder.HasOne(x => x.CompetencyFramework)
                .WithMany(x => x.Competencies)
                .HasForeignKey(x => x.CompetencyFrameworkId)
                .OnDelete(DeleteBehavior.Cascade);

            builder.HasOne(x => x.Parent)
                .WithMany(x => x.Children)
                .HasForeignKey(x => x.ParentId)
                .OnDelete(DeleteBehavior.Restrict)
                .IsRequired(false);
        }
    }
}
