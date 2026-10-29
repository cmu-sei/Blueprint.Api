// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Blueprint.Api.Data.Models
{
    /// <summary>
    /// One call to an AI model and what it used, so the cost of building each MSEL is known. A call made while
    /// drafting a MSEL that does not exist yet carries the draft's ID, and takes the MSEL's when it is created.
    /// </summary>
    public class AiUsageEntity : BaseEntity
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public Guid Id { get; set; }
        public Guid? MselId { get; set; }
        public virtual MselEntity Msel { get; set; }
        public Guid? DraftId { get; set; }
        /// <summary>What the call was for: draft, article, profile, value, assist or photo</summary>
        public string Operation { get; set; }
        public string Model { get; set; }
        public int InputTokens { get; set; }
        public int OutputTokens { get; set; }
        public int CacheReadTokens { get; set; }
        public int CacheWriteTokens { get; set; }
        public int Images { get; set; }
        /// <summary>Why the model stopped: tool_use, refusal, max_tokens... A refused call is billed too.</summary>
        public string StopReason { get; set; }
    }

    public class AiUsageConfiguration : IEntityTypeConfiguration<AiUsageEntity>
    {
        public void Configure(EntityTypeBuilder<AiUsageEntity> builder)
        {
            builder.HasIndex(u => u.MselId);
            builder.HasIndex(u => u.DraftId);
            // what an exercise cost stays known after the MSEL is deleted
            builder
                .HasOne(u => u.Msel)
                .WithMany()
                .HasForeignKey(u => u.MselId)
                .OnDelete(DeleteBehavior.SetNull);
        }
    }
}
