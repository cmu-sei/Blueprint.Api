// Copyright 2025 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using System;
using System.Collections.Generic;

namespace Blueprint.Api.ViewModels
{
    public class CompetencyElement : Base
    {
        public Guid Id { get; set; }
        public Guid CompetencyFrameworkId { get; set; }
        public string ElementIdentifier { get; set; }
        public string ElementType { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public Guid? ParentId { get; set; }
        public virtual ICollection<CompetencyElement> Children { get; set; } = new HashSet<CompetencyElement>();
    }
}
