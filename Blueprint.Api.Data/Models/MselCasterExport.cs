// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Blueprint.Api.Data.Models
{
    // A copy of the Caster directory a MSEL deploys, so the MSEL carries its topology to another Caster.
    // A workspace runs its directory's files and those of every directory above it, so the copy holds the
    // whole chain: each directory above, with its own shared files and settings, and the directory itself
    // as Caster exports it (Archive). A push to a Caster without the directory imports the copy as a new
    // project. A MSEL has at most one; an export of a MSEL that links a directory replaces it.
    public class MselCasterExportEntity : BaseEntity
    {
        [Key]
        public Guid MselId { get; set; }
        public Guid? SourceProjectId { get; set; }
        public string ProjectName { get; set; }
        public Guid? SourceDirectoryId { get; set; }
        // the directory's own export from Caster: a zip of its files, child directories and workspaces
        public byte[] Archive { get; set; }
        public DateTime DateExported { get; set; }
        public Guid? ExportedBy { get; set; }
        // the chain from the project's root directory (Level 0) down to the exported directory, which is last
        public virtual ICollection<MselCasterExportDirectoryEntity> Directories { get; set; } = new HashSet<MselCasterExportDirectoryEntity>();
    }

    public class MselCasterExportDirectoryEntity : BaseEntity
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public Guid Id { get; set; }
        public Guid MselCasterExportId { get; set; }
        public int Level { get; set; }
        public string Name { get; set; }
        public string TerraformVersion { get; set; }
        public int? Parallelism { get; set; }
        public bool AzureDestroyFailureThresholdEnabled { get; set; }
        public int? AzureDestroyFailureThreshold { get; set; }
        // a directory above the exported one has its shared files here; the exported one's are in the archive
        public virtual ICollection<MselCasterExportFileEntity> Files { get; set; } = new HashSet<MselCasterExportFileEntity>();
    }

    public class MselCasterExportFileEntity : BaseEntity
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public Guid Id { get; set; }
        public Guid MselCasterExportDirectoryId { get; set; }
        public string Name { get; set; }
        public string Content { get; set; }
    }

    public class MselCasterExportConfiguration : IEntityTypeConfiguration<MselCasterExportEntity>
    {
        public void Configure(EntityTypeBuilder<MselCasterExportEntity> builder)
        {
            builder
                .HasOne<MselEntity>()
                .WithOne(m => m.CasterExport)
                .HasForeignKey<MselCasterExportEntity>(x => x.MselId)
                .OnDelete(DeleteBehavior.Cascade);
            builder
                .HasMany(x => x.Directories)
                .WithOne()
                .HasForeignKey(x => x.MselCasterExportId)
                .OnDelete(DeleteBehavior.Cascade);
        }
    }

    public class MselCasterExportDirectoryConfiguration : IEntityTypeConfiguration<MselCasterExportDirectoryEntity>
    {
        public void Configure(EntityTypeBuilder<MselCasterExportDirectoryEntity> builder)
        {
            builder
                .HasMany(x => x.Files)
                .WithOne()
                .HasForeignKey(x => x.MselCasterExportDirectoryId)
                .OnDelete(DeleteBehavior.Cascade);
        }
    }
}
