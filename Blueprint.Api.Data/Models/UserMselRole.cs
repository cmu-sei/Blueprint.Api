// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Blueprint.Api.Data.Enumerations;

namespace Blueprint.Api.Data.Models
{
    public class UserMselRoleEntity : BaseEntity
    {
        public UserMselRoleEntity() { }

        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public Guid Id { get; set; }
        public Guid MselId { get; set; }
        public MselEntity Msel { get; set; }
        public Guid UserId { get; set; }
        public UserEntity User { get; set; }
        public MselRole Role { get; set; }
    }

    public class UserMselRoleConfiguration : IEntityTypeConfiguration<UserMselRoleEntity>
    {
        public void Configure(EntityTypeBuilder<UserMselRoleEntity> builder)
        {
            builder.HasIndex(x => new { x.MselId, x.UserId, x.Role }).IsUnique();
        }
    }
}

