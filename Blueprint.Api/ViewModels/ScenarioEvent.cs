// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Blueprint.Api.Data.Enumerations;

namespace Blueprint.Api.ViewModels
{
    public class ScenarioEvent : Base
    {
        public Guid Id { get; set; }
        public int MoveNumber { get; set; }
        public string Group { get; set; }
        public int ScenarioEventNumber { get; set; }
        public Guid MselId { get; set; }
        public ItemStatus Status { get; set; }
        public virtual ICollection<DataValue> DataValues { get; set; } = new HashSet<DataValue>();
   }
}

