// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;

namespace Blueprint.Api.ViewModels
{
    /// <summary>
    /// A Caster directory a MSEL can deploy its topology from, with the project it belongs to.
    /// </summary>
    public class CasterDirectory
    {
        public Guid Id { get; set; }
        public string Name { get; set; }
        public Guid ProjectId { get; set; }
        public string ProjectName { get; set; }
        public Guid? ParentId { get; set; }
    }
}
