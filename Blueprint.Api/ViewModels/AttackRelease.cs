// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;

namespace Blueprint.Api.ViewModels
{
    public class AttackRelease : Base
    {
        public Guid Id { get; set; }
        public string Domain { get; set; }
        public string Version { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public DateTime? ReleaseDate { get; set; }
        public int TacticCount { get; set; }
        public int TechniqueCount { get; set; }
    }

    public class AttackTactic
    {
        public Guid Id { get; set; }
        public Guid AttackReleaseId { get; set; }
        public string ExternalId { get; set; }
        public string ShortName { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public string Url { get; set; }
        public int DisplayOrder { get; set; }
    }

    public class AttackTechnique
    {
        public Guid Id { get; set; }
        public Guid AttackReleaseId { get; set; }
        public string ExternalId { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public string Url { get; set; }
        public Guid? ParentId { get; set; }
        public bool IsSubTechnique { get; set; }
        /// <summary>The short names of the tactics the technique serves, e.g. "execution".</summary>
        public List<string> Tactics { get; set; } = new List<string>();
        public List<string> Platforms { get; set; } = new List<string>();
    }
}
