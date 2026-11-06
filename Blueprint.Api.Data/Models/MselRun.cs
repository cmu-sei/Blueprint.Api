// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Blueprint.Api.Data.Enumerations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Blueprint.Api.Data.Models
{
    // One push of a MSEL: the ids it created in the integrated applications and when it ran. The pull
    // deletes what the ids name but never clears them here, so the run's xAPI statements can still be found.
    public class MselRunEntity : BaseEntity
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public Guid Id { get; set; }
        public Guid MselId { get; set; }
        public string Name { get; set; }
        public MselRunStatus Status { get; set; }
        public Guid? PlayerViewId { get; set; }
        public Guid? CiteEvaluationId { get; set; }
        public Guid? CiteScoringModelId { get; set; }
        public Guid? GalleryCollectionId { get; set; }
        public Guid? GalleryExhibitId { get; set; }
        public Guid? SteamfitterScenarioId { get; set; }
        public Guid? CasterWorkspaceId { get; set; }
        public Guid? CasterRunId { get; set; }
        // when the push completed, and when the run was ended or its integrations removed
        public DateTime? ActualStartTime { get; set; }
        public DateTime? ActualEndTime { get; set; }
        // the MSEL's JSON export as it stood when the run ended
        public string MselSnapshotJson { get; set; }
        // the run's xAPI statements as exported before its integrations were removed
        public string RecordJson { get; set; }
        // each team's CITE submissions as they stood when the run ended
        public string CiteSubmissionsJson { get; set; }
        // per-source statement counts and the last statement time from the end-of-event record check
        public string Statistics { get; set; }
    }

    public class MselRunConfiguration : IEntityTypeConfiguration<MselRunEntity>
    {
        public void Configure(EntityTypeBuilder<MselRunEntity> builder)
        {
            // no navigation on MselEntity, so the MSEL's copy, export and view model do not carry its runs
            builder
                .HasOne<MselEntity>()
                .WithMany()
                .HasForeignKey(x => x.MselId)
                .OnDelete(DeleteBehavior.Cascade);
        }
    }
}
