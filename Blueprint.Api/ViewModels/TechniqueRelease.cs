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
        /// <summary>Whether the release's techniques are defences rather than attacks.</summary>
        public bool IsDefensive { get; set; }
        public string Version { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public DateTime? ReleaseDate { get; set; }
        /// <summary>Who publishes the release; null when it is not known.</summary>
        public string Source { get; set; }
        public string SourceUrl { get; set; }
        /// <summary>The copyright and trademark notice to show with the release's content, one line per sentence group; null when it carries none.</summary>
        public string Notice { get; set; }
        public string TermsUrl { get; set; }
        /// <summary>What the release calls its groups of techniques: "Tactics" or "Categories".</summary>
        public string GroupLabel { get; set; }
        public int TacticCount { get; set; }
        public int TechniqueCount { get; set; }
        /// <summary>The "countered by" mappings imported onto the release; 0 for an attack release.</summary>
        public int CountermeasureCount { get; set; }
    }

    /// <summary>The notice fields of a release an administrator may edit; nothing else about a release changes.</summary>
    public class TechniqueReleaseNotice
    {
        /// <summary>Who publishes the release; blank for none.</summary>
        public string Source { get; set; }
        /// <summary>The publisher's website for the framework, an http or https address; blank for none.</summary>
        public string SourceUrl { get; set; }
        /// <summary>The copyright and trademark notice to show with the release's content, one line per sentence group; blank for none.</summary>
        public string Notice { get; set; }
        /// <summary>The publisher's terms of use, an http or https address; blank for none.</summary>
        public string TermsUrl { get; set; }
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
        /// <summary>The release's domain.</summary>
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
        /// <summary>The CWE ids the technique exploits, e.g. "CWE-89"; empty when the framework gives none.</summary>
        public List<string> Weaknesses { get; set; } = new List<string>();
        /// <summary>The technique's links to other catalogues; empty when the framework gives none.</summary>
        public List<TechniqueLink> ReferenceLinks { get; set; } = new List<TechniqueLink>();
    }

    /// <summary>A link from a technique to the same attack in another catalogue.</summary>
    public class TechniqueLink
    {
        public string Source { get; set; }
        public string Label { get; set; }
        public string Url { get; set; }
    }
}
