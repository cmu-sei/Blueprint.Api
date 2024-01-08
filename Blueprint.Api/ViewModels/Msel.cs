// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using System;
using System.Collections.Generic;
using Blueprint.Api.Data.Enumerations;

namespace Blueprint.Api.ViewModels
{
    public class Msel : Base
    {
        public Guid Id { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public ItemStatus Status { get; set; }
        public Guid? PlayerViewId { get; set; }
        public bool UseGallery { get; set; }
        public Guid? GalleryCollectionId { get; set; }
        public Guid? GalleryExhibitId { get; set; }
        public bool UseCite { get; set; }
        public Guid? CiteEvaluationId { get; set; }
        public Guid? CiteScoringModelId { get; set; }
        public bool UseSteamfitter { get; set; }
        public Guid? SteamfitterScenarioId { get; set; }
        public bool IsTemplate { get; set; }
        public DateTime StartTime { get; set; }
        public int DurationSeconds { get; set; }
        public bool ShowTimeOnScenarioEventList { get; set; }
        public bool ShowTimeOnExerciseView { get; set; }
        public bool ShowMoveOnScenarioEventList { get; set; }
        public bool ShowMoveOnExerciseView { get; set; }
        public bool ShowGroupOnScenarioEventList { get; set; }
        public bool ShowGroupOnExerciseView { get; set; }
        public virtual ICollection<Move> Moves { get; set; } = new HashSet<Move>();
        public virtual ICollection<DataField> DataFields { get; set; } = new HashSet<DataField>();
        public virtual ICollection<ScenarioEvent> ScenarioEvents { get; set; } = new HashSet<ScenarioEvent>();
        public ICollection<Team> Teams { get; set; } = new List<Team>();
        public ICollection<UserMselRole> UserMselRoles { get; set; } = new List<UserMselRole>();
        public string HeaderRowMetadata { get; set; }
        public virtual ICollection<Organization> Organizations { get; set; } = new HashSet<Organization>();
        public ICollection<Card> Cards { get; set; } = new List<Card>();
        public List<string> GalleryArticleParameters { get; set; } = new List<string>(); // the parameters that must be sent to Gallery to define an Article
        public List<string> GallerySourceTypes { get; set; } = new List<string>(); // the source types that must be sent to Gallery to define an Article
        public virtual ICollection<CiteRole> CiteRoles { get; set; } = new HashSet<CiteRole>();
        public virtual ICollection<CiteAction> CiteActions { get; set; } = new HashSet<CiteAction>();
        public virtual ICollection<PlayerApplication> PlayerApplications { get; set; } = new HashSet<PlayerApplication>();
        public ICollection<MselPage> Pages { get; set; } = new List<MselPage>();
   }
}

