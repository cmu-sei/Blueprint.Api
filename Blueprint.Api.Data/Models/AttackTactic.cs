// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Blueprint.Api.Data.Models
{
    /// <summary>An ATT&amp;CK tactic (TA0002 Execution) - the column a technique sits under in the matrix.</summary>
    public class AttackTacticEntity
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.None)]
        public Guid Id { get; set; }
        public Guid AttackReleaseId { get; set; }
        public AttackReleaseEntity AttackRelease { get; set; }
        public string StixId { get; set; }
        public string ExternalId { get; set; }
        /// <summary>The kill chain phase name techniques use to point at this tactic, e.g. "execution".</summary>
        public string ShortName { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public string Url { get; set; }
        /// <summary>The tactic's column in the release's matrix, left to right.</summary>
        public int DisplayOrder { get; set; }
    }

    public class AttackTacticConfiguration : IEntityTypeConfiguration<AttackTacticEntity>
    {
        public void Configure(EntityTypeBuilder<AttackTacticEntity> builder)
        {
            builder.HasIndex(x => new { x.AttackReleaseId, x.ExternalId }).IsUnique();

            builder
                .HasOne(x => x.AttackRelease)
                .WithMany(x => x.Tactics)
                .HasForeignKey(x => x.AttackReleaseId)
                .OnDelete(DeleteBehavior.Cascade);
        }
    }
}
