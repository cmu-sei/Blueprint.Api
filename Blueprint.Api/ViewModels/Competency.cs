// Copyright 2024 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using System;
using System.Collections.Generic;

namespace Blueprint.Api.ViewModels
{
    public class Competency : Base
    {
        public Guid Id { get; set; }
        public Guid CompetencyFrameworkId { get; set; }
        public Guid? ParentId { get; set; }
        public string IdNumber { get; set; }
        public string ShortName { get; set; }
        public string Description { get; set; }
        public int DescriptionFormat { get; set; }
        public string Path { get; set; }
        public int SortOrder { get; set; }
        public string RuleType { get; set; }
        public int RuleOutcome { get; set; }
        public string RuleConfig { get; set; }
        public string ScaleValues { get; set; }
        public string ScaleConfiguration { get; set; }
        public ICollection<Competency> Children { get; set; } = new HashSet<Competency>();
        public ICollection<string> RelatedIdNumbers { get; set; } = new List<string>();
    }
}
