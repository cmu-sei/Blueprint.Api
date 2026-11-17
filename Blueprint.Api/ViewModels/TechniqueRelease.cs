// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;

namespace Blueprint.Api.ViewModels
{
    public class TechniqueRelease : Base
    {
        public Guid Id { get; set; }
        public string Domain { get; set; }
        /// <summary>Whether the release's techniques are defences (MITRE D3FEND) rather than attacks (ATT&amp;CK, ATLAS).</summary>
        public bool IsDefensive { get; set; }
        public string Version { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public DateTime? ReleaseDate { get; set; }
        public int TacticCount { get; set; }
        public int TechniqueCount { get; set; }
        /// <summary>The D3FEND "countered by" mappings imported onto the release; 0 for an attack release.</summary>
        public int CountermeasureCount { get; set; }
    }

    public class TechniqueTactic
    {
        public Guid Id { get; set; }
        public Guid TechniqueReleaseId { get; set; }
        public string ExternalId { get; set; }
        public string ShortName { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public string Url { get; set; }
        public int DisplayOrder { get; set; }
    }

    public class Technique
    {
        public Guid Id { get; set; }
        public Guid TechniqueReleaseId { get; set; }
        /// <summary>The release's domain (enterprise-attack, ATLAS, d3fend).</summary>
        public string Domain { get; set; }
        /// <summary>Whether the technique is a defence (its release is defensive) rather than an attack.</summary>
        public bool IsDefensive { get; set; }
        public string ExternalId { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public string Url { get; set; }
        public Guid? ParentId { get; set; }
        public bool IsSubTechnique { get; set; }
        /// <summary>The short names of the tactics the technique serves, e.g. "execution".</summary>
        public List<string> Tactics { get; set; } = new List<string>();
        public List<string> Platforms { get; set; } = new List<string>();
        /// <summary>The CWE ids a CAPEC pattern exploits, e.g. "CWE-89"; empty for the other frameworks.</summary>
        public List<string> Weaknesses { get; set; } = new List<string>();
        /// <summary>A CAPEC pattern's links to OWASP and WASC; empty for the other frameworks.</summary>
        public List<TechniqueLink> ReferenceLinks { get; set; } = new List<TechniqueLink>();
    }

    /// <summary>A link from a technique to the same attack in another catalogue (OWASP Attacks, WASC).</summary>
    public class TechniqueLink
    {
        public string Source { get; set; }
        public string Label { get; set; }
        public string Url { get; set; }
    }
}
