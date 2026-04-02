// Copyright 2025 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Blueprint.Api.Data.Models
{
    public class CompetencyElementEntity : BaseEntity
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public Guid Id { get; set; }
        public Guid CompetencyFrameworkId { get; set; }
        public virtual CompetencyFrameworkEntity CompetencyFramework { get; set; }
        public string ElementIdentifier { get; set; }
        public string ElementType { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public Guid? ParentId { get; set; }
        public virtual CompetencyElementEntity Parent { get; set; }
        public virtual ICollection<CompetencyElementEntity> Children { get; set; } = new HashSet<CompetencyElementEntity>();
    }

    public class CompetencyElementConfiguration : IEntityTypeConfiguration<CompetencyElementEntity>
    {
        public void Configure(EntityTypeBuilder<CompetencyElementEntity> builder)
        {
            builder.HasIndex(x => new { x.CompetencyFrameworkId, x.ElementIdentifier }).IsUnique();

            builder.HasOne(x => x.CompetencyFramework)
                .WithMany(x => x.CompetencyElements)
                .HasForeignKey(x => x.CompetencyFrameworkId)
                .OnDelete(DeleteBehavior.Cascade);

            builder.HasOne(x => x.Parent)
                .WithMany(x => x.Children)
                .HasForeignKey(x => x.ParentId)
                .OnDelete(DeleteBehavior.Restrict);
        }
    }
}
