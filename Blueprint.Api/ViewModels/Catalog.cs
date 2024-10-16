// Copyright 2024 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using System;
using System.Collections.Generic;

namespace Blueprint.Api.ViewModels
{
    public class Catalog : Base
    {
        public Guid Id { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public Guid InjectTypeId { get; set; }
        public bool IsPublic { get; set; }
        public Guid? ParentId { get; set; }
        public Catalog Parent { get; set; }
        public virtual ICollection<Unit> Units { get; set; } = new HashSet<Unit>();
        public string ListDataFields { get; set; }  // comma separated list of data field IDs to show in the list of injects
    }
}
