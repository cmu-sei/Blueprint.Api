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
    public class CatalogEntity : BaseEntity
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public Guid Id { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public Guid InjectTypeId { get; set; }
        public InjectTypeEntity InjectType { get; set; }
        public bool IsPublic { get; set; }
        public Guid? ParentId { get; set; }
        public CatalogEntity Parent { get; set; }
        public virtual ICollection<InjectEntity> Injects { get; set; } = new HashSet<InjectEntity>();
        public virtual ICollection<CatalogUnitEntity> CatalogUnits { get; set; } = new HashSet<CatalogUnitEntity>();
        public virtual ICollection<CatalogInjectEntity> CatalogInjects { get; set; } = new HashSet<CatalogInjectEntity>();
    }

    public class CatalogConfiguration : IEntityTypeConfiguration<CatalogEntity>
    {
        public void Configure(EntityTypeBuilder<CatalogEntity> builder)
        {
            builder.HasIndex(x => new { x.Name }).IsUnique();
        }
    }

}

