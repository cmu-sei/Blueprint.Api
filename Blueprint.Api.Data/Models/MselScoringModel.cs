// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Blueprint.Api.Data.Attributes;
using Blueprint.Api.Data.Enumerations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Blueprint.Api.Data.Models
{
    // A CITE scoring model the MSEL authors itself. The fields are CITE's ScoringModel, field for field;
    // the push creates it in CITE for the MSEL's evaluation. A MSEL has at most one.
    public class MselScoringModelEntity : BaseEntity
    {
        [Key]
        public Guid MselId { get; set; }
        public string Description { get; set; }
        public string CalculationEquation { get; set; }
        public bool HideScoresOnScoreSheet { get; set; }
        public bool DisplayCommentTextBoxes { get; set; }
        public bool DisplayScoringModelByMoveNumber { get; set; }
        public bool ShowPastSituationDescriptions { get; set; }
        public bool UseSubmit { get; set; }
        public bool UseUserScore { get; set; }
        public bool UseTeamScore { get; set; }
        public bool UseTeamAverageScore { get; set; }
        public bool UseTypeAverageScore { get; set; }
        public bool UseOfficialScore { get; set; }
        public CiteRightSideDisplay RightSideDisplay { get; set; }

        [SanitizeHtml]
        public string RightSideHtmlBlock { get; set; }

        public string RightSideEmbeddedUrl { get; set; }
        public virtual ICollection<MselScoringCategoryEntity> Categories { get; set; } = new HashSet<MselScoringCategoryEntity>();
    }

    public class MselScoringCategoryEntity : BaseEntity
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public Guid Id { get; set; }
        public Guid MselScoringModelId { get; set; }
        public int DisplayOrder { get; set; }
        public string Description { get; set; }
        public string CalculationEquation { get; set; }
        public bool IsModifierRequired { get; set; }
        public double ScoringWeight { get; set; }
        public int MoveNumberFirstDisplay { get; set; }
        public int MoveNumberLastDisplay { get; set; }
        public CiteScoringOptionSelection ScoringOptionSelection { get; set; }
        public virtual ICollection<MselScoringOptionEntity> Options { get; set; } = new HashSet<MselScoringOptionEntity>();
    }

    public class MselScoringOptionEntity : BaseEntity
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public Guid Id { get; set; }
        public Guid MselScoringCategoryId { get; set; }
        public int DisplayOrder { get; set; }
        public string Description { get; set; }
        public bool IsModifier { get; set; }
        public double Value { get; set; }
    }

    public class MselScoringModelConfiguration : IEntityTypeConfiguration<MselScoringModelEntity>
    {
        public void Configure(EntityTypeBuilder<MselScoringModelEntity> builder)
        {
            builder
                .HasOne<MselEntity>()
                .WithOne(m => m.ScoringModel)
                .HasForeignKey<MselScoringModelEntity>(x => x.MselId)
                .OnDelete(DeleteBehavior.Cascade);
            builder
                .HasMany(x => x.Categories)
                .WithOne()
                .HasForeignKey(x => x.MselScoringModelId)
                .OnDelete(DeleteBehavior.Cascade);
        }
    }

    public class MselScoringCategoryConfiguration : IEntityTypeConfiguration<MselScoringCategoryEntity>
    {
        public void Configure(EntityTypeBuilder<MselScoringCategoryEntity> builder)
        {
            builder
                .HasMany(x => x.Options)
                .WithOne()
                .HasForeignKey(x => x.MselScoringCategoryId)
                .OnDelete(DeleteBehavior.Cascade);
        }
    }
}
