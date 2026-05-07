// Copyright 2024 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Blueprint.Api.Data.Models
{
    public class CompetencyRelationshipEntity : BaseEntity
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public Guid Id { get; set; }
        public Guid CompetencyId { get; set; }
        public virtual CompetencyEntity Competency { get; set; }
        public Guid RelatedCompetencyId { get; set; }
        public virtual CompetencyEntity RelatedCompetency { get; set; }
    }

    public class CompetencyRelationshipConfiguration : IEntityTypeConfiguration<CompetencyRelationshipEntity>
    {
        public void Configure(EntityTypeBuilder<CompetencyRelationshipEntity> builder)
        {
            builder.HasIndex(x => new { x.CompetencyId, x.RelatedCompetencyId }).IsUnique();

            builder.HasOne(x => x.Competency)
                .WithMany(x => x.Relationships)
                .HasForeignKey(x => x.CompetencyId)
                .OnDelete(DeleteBehavior.Cascade);

            builder.HasOne(x => x.RelatedCompetency)
                .WithMany(x => x.InverseRelationships)
                .HasForeignKey(x => x.RelatedCompetencyId)
                .OnDelete(DeleteBehavior.Cascade);
        }
    }
}
