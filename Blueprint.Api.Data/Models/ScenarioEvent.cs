// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Blueprint.Api.Data.Enumerations;

namespace Blueprint.Api.Data.Models
{
    public class ScenarioEventEntity : BaseEntity
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public Guid Id { get; set; }
        public int MoveNumber { get; set; }
        public string Time { get; set; }
        public Guid MselId { get; set; }
        public virtual MselEntity Msel { get; set; }
        public ItemStatus Status { get; set; }
        public virtual ICollection<DataValueEntity> DataValues { get; set; } = new HashSet<DataValueEntity>();
    }

}

