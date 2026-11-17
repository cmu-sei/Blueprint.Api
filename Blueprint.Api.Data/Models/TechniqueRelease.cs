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
    /// One imported technique framework release: MITRE ATT&amp;CK (e.g. enterprise-attack 17.1), ATLAS, or D3FEND.
    /// Releases are reference data: they are uploaded by an administrator and never fetched or updated in place, so
    /// a MSEL keeps the technique names and descriptions it was planned against until it is explicitly upgraded.
    /// </summary>
    /// <remarks>
    /// Ids are derived from the domain, the version and the STIX id rather than generated, so the same release
    /// has the same ids on every installation and a MSEL exported from one Blueprint links its techniques on
    /// another.
    /// </remarks>
    public class TechniqueReleaseEntity : BaseEntity
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.None)]
        public Guid Id { get; set; }
        public string Domain { get; set; }
        /// <summary>
        /// Whether the release's techniques are defences (MITRE D3FEND) rather than attacks (ATT&amp;CK, ATLAS). A MSEL
        /// pools the two families apart; the parser of each file format says which it reads.
        /// </summary>
        public bool IsDefensive { get; set; }
        public string Version { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public DateTime? ReleaseDate { get; set; }
        /// <summary>Who publishes the release (MITRE ATT&amp;CK, MITRE D3FEND); null for a release whose publisher is not known.</summary>
        public string Source { get; set; }
        /// <summary>The publisher's website for the framework.</summary>
        public string SourceUrl { get; set; }
        /// <summary>
        /// The copyright and trademark notice the publisher's terms ask to be shown wherever the release's content is,
        /// one line per sentence group. Null for a release that carries none, which then shows no notice.
        /// </summary>
        public string Notice { get; set; }
        /// <summary>The publisher's terms of use for the release's content.</summary>
        public string TermsUrl { get; set; }
        /// <summary>What the release calls its groups of techniques: "Tactics" (the default) or "Categories" (CAPEC).</summary>
        public string GroupLabel { get; set; } = "Tactics";
        public virtual ICollection<TechniqueTacticEntity> Tactics { get; set; } = new HashSet<TechniqueTacticEntity>();
        public virtual ICollection<TechniqueEntity> Techniques { get; set; } = new HashSet<TechniqueEntity>();
        /// <summary>The D3FEND "countered by" mappings imported onto a defensive release; empty for an attack release.</summary>
        public virtual ICollection<TechniqueCountermeasureEntity> Countermeasures { get; set; } = new HashSet<TechniqueCountermeasureEntity>();
    }

    public class TechniqueReleaseConfiguration : IEntityTypeConfiguration<TechniqueReleaseEntity>
    {
        public void Configure(EntityTypeBuilder<TechniqueReleaseEntity> builder)
        {
            builder.HasIndex(x => new { x.Domain, x.Version }).IsUnique();
        }
    }
}
