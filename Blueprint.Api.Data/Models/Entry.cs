// Copyright 2023 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System.Collections.Generic;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace Blueprint.Api.Data.Models
{
    public class Entry
    {
        public Entry(EntityEntry entry)
        {
            Entity = entry.Entity;
            State = entry.State;
            Properties = entry.Properties;
        }

        public object Entity { get; set; }
        public EntityState State { get; set; }
        public IEnumerable<PropertyEntry> Properties { get; set; }
    }
}
