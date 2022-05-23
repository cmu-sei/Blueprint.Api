// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Blueprint.Api.Data.Models
{
    public class DataOptionEntity : BaseEntity
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public Guid Id { get; set; }
        public Guid DataFieldId { get; set; }
        public virtual DataFieldEntity DataField { get; set; }
        public string OptionName {get; set; }
        public string OptionValue {get; set; }
        public int DisplayOrder { get; set; }
    }

}

