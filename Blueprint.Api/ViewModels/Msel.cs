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
        public string Description { get; set; }
        public ItemStatus Status { get; set; }
        public Guid? TeamId { get; set; }
        public virtual Team Team { get; set; }
        public Guid? GalleryExhibitId { get; set; }
        public Guid? CiteEvaluationId { get; set; }
        public Guid? SteamfitterScenarioId { get; set; }
        public bool IsTemplate { get; set; }
        public virtual ICollection<Move> Moves { get; set; } = new HashSet<Move>();
        public virtual ICollection<DataField> DataFields { get; set; } = new HashSet<DataField>();
        public virtual ICollection<ScenarioEvent> ScenarioEvents { get; set; } = new HashSet<ScenarioEvent>();
   }
}

