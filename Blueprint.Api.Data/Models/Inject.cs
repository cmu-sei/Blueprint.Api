// Copyright 2024 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Blueprint.Api.Data.Models
{
    public class InjectEntity : BaseEntity
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public Guid Id { get; set; }
        public string Name { get; set;}
        public string Description { get; set;}
        public Guid InjectTypeId { get; set; }
        public InjectTypeEntity InjectType { get; set; }
        public Guid? RequiresInjectId { get; set; }
        public virtual InjectEntity RequiresInject { get; set; }
        public virtual ICollection<DataValueEntity> DataValues { get; set; } = new HashSet<DataValueEntity>();
        public virtual ICollection<CatalogInjectEntity> CatalogInjects { get; set; } = new HashSet<CatalogInjectEntity>();
    }

}
