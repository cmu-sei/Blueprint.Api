// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Blueprint.Api.Data.Enumerations;

namespace Blueprint.Api.Data.Models
{
    public class MselEntity : BaseEntity
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public Guid Id { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public MselItemStatus Status { get; set; }
        public bool UsePlayer { get; set; }
        public Guid? PlayerViewId { get; set; }
        public IntegrationType PlayerIntegrationType { get; set; }
        public bool UseGallery { get; set; }
        public Guid? GalleryCollectionId { get; set; }
        public Guid? GalleryExhibitId { get; set; }
        public IntegrationType GalleryIntegrationType { get; set; }
        public bool UseCite { get; set; }
        public Guid? CiteEvaluationId { get; set; }
        public Guid? CiteScoringModelId { get; set; }
        public IntegrationType CiteIntegrationType { get; set; }
        public bool UseSteamfitter { get; set; }
        public Guid? SteamfitterScenarioId { get; set; }
        public IntegrationType SteamfitterIntegrationType { get; set; }
        public bool IsTemplate { get; set; }
        public DateTime StartTime { get; set; }
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
        public bool ShowDeliveryMethodOnScenarioEventList { get; set; }
        public bool ShowDeliveryMethodOnExerciseView { get; set; }
        public int DeliveryMethodDisplayOrder { get; set; }
        public virtual ICollection<MoveEntity> Moves { get; set; } = new HashSet<MoveEntity>();
        public virtual ICollection<DataFieldEntity> DataFields { get; set; } = new HashSet<DataFieldEntity>();
        public virtual ICollection<ScenarioEventEntity> ScenarioEvents { get; set; } = new HashSet<ScenarioEventEntity>();
        public virtual ICollection<TeamEntity> Teams { get; set; } = new HashSet<TeamEntity>();
        public virtual ICollection<MselUnitEntity> MselUnits { get; set; } = new HashSet<MselUnitEntity>();
        public virtual ICollection<OrganizationEntity> Organizations { get; set; } = new HashSet<OrganizationEntity>();
        public virtual ICollection<UserMselRoleEntity> UserMselRoles { get; set; } = new HashSet<UserMselRoleEntity>();
        public string HeaderRowMetadata { get; set; }
        public virtual ICollection<CardEntity> Cards { get; set; } = new HashSet<CardEntity>();
        public virtual ICollection<CiteRoleEntity> CiteRoles { get; set; } = new HashSet<CiteRoleEntity>();
        public virtual ICollection<CiteActionEntity> CiteActions { get; set; } = new HashSet<CiteActionEntity>();
        public virtual ICollection<PlayerApplicationEntity> PlayerApplications { get; set; } = new HashSet<PlayerApplicationEntity>();
        public virtual ICollection<MselPageEntity> Pages { get; set; } = new HashSet<MselPageEntity>();
        public virtual ICollection<InvitationEntity> Invitations { get; set; } = new HashSet<InvitationEntity>();
    }
}
