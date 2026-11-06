// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using Blueprint.Api.Data.Enumerations;

namespace Blueprint.Api.ViewModels
{
    // A run of a MSEL, without its stored snapshot, statement record and CITE submissions, which are
    // large and are read through their own routes.
    public class MselRun : Base
    {
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
        public DateTime? ActualStartTime { get; set; }
        public DateTime? ActualEndTime { get; set; }
        public string Statistics { get; set; }
    }
}
