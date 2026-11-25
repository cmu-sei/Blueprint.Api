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
    /// One imported release of a technique framework. Releases are uploaded by an administrator and never changed
    /// in place, so a MSEL keeps the techniques it was planned against until it is upgraded.
    /// </summary>
    /// <remarks>
    /// Ids are derived from the domain, version and source id, so a MSEL exported from one installation links its
    /// techniques on another.
    /// </remarks>
    public class TechniqueReleaseEntity : BaseEntity
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.None)]
        public Guid Id { get; set; }
        public string Domain { get; set; }
        /// <summary>The framework the release was read as; null once an administrator unlinks it.</summary>
        public Guid? TechniqueFrameworkId { get; set; }
        public TechniqueFrameworkEntity TechniqueFramework { get; set; }
        /// <summary>
        /// Whether the release's techniques are defences rather than attacks; a MSEL pools the two apart.
        /// </summary>
        public bool IsDefensive { get; set; }
        public string Version { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public DateTime? ReleaseDate { get; set; }
        /// <summary>Who publishes the release; null for a release whose publisher is not known.</summary>
        public string Source { get; set; }
        /// <summary>The publisher's website for the framework.</summary>
        public string SourceUrl { get; set; }
        /// <summary>
        /// The copyright and trademark notice to show with the release's content; null when it carries none.
        /// </summary>
        public string Notice { get; set; }
        /// <summary>The publisher's terms of use for the release's content.</summary>
        public string TermsUrl { get; set; }
        /// <summary>What the release calls its groups of techniques: "Tactics" (the default) or "Categories".</summary>
        public string GroupLabel { get; set; } = "Tactics";
        public virtual ICollection<TechniqueTacticEntity> Tactics { get; set; } = new HashSet<TechniqueTacticEntity>();
        public virtual ICollection<TechniqueEntity> Techniques { get; set; } = new HashSet<TechniqueEntity>();
        /// <summary>The mappings imported onto the release; empty when none have been.</summary>
        public virtual ICollection<TechniqueCountermeasureEntity> Countermeasures { get; set; } = new HashSet<TechniqueCountermeasureEntity>();
    }

    public class TechniqueReleaseConfiguration : IEntityTypeConfiguration<TechniqueReleaseEntity>
    {
        public void Configure(EntityTypeBuilder<TechniqueReleaseEntity> builder)
        {
            builder.HasIndex(x => new { x.Domain, x.Version }).IsUnique();

            builder
                .HasOne(x => x.TechniqueFramework)
                .WithMany(x => x.Releases)
                .HasForeignKey(x => x.TechniqueFrameworkId)
                .OnDelete(DeleteBehavior.SetNull);
        }
    }
}
