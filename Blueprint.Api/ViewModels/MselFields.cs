// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.ComponentModel.DataAnnotations;
using Blueprint.Api.Data.Enumerations;

namespace Blueprint.Api.ViewModels
{
    /// <summary>
    /// The fields a MSEL's create and edit requests share. The integration IDs, the integration status
    /// and the MSEL's child collections are not among them: the push and pull pipeline owns the first
    /// two, and each child has its own endpoints.
    /// </summary>
    public abstract class MselFields
    {
        // A MSEL must be named. Required rejects null, "" and whitespace-only values (it trims
        // before testing), which ValidateModelStateFilter turns into a 400.
        [Required]
        public string Name { get; set; }
        public string Description { get; set; }
        /// <summary>
        /// Pushing, Pulling and Deployed are set only by the integration pipeline, and a MSEL in one of
        /// them keeps it until the pipeline moves it on.
        /// </summary>
        public MselItemStatus Status { get; set; }
        public bool UsePlayer { get; set; }
        public IntegrationType PlayerIntegrationType { get; set; }
        public bool UseGallery { get; set; }
        public IntegrationType GalleryIntegrationType { get; set; }
        public bool UseCite { get; set; }
        public Guid? CiteScoringModelId { get; set; }
        /// <summary>The banner Blueprint puts on the MSEL's Gallery articles: None, Exercise (the default), Tlp or Classification</summary>
        public string ArticleMarking { get; set; }
        /// <summary>The TLP level (CLEAR, GREEN, AMBER, AMBER+STRICT, RED), or the classification banner's text</summary>
        public string ArticleMarkingText { get; set; }
        public IntegrationType CiteIntegrationType { get; set; }
        public bool UseSteamfitter { get; set; }
        public IntegrationType SteamfitterIntegrationType { get; set; }
        public bool UseCaster { get; set; }
        /// <summary>The Caster directory a push deploys from, which the caller must be able to view.</summary>
        public Guid? CasterDirectoryId { get; set; }
        public IntegrationType CasterIntegrationType { get; set; }
        public bool IsTemplate { get; set; }
        public DateTime StartTime { get; set; }

        // The exercise window is stored as StartTime plus a duration, so an end time that precedes
        // the start is exactly a negative DurationSeconds. Zero stays legal — it means no window
        // has been set yet.
        [Range(0, int.MaxValue, ErrorMessage = "The exercise duration cannot be negative; the end time must not precede the start time.")]
        public int DurationSeconds { get; set; }
        public bool ShowTimeOnScenarioEventList { get; set; }
        public bool ShowTimeOnExerciseView { get; set; }
        public int TimeDisplayOrder { get; set; }
        public bool ShowMoveOnScenarioEventList { get; set; }
        public bool ShowMoveOnExerciseView { get; set; }
        public int MoveDisplayOrder { get; set; }
        public bool ShowGroupOnScenarioEventList { get; set; }
        public bool ShowGroupOnExerciseView { get; set; }
        public int GroupDisplayOrder { get; set; }
        public bool ShowIntegrationTargetOnScenarioEventList { get; set; }
        public bool ShowIntegrationTargetOnExerciseView { get; set; }
        public int IntegrationTargetDisplayOrder { get; set; }
        public bool ShowTimeOnAssessorView { get; set; }
        public bool ShowMoveOnAssessorView { get; set; }
        public bool ShowGroupOnAssessorView { get; set; }
        public bool ShowIntegrationTargetOnAssessorView { get; set; }
        public string HeaderRowMetadata { get; set; }
        // IEEE 2881 (LMT) metadata fields
        public string EducationalLevel { get; set; }
        public string Subject { get; set; }
        public string Keywords { get; set; }
        public string EducationalUse { get; set; }
        public string CourseMode { get; set; }
        public string Language { get; set; }
    }

    public class MselCreate : MselFields
    {
        public Guid Id { get; set; }
    }

    public class MselEdit : MselFields
    {
    }
}
