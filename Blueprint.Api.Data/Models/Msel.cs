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
        public string Description { get; set; }
        public ItemStatus Status { get; set; }
        public Guid? GalleryExhibitId { get; set; }
        public Guid? CiteEvaluationId { get; set; }
        public Guid? SteamfitterScenarioId { get; set; }
        public bool IsTemplate { get; set; }
        public virtual ICollection<MoveEntity> Moves { get; set; } = new HashSet<MoveEntity>();
        public virtual ICollection<DataFieldEntity> DataFields { get; set; } = new HashSet<DataFieldEntity>();
        public virtual ICollection<ScenarioEventEntity> ScenarioEvents { get; set; } = new HashSet<ScenarioEventEntity>();
        public virtual ICollection<MselTeamEntity> MselTeams { get; set; } = new HashSet<MselTeamEntity>();
        public virtual ICollection<OrganizationEntity> Organizations { get; set; } = new HashSet<OrganizationEntity>();
        public virtual ICollection<UserMselRoleEntity> UserMselRoles { get; set; } = new HashSet<UserMselRoleEntity>();
        public string HeaderRowMetadata { get; set; }
    }
}

