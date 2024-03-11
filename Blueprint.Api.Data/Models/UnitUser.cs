// Copyright 2024 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Blueprint.Api.Data.Models
{
    public class UnitUserEntity
    {
        public UnitUserEntity() { }

        public UnitUserEntity(Guid userId, Guid unitId)
        {
            UserId = userId;
            UnitId = unitId;
        }

        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public Guid Id { get; set; }

        public Guid UserId { get; set; }
        public UserEntity User { get; set; }

        public Guid UnitId { get; set; }
        public UnitEntity Unit { get; set; }
    }

    public class UnitUserConfiguration : IEntityTypeConfiguration<UnitUserEntity>
    {
        public void Configure(EntityTypeBuilder<UnitUserEntity> builder)
        {
            builder.HasIndex(x => new { x.UserId, x.UnitId }).IsUnique();

            builder
                .HasOne(u => u.User)
                .WithMany(p => p.UnitUsers)
                .HasForeignKey(x => x.UserId);
            builder
                .HasOne(u => u.Unit)
                .WithMany(p => p.UnitUsers)
                .HasForeignKey(x => x.UnitId);
        }
    }
}

