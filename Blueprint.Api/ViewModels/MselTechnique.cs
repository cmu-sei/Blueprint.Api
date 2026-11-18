// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;

namespace Blueprint.Api.ViewModels
{
    public class MselTechnique
    {
        public Guid Id { get; set; }
        public Guid MselId { get; set; }
        public Guid TechniqueId { get; set; }
        public Technique Technique { get; set; }
    }

    /// <summary>What moving a MSEL's technique pool to another release kept and dropped.</summary>
    public class MselTechniqueUpgrade
    {
        public Guid MselId { get; set; }
        public Guid TechniqueReleaseId { get; set; }
        public int Kept { get; set; }
        /// <summary>The technique ids (T1059.001) the new release no longer has, revoked or deprecated.</summary>
        public List<string> Dropped { get; set; } = new List<string>();
    }
}
