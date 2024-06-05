// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using System;

namespace Blueprint.Api.ViewModels
{
    public class DataValue : Base
    {
        public Guid Id { get; set; }
        public string Value { get; set; }
        public Guid? ScenarioEventId { get; set; }
        public Guid? InjectId { get; set; }
        public Guid DataFieldId { get; set; }
        public string CellMetadata { get; set; }
    }

}

