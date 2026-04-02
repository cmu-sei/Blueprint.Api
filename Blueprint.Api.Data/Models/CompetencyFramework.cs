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
    public class CompetencyFrameworkEntity : BaseEntity
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public Guid Id { get; set; }
        public string Name { get; set; }
        public string Version { get; set; }
        public string Source { get; set; }
        public string Description { get; set; }
        public virtual ICollection<CompetencyElementEntity> CompetencyElements { get; set; } = new HashSet<CompetencyElementEntity>();
        public virtual ICollection<ProficiencyScaleEntity> ProficiencyScales { get; set; } = new HashSet<ProficiencyScaleEntity>();
    }

    public class CompetencyFrameworkConfiguration : IEntityTypeConfiguration<CompetencyFrameworkEntity>
    {
        public void Configure(EntityTypeBuilder<CompetencyFrameworkEntity> builder)
        {
            builder.HasIndex(x => new { x.Name, x.Version }).IsUnique();
        }
    }
}
