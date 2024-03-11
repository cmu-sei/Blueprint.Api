// Copyright 2024 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace Blueprint.Api.Data.Models
{
    public class UnitEntity : BaseEntity
    {
        [Key]
        public Guid Id { get; set; }
        public string Name { get; set; }
        public string ShortName { get; set; }
        public ICollection<UnitUserEntity> UnitUsers { get; set; } = new List<UnitUserEntity>();
        public virtual ICollection<MselUnitEntity> MselUnits { get; set; } = new HashSet<MselUnitEntity>();
        public Guid? OldTeamId { get; set; }
    }

    public class UnitConfiguration : IEntityTypeConfiguration<UnitEntity>
    {
        public void Configure(EntityTypeBuilder<UnitEntity> builder)
        {
            builder.HasIndex(e => e.Id).IsUnique();
        }
    }
}
