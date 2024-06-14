// Copyright 2024 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact injectType@sei.cmu.edu for full terms.

using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Blueprint.Api.Data.Models
{
    public class InjectTypeEntity : BaseEntity
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public Guid Id { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public virtual ICollection<DataFieldEntity> DataFields { get; set; } = new HashSet<DataFieldEntity>();
    }

    public class InjectTypeConfiguration : IEntityTypeConfiguration<InjectTypeEntity>
    {
        public void Configure(EntityTypeBuilder<InjectTypeEntity> builder)
        {
            builder.HasIndex(x => new { x.Name }).IsUnique();
        }
    }
}

