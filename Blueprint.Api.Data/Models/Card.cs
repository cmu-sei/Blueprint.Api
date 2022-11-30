// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Blueprint.Api.Data.Enumerations;

namespace Blueprint.Api.Data.Models
{
    public class CardEntity : BaseEntity
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public Guid Id { get; set; }
        public Guid MselId { get; set; }
        public virtual MselEntity Msel { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public int Move { get; set; }
        public int Inject { get; set; }
        public Guid? GalleryId { get; set; }
        public virtual ICollection<CardTeamEntity> CardTeams { get; set; } = new HashSet<CardTeamEntity>();
    }

}

