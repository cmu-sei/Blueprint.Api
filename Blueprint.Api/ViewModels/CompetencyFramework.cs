// Copyright 2025 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using System;
using System.Collections.Generic;

namespace Blueprint.Api.ViewModels
{
    public class CompetencyFramework : Base
    {
        public Guid Id { get; set; }
        public string Name { get; set; }
        public string Version { get; set; }
        public string Source { get; set; }
        public string Description { get; set; }
        public virtual ICollection<CompetencyElement> CompetencyElements { get; set; } = new HashSet<CompetencyElement>();
        public virtual ICollection<ProficiencyScale> ProficiencyScales { get; set; } = new HashSet<ProficiencyScale>();
    }
}
