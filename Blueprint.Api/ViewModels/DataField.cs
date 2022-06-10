// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Blueprint.Api.Data.Enumerations;

namespace Blueprint.Api.ViewModels
{
    public class DataField : Base
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public Guid Id { get; set; }
        public Guid MselId { get; set; }
        public string Name { get; set; }
        public DataFieldType DataType { get; set; }
        public int DisplayOrder { get; set; }
        public bool IsChosenFromList { get; set; }
        public virtual ICollection<DataOption> DataOptions { get; set; } = new HashSet<DataOption>();
        public string CellMetadata { get; set; }
        public string ColumnMetadata { get; set; }

    }

}

