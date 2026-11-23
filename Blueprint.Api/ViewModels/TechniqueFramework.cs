// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using Blueprint.Api.Data.Enumerations;
using Blueprint.Api.Data.Models;

namespace Blueprint.Api.ViewModels
{
    /// <summary>
    /// A technique framework definition: how its release files are recognised and read, and how their content is
    /// shown. An administrator creates, edits, imports and exports them; Blueprint ships none.
    /// </summary>
    public class TechniqueFramework : Base
    {
        public Guid Id { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        /// <summary>The domain its releases are filed under; unique across frameworks.</summary>
        public string Domain { get; set; }
        /// <summary>Whether its techniques are defences rather than attacks.</summary>
        public bool IsDefensive { get; set; }
        public TechniqueFileFormat Format { get; set; }
        public TechniqueFrameworkSettings Settings { get; set; } = new TechniqueFrameworkSettings();
        /// <summary>The releases read as this framework; read only.</summary>
        public int ReleaseCount { get; set; }
    }
}
