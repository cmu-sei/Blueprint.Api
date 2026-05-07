// Copyright 2024 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using System.Collections.Generic;

namespace Blueprint.Api.ViewModels
{
    public class CompetencyFrameworkImportPreview
    {
        public string Source { get; set; }
        public string Version { get; set; }
        public string FrameworkName { get; set; }
        public List<ElementTypeCount> ElementTypeCounts { get; set; } = new List<ElementTypeCount>();
        public int TotalElements { get; set; }
        public int TotalRelationships { get; set; }
        public string Error { get; set; }
    }

    public class ElementTypeCount
    {
        public string Type { get; set; }
        public int Count { get; set; }
    }
}
