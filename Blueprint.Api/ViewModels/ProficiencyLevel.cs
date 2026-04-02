// Copyright 2025 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using System;

namespace Blueprint.Api.ViewModels
{
    public class ProficiencyLevel : Base
    {
        public Guid Id { get; set; }
        public Guid ProficiencyScaleId { get; set; }
        public string Name { get; set; }
        public int Value { get; set; }
        public string Description { get; set; }
        public int DisplayOrder { get; set; }
    }
}
