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
    public class ProficiencyScaleEntity : BaseEntity
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public Guid Id { get; set; }
        public Guid CompetencyFrameworkId { get; set; }
        public virtual CompetencyFrameworkEntity CompetencyFramework { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public virtual ICollection<ProficiencyLevelEntity> ProficiencyLevels { get; set; } = new HashSet<ProficiencyLevelEntity>();
    }

    public class ProficiencyScaleConfiguration : IEntityTypeConfiguration<ProficiencyScaleEntity>
    {
        public void Configure(EntityTypeBuilder<ProficiencyScaleEntity> builder)
        {
            builder.HasOne(x => x.CompetencyFramework)
                .WithMany(x => x.ProficiencyScales)
                .HasForeignKey(x => x.CompetencyFrameworkId)
                .OnDelete(DeleteBehavior.Cascade);
        }
    }
}
