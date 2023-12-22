// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Blueprint.Api.Data.Models
{
    public class CiteRoleEntity : BaseEntity
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public Guid Id { get; set; }
        public Guid? MselId { get; set; }
        public virtual MselEntity Msel { get; set; }
        public Guid? TeamId { get; set; }
        public virtual TeamEntity Team { get; set; }
        public string Name { get; set; }
        public bool IsTemplate { get; set; }
    }

    public class CiteRoleEntityConfiguration : IEntityTypeConfiguration<CiteRoleEntity>
    {
        public void Configure(EntityTypeBuilder<CiteRoleEntity> builder)
        {
            builder
                .HasOne(d => d.Msel)
                .WithMany(d => d.CiteRoles)
                .OnDelete(DeleteBehavior.Cascade);
        }
    }

}

