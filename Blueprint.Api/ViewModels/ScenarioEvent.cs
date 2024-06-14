// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using System;
using System.Collections.Generic;
using Blueprint.Api.Data.Enumerations;

namespace Blueprint.Api.ViewModels
{
    public class ScenarioEvent : Base
    {
        public Guid Id { get; set; }
        public Guid MselId { get; set; }
        public virtual ICollection<DataValue> DataValues { get; set; } = new HashSet<DataValue>();
        public int GroupOrder { get; set; }
        public bool IsHidden { get; set; }
        public string RowMetadata { get; set; }
        public int DeltaSeconds { get; set; }     // time from the start of the MSEL when this event should be executed
        public EventType ScenarioEventType { get; set; }
        public string Description { get; set; }
        public Guid? InjectId { get; set; }
   }
}

