// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;

namespace Blueprint.Api.ViewModels
{
    /// <summary>
    /// The copy of a Caster directory a MSEL carries, read only. A MSEL that links a Caster directory refreshes
    /// it on every JSON download; a push to a Caster without the directory imports it as a new project.
    /// </summary>
    public class MselCasterExport
    {
        public Guid MselId { get; set; }
        /// <summary>The Caster project the directory was exported from; an import creates a project by this name.</summary>
        public string ProjectName { get; set; }
        public Guid? SourceProjectId { get; set; }
        public Guid? SourceDirectoryId { get; set; }
        public DateTime DateExported { get; set; }
        public Guid? ExportedBy { get; set; }
        public string ExportedByName { get; set; }
        /// <summary>From the project's root directory down to the exported directory, which is last.</summary>
        public List<MselCasterExportDirectory> Directories { get; set; } = new List<MselCasterExportDirectory>();
        /// <summary>The exported directory's contents as Caster exported them, by their paths in the export.</summary>
        public List<MselCasterExportEntry> Entries { get; set; } = new List<MselCasterExportEntry>();
        /// <summary>Whether the files' contents are included. A workspace's files can hold credentials, so only those
        /// who may change the copy read them; other contributors get the paths alone.</summary>
        public bool ContentsIncluded { get; set; }
                /// <summary>What the copy could not carry, when it was just taken from Caster.</summary>
        public List<string> Warnings { get; set; } = new List<string>();
    }

    public class MselCasterExportDirectory
    {
        /// <summary>0 for the project's root directory.</summary>
        public int Level { get; set; }
        public string Name { get; set; }
        public string TerraformVersion { get; set; }
        public int? Parallelism { get; set; }
        public bool AzureDestroyFailureThresholdEnabled { get; set; }
        public int? AzureDestroyFailureThreshold { get; set; }
        /// <summary>A directory above the exported one: its own shared files. The exported directory's are in Entries.</summary>
        public List<MselCasterExportFile> Files { get; set; } = new List<MselCasterExportFile>();
    }

    public class MselCasterExportFile
    {
        public string Name { get; set; }
        public string Content { get; set; }
    }

    public class MselCasterExportEntry
    {
        /// <summary>The path in the export, relative to the exported directory.</summary>
        public string Path { get; set; }
        /// <summary>The workspace the file belongs to, or null for a directory's shared file.</summary>
        public string Workspace { get; set; }
        public string Content { get; set; }
    }

    /// <summary>The Caster directory an import of a MSEL's Caster copy created, and what Caster did not import.</summary>
    public class MselCasterImportResult
    {
        public Msel Msel { get; set; }
        public Guid CasterDirectoryId { get; set; }
        public List<string> LockedFiles { get; set; } = new List<string>();
    }
}
