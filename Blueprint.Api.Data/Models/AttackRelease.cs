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
    /// <summary>
    /// One imported MITRE ATT&amp;CK release (e.g. enterprise-attack 17.1). Releases are reference data: they are
    /// uploaded by an administrator and never fetched or updated in place, so a MSEL keeps the technique
    /// names and descriptions it was planned against until it is explicitly upgraded.
    /// </summary>
    /// <remarks>
    /// Ids are derived from the domain, the version and the STIX id rather than generated, so the same release
    /// has the same ids on every installation and a MSEL exported from one Blueprint links its techniques on
    /// another.
    /// </remarks>
    public class AttackReleaseEntity : BaseEntity
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.None)]
        public Guid Id { get; set; }
        public string Domain { get; set; }
        public string Version { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public DateTime? ReleaseDate { get; set; }
        public virtual ICollection<AttackTacticEntity> Tactics { get; set; } = new HashSet<AttackTacticEntity>();
        public virtual ICollection<AttackTechniqueEntity> Techniques { get; set; } = new HashSet<AttackTechniqueEntity>();
    }

    public class AttackReleaseConfiguration : IEntityTypeConfiguration<AttackReleaseEntity>
    {
        public void Configure(EntityTypeBuilder<AttackReleaseEntity> builder)
        {
            builder.HasIndex(x => new { x.Domain, x.Version }).IsUnique();
        }
    }
}
