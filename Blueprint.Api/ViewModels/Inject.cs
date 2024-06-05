// Copyright 2024 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using System;
using System.Collections.Generic;
using Blueprint.Api.Data.Enumerations;

namespace Blueprint.Api.ViewModels
{
    public class Inject : Base
    {
        public Guid Id { get; set; }
        public Guid InjectTypeId { get; set; }
        public Guid? RequiresInjectId { get; set; }
        public virtual Inject RequiresInject { get; set; }
        public virtual ICollection<DataValue> DataValues { get; set; } = new HashSet<DataValue>();
   }
}

