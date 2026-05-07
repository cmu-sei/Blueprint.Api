// Copyright 2024 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using System;
using System.Collections.Generic;

namespace Blueprint.Api.ViewModels
{
    public class CompetencyFramework : Base
    {
        public Guid Id { get; set; }
        public string Name { get; set; }
        public string IdNumber { get; set; }
        public string Description { get; set; }
        public int DescriptionFormat { get; set; }
        public string Source { get; set; }
        public string Version { get; set; }
        public string ScaleValues { get; set; }
        public string ScaleConfiguration { get; set; }
        public string Taxonomies { get; set; }
        public Guid? DefaultProficiencyScaleId { get; set; }
        public ICollection<Competency> Competencies { get; set; } = new HashSet<Competency>();
    }
}
