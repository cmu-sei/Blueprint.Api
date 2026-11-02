// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;

namespace Blueprint.Api.ViewModels
{
    /// <summary>A D3FEND "countered by" mapping: the offensive technique id and the D3FEND technique that counters it.</summary>
    public class AttackTechniqueCountermeasure
    {
        public Guid Id { get; set; }
        /// <summary>The D3FEND release the mapping belongs to.</summary>
        public Guid AttackReleaseId { get; set; }
        /// <summary>The offensive technique's id (T1003.005), whichever release of its framework the MSEL uses.</summary>
        public string OffensiveExternalId { get; set; }
        public Guid DefensiveTechniqueId { get; set; }
        public AttackTechnique DefensiveTechnique { get; set; }
        /// <summary>The D3FEND tactic label the mapping is filed under: Model, Harden, Detect, Isolate, Deceive, Evict or Restore.</summary>
        public string DefensiveTactic { get; set; }
        /// <summary>The most common artifact path behind the pair, e.g. "hardens Credential; T1003.005 accesses Encrypted Credential".</summary>
        public string Rationale { get; set; }
        /// <summary>The number of distinct artifact paths that link the pair; the list is ranked by it.</summary>
        public int Weight { get; set; }
        /// <summary>True when the offensive id asked for had no mappings of its own and these are its parent technique's.</summary>
        public bool InheritedFromParent { get; set; }
    }

    /// <summary>What importing a d3fend-full-mappings.json onto a D3FEND release read and kept.</summary>
    public class AttackTechniqueCountermeasureImport
    {
        public Guid AttackReleaseId { get; set; }
        /// <summary>The result rows in the file, one per artifact path per queried technique.</summary>
        public int RowsRead { get; set; }
        /// <summary>The distinct (offensive id, D3FEND technique) pairs stored.</summary>
        public int PairsStored { get; set; }
        /// <summary>Pairs whose D3FEND class is not a defensive technique in this release, so they were left out.</summary>
        public int PairsSkipped { get; set; }
        /// <summary>The distinct offensive technique ids the stored pairs cover.</summary>
        public int OffensiveTechniqueCount { get; set; }
    }

    /// <summary>The countermeasures of every attack technique in a MSEL's pool, from one D3FEND release.</summary>
    public class MselCountermeasures
    {
        public Guid MselId { get; set; }
        /// <summary>
        /// The D3FEND release the mappings come from: the one the MSEL's defensive techniques are pinned to, else the
        /// newest with mappings imported. Null when no D3FEND release has mappings.
        /// </summary>
        public Guid? AttackReleaseId { get; set; }
        /// <summary>True when that release has mappings imported; false when the pinned release has none yet.</summary>
        public bool HasMappings { get; set; }
        public List<MselTechniqueCountermeasures> Techniques { get; set; } = new List<MselTechniqueCountermeasures>();
    }

    /// <summary>The countermeasures of one pooled attack technique, ranked by weight.</summary>
    public class MselTechniqueCountermeasures
    {
        public Guid AttackTechniqueId { get; set; }
        public string OffensiveExternalId { get; set; }
        /// <summary>The parent technique's id when the technique has no mappings of its own and these are inherited; else null.</summary>
        public string InheritedFrom { get; set; }
        public List<AttackTechniqueCountermeasure> Countermeasures { get; set; } = new List<AttackTechniqueCountermeasure>();
    }
}
