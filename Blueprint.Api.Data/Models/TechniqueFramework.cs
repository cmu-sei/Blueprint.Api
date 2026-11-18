// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json;
using System.Text.Json.Serialization;
using Blueprint.Api.Data.Enumerations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Blueprint.Api.Data.Models
{
    /// <summary>
    /// A technique framework an administrator has defined: how to recognise its release files, how to read them,
    /// and how to show what they hold. Blueprint ships none; every framework is data.
    /// </summary>
    public class TechniqueFrameworkEntity : BaseEntity
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public Guid Id { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        /// <summary>The domain its releases are filed under; a MSEL pins one release per domain.</summary>
        public string Domain { get; set; }
        /// <summary>Whether its techniques are defences rather than attacks.</summary>
        public bool IsDefensive { get; set; }
        public TechniqueFileFormat Format { get; set; }
        public TechniqueFrameworkSettings Settings { get; set; } = new TechniqueFrameworkSettings();
        public virtual ICollection<TechniqueReleaseEntity> Releases { get; set; } = new HashSet<TechniqueReleaseEntity>();
    }

    /// <summary>Everything about a framework beyond its identity, stored as one JSON document.</summary>
    public class TechniqueFrameworkSettings
    {
        /// <summary>What a file must hold to be a release of this framework.</summary>
        public TechniqueFrameworkMatch Match { get; set; } = new TechniqueFrameworkMatch();
        /// <summary>How to read a STIX bundle; used when the format is a STIX bundle.</summary>
        public StixFormatOptions Stix { get; set; }
        /// <summary>How to read a JSON-LD ontology; used when the format is a JSON-LD ontology.</summary>
        public JsonLdFormatOptions JsonLd { get; set; }
        /// <summary>The technique mappings a release of this framework can import; null when it has none.</summary>
        public TechniqueMappingDefinition Mappings { get; set; }
        /// <summary>What the releases call their groups of techniques, such as "Tactics".</summary>
        public string GroupLabel { get; set; }
        /// <summary>Tactic names in the order they are shown, for lists that carry a tactic name without an order.</summary>
        public List<string> TacticOrder { get; set; } = new List<string>();
        /// <summary>A technique's page, with {id} for its id and {sourceId} for its id in the file; used when the file gives none.</summary>
        public string TechniqueUrlTemplate { get; set; }
        /// <summary>A tactic's page, with the same placeholders; used when the file gives none.</summary>
        public string TacticUrlTemplate { get; set; }
        /// <summary>A weakness's page, with {id} for the number after the weakness id's prefix and {fullId} for the whole id.</summary>
        public string WeaknessUrlTemplate { get; set; }
        /// <summary>The publisher's site, given to a release whose file names none.</summary>
        public string SourceUrl { get; set; }
        /// <summary>The terms of use, given to a release whose file names none.</summary>
        public string TermsUrl { get; set; }
        /// <summary>Where an administrator gets the release files, shown when nothing has been imported.</summary>
        public string DownloadHint { get; set; }
        public string DownloadUrl { get; set; }
    }

    /// <summary>
    /// The conditions a file must meet to be a release of a framework. Every condition given must hold; at least
    /// one must be given.
    /// </summary>
    public class TechniqueFrameworkMatch
    {
        /// <summary>STIX: the domain most of the bundle's techniques name first in the definition's domains property.</summary>
        public string StixDomain { get; set; }
        /// <summary>STIX: the source most techniques' first external reference names, the bundle's own catalogue.</summary>
        public string ReferenceSource { get; set; }
        /// <summary>STIX: the start of the ids in that reference.</summary>
        public string IdPrefix { get; set; }
        /// <summary>STIX: the name of the bundle's collection object, of the definition's collection type.</summary>
        public string CollectionName { get; set; }
        /// <summary>JSON-LD: a property some node of the graph carries.</summary>
        public string Property { get; set; }
        /// <summary>JSON-LD: text the ontology's dcterms:title contains.</summary>
        public string OntologyTitleContains { get; set; }

        [JsonIgnore]
        public bool IsEmpty =>
            string.IsNullOrWhiteSpace(StixDomain) && string.IsNullOrWhiteSpace(ReferenceSource)
            && string.IsNullOrWhiteSpace(IdPrefix) && string.IsNullOrWhiteSpace(CollectionName)
            && string.IsNullOrWhiteSpace(Property) && string.IsNullOrWhiteSpace(OntologyTitleContains);
    }

    /// <summary>How a STIX bundle's techniques are grouped.</summary>
    public enum StixGrouping
    {
        /// <summary>By the bundle's tactic objects, through each technique's kill chain phases.</summary>
        KillChain = 0,
        /// <summary>By the top level of a pattern hierarchy, for a bundle with no tactics.</summary>
        Hierarchy = 1
    }

    public class StixFormatOptions
    {
        /// <summary>The external reference source that carries an object's id; blank for the source most tactics name.</summary>
        public string IdSource { get; set; }
        /// <summary>Only ids that start with this; blank for any.</summary>
        public string IdPrefix { get; set; }
        public StixGrouping Grouping { get; set; }
        /// <summary>Kill chain: the object type of a tactic.</summary>
        public string TacticType { get; set; }
        /// <summary>Kill chain: the tactic property a technique's kill chain phase names match.</summary>
        public string TacticShortNameProperty { get; set; }
        /// <summary>The object type listing the tactics in order; blank for none, the tactics then in file order.</summary>
        public string MatrixType { get; set; }
        /// <summary>The matrix property listing its tactics' STIX ids in order.</summary>
        public string MatrixTacticRefsProperty { get; set; }
        /// <summary>The object type of the bundle's collection, which names the release; blank for none.</summary>
        public string CollectionType { get; set; }
        /// <summary>The collection property holding the release version.</summary>
        public string CollectionVersionProperty { get; set; }
        /// <summary>The technique property listing its domains, read by the StixDomain match condition.</summary>
        public string DomainsProperty { get; set; }
        /// <summary>The relationship type from a sub-technique to its technique; blank for no sub-techniques.</summary>
        public string SubTechniqueRelationship { get; set; }
        /// <summary>A true/false property marking an object deprecated, beside the STIX revoked flag; blank for none.</summary>
        public string DeprecatedProperty { get; set; }
        /// <summary>Hierarchy: the property naming a pattern's level of abstraction.</summary>
        public string AbstractionProperty { get; set; }
        /// <summary>Hierarchy: the level whose patterns are the groups.</summary>
        public string GroupAbstraction { get; set; }
        /// <summary>Hierarchy: the property listing a pattern's parents' STIX ids.</summary>
        public string ParentRefsProperty { get; set; }
        /// <summary>A property holding an object's status, beside the STIX revoked flag.</summary>
        public string StatusProperty { get; set; }
        /// <summary>The statuses whose objects are skipped.</summary>
        public List<string> SkipStatuses { get; set; } = new List<string>();
        /// <summary>A technique property holding the release version, for a bundle with no collection.</summary>
        public string VersionProperty { get; set; }
        /// <summary>The technique property listing its platforms; blank for none.</summary>
        public string PlatformsProperty { get; set; }
        /// <summary>The external reference source whose ids are the weaknesses a technique exploits; blank for none.</summary>
        public string WeaknessSource { get; set; }
        /// <summary>External references kept as links to other catalogues.</summary>
        public List<TechniqueLinkSource> LinkSources { get; set; } = new List<TechniqueLinkSource>();
    }

    /// <summary>An external reference source kept as a link.</summary>
    public class TechniqueLinkSource
    {
        /// <summary>The reference's source name in the file.</summary>
        public string Source { get; set; }
        /// <summary>The name the link is shown under.</summary>
        public string Label { get; set; }
        /// <summary>When the reference has an id, the link's text starts with this and the id.</summary>
        public string IdLabelPrefix { get; set; }
    }

    public class JsonLdFormatOptions
    {
        /// <summary>The property holding a technique's id.</summary>
        public string IdProperty { get; set; }
        /// <summary>The class every technique descends from.</summary>
        public string TechniqueClass { get; set; }
        /// <summary>The type of a tactic node.</summary>
        public string TacticType { get; set; }
        /// <summary>The property a technique names its tactics with.</summary>
        public string TacticRelation { get; set; }
        /// <summary>The property ordering the tactics.</summary>
        public string DisplayOrderProperty { get; set; }
        /// <summary>The property holding a technique's or tactic's description.</summary>
        public string DefinitionProperty { get; set; }
        /// <summary>The ontology property holding the release date.</summary>
        public string ReleaseDateProperty { get; set; }
        /// <summary>The ontology property naming the publisher; blank for none.</summary>
        public string PublisherProperty { get; set; }
        /// <summary>The ontology property holding the copyright and terms notice; blank for none.</summary>
        public string NoticeProperty { get; set; }
    }

    /// <summary>The file format of a technique mappings file.</summary>
    public enum TechniqueMappingFormat
    {
        /// <summary>SPARQL query results as JSON: head.vars and results.bindings.</summary>
        SparqlResults = 0
    }

    /// <summary>How a mappings file's technique on this framework's side is found in the release.</summary>
    public enum MappingTargetMatch
    {
        /// <summary>By the technique's id.</summary>
        ExternalId = 0,
        /// <summary>By the local name of the technique's id in the file, such as the class name of an ontology IRI.</summary>
        SourceIdLocalName = 1
    }

    /// <summary>
    /// A file of mappings between another framework's techniques (the source side, by id) and this framework's
    /// (the target side, a technique of the release the file is imported onto).
    /// </summary>
    public class TechniqueMappingDefinition
    {
        public TechniqueMappingFormat Format { get; set; }
        /// <summary>How the mapping reads: the source technique is "{Relation}" the target.</summary>
        public string Relation { get; set; }
        /// <summary>The variable holding the source technique's id.</summary>
        public string SourceIdVariable { get; set; }
        /// <summary>The variable naming the target technique.</summary>
        public string TargetVariable { get; set; }
        public MappingTargetMatch TargetMatch { get; set; }
        /// <summary>The variable holding the target's tactic name for the pair.</summary>
        public string TacticVariable { get; set; }
        /// <summary>The variables of the path behind a pair, worded into its rationale; a pair's weight is its number of distinct paths.</summary>
        public string TargetRelationVariable { get; set; }
        public string TargetArtifactVariable { get; set; }
        public string SourceRelationVariable { get; set; }
        public string SourceArtifactVariable { get; set; }
        /// <summary>Where an administrator gets the file, with {version} for the release's version.</summary>
        public string DownloadUrlTemplate { get; set; }
        public string DownloadHint { get; set; }
    }

    public class TechniqueFrameworkConfiguration : IEntityTypeConfiguration<TechniqueFrameworkEntity>
    {
        public static readonly JsonSerializerOptions SettingsJson = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Converters = { new JsonStringEnumConverter() }
        };

        public void Configure(EntityTypeBuilder<TechniqueFrameworkEntity> builder)
        {
            builder.HasIndex(x => x.Domain).IsUnique();
            builder
                .Property(x => x.Settings)
                .HasColumnType("text")
                .HasConversion(
                    v => JsonSerializer.Serialize(v ?? new TechniqueFrameworkSettings(), SettingsJson),
                    v => JsonSerializer.Deserialize<TechniqueFrameworkSettings>(v ?? "{}", SettingsJson) ?? new TechniqueFrameworkSettings(),
                    new ValueComparer<TechniqueFrameworkSettings>(
                        (a, b) => JsonSerializer.Serialize(a, SettingsJson) == JsonSerializer.Serialize(b, SettingsJson),
                        v => JsonSerializer.Serialize(v, SettingsJson).GetHashCode(),
                        v => JsonSerializer.Deserialize<TechniqueFrameworkSettings>(JsonSerializer.Serialize(v, SettingsJson), SettingsJson)));
        }
    }
}
