// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Blueprint.Api.Data.Models
{
    public class MoveEntity : BaseEntity
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public Guid Id { get; set; }
        public int MoveNumber { get; set; }
        public string Title { get; set; }
        public string Description { get; set; }
        public DateTime? MoveStartTime { get; set; }
        public DateTime? MoveStopTime { get; set; }
        public DateTime? SituationTime { get; set; }
        public string SituationDescription { get; set; }
        public Guid MselId { get; set; }
        public virtual MselEntity Msel { get; set; }
    }
    public class MoveEntityConfiguration : IEntityTypeConfiguration<MoveEntity>
    {
        public void Configure(EntityTypeBuilder<MoveEntity> builder)
        {
            builder
                .HasIndex(move => new { move.MselId, move.MoveNumber }).IsUnique();
            builder
                .HasOne(u => u.Msel)
                .WithMany(p => p.Moves)
                .HasForeignKey(x => x.MselId)
                .OnDelete(DeleteBehavior.Cascade);
        }
    }

}

