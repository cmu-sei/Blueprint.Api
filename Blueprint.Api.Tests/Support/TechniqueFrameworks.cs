// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Blueprint.Api.Data.Enumerations;
using Blueprint.Api.Data.Models;
using Blueprint.Api.Services;

namespace Blueprint.Api.Tests.Support;

/// <summary>
/// Technique framework definitions for the test bundles, as an administrator would enter them. Blueprint ships no
/// definitions, so every test that reads a release file brings its own; these mirror the ones used with the
/// published files.
/// </summary>
public static class TechniqueFrameworks
{
    public static readonly Guid EnterpriseId = new("6a0d9e00-0000-4000-8000-000000000001");
    public static readonly Guid AtlasId = new("6a0d9e00-0000-4000-8000-000000000002");
    public static readonly Guid CapecId = new("6a0d9e00-0000-4000-8000-000000000003");
    public static readonly Guid D3fendId = new("6a0d9e00-0000-4000-8000-000000000004");
    public static readonly Guid CustomId = new("6a0d9e00-0000-4000-8000-000000000005");

    public static TechniqueFrameworkEntity Enterprise() => new()
    {
        Id = EnterpriseId,
        Name = "Enterprise ATT&CK",
        Description = "Enterprise techniques.",
        Domain = "enterprise-attack",
        Format = TechniqueFileFormat.StixBundle,
        Settings = new TechniqueFrameworkSettings
        {
            Match = new TechniqueFrameworkMatch { StixDomain = "enterprise-attack" },
            Stix = new StixFormatOptions { IdSource = "mitre-attack" },
            GroupLabel = "Tactics",
            TermsUrl = "https://attack.mitre.org/resources/legal-and-branding/terms-of-use/"
        }
    };

    public static TechniqueFrameworkEntity Atlas() => new()
    {
        Id = AtlasId,
        Name = "ATLAS",
        Domain = "ATLAS",
        Format = TechniqueFileFormat.StixBundle,
        Settings = new TechniqueFrameworkSettings
        {
            Match = new TechniqueFrameworkMatch { StixDomain = "ATLAS" },
            Stix = new StixFormatOptions { IdSource = "mitre-atlas" }
        }
    };

    public static TechniqueFrameworkEntity Capec() => new()
    {
        Id = CapecId,
        Name = "MITRE CAPEC",
        Description = "Attack patterns.",
        Domain = "capec",
        Format = TechniqueFileFormat.StixBundle,
        Settings = new TechniqueFrameworkSettings
        {
            Match = new TechniqueFrameworkMatch { ReferenceSource = "capec", IdPrefix = "CAPEC-" },
            Stix = new StixFormatOptions
            {
                IdSource = "capec",
                IdPrefix = "CAPEC-",
                Grouping = StixGrouping.Hierarchy,
                AbstractionProperty = "x_capec_abstraction",
                GroupAbstraction = "Meta",
                ParentRefsProperty = "x_capec_child_of_refs",
                StatusProperty = "x_capec_status",
                SkipStatuses = ["Deprecated", "Obsolete"],
                VersionProperty = "x_capec_version",
                PlatformsProperty = "x_capec_domains",
                WeaknessSource = "cwe",
                LinkSources =
                [
                    new TechniqueLinkSource { Source = "OWASP Attacks", Label = "OWASP" },
                    new TechniqueLinkSource { Source = "WASC", Label = "WASC", IdLabelPrefix = "WASC-" }
                ]
            },
            GroupLabel = "Categories",
            WeaknessUrlTemplate = "https://cwe.mitre.org/data/definitions/{id}.html"
        }
    };

    public static TechniqueFrameworkEntity D3fend() => new()
    {
        Id = D3fendId,
        Name = "MITRE D3FEND",
        Domain = "d3fend",
        IsDefensive = true,
        Format = TechniqueFileFormat.JsonLdOntology,
        Settings = new TechniqueFrameworkSettings
        {
            Match = new TechniqueFrameworkMatch { Property = "d3f:d3fend-id" },
            JsonLd = new JsonLdFormatOptions
            {
                IdProperty = "d3f:d3fend-id",
                TechniqueClass = "d3f:DefensiveTechnique",
                TacticType = "d3f:DefensiveTactic",
                TacticRelation = "d3f:enables",
                DisplayOrderProperty = "d3f:display-order",
                DefinitionProperty = "d3f:definition",
                ReleaseDateProperty = "d3f:release-date",
                NoticeProperty = "rdfs:comment"
            },
            Mappings = D3fendMappings(),
            TacticOrder = ["Model", "Harden", "Detect", "Isolate", "Deceive", "Evict", "Restore"],
            TechniqueUrlTemplate = "https://d3fend.mitre.org/technique/{sourceId}/",
            TacticUrlTemplate = "https://d3fend.mitre.org/tactic/{sourceId}/"
        }
    };

    public static TechniqueMappingDefinition D3fendMappings() => new()
    {
        Format = TechniqueMappingFormat.SparqlResults,
        Relation = "countered by",
        SourceIdVariable = "off_tech_id",
        TargetVariable = "def_tech",
        TargetMatch = MappingTargetMatch.SourceIdLocalName,
        TargetLabelVariable = "def_tech_label",
        TacticVariable = "def_tactic_label",
        TargetRelationVariable = "def_artifact_rel_label",
        TargetArtifactVariable = "def_artifact_label",
        SourceRelationVariable = "off_artifact_rel_label",
        SourceArtifactVariable = "off_artifact_label",
        DownloadUrlTemplate = "https://d3fend.mitre.org/ontologies/d3fend/{version}/d3fend-full-mappings.json"
    };

    /// <summary>A framework for <see cref="TechniqueBundles.Custom"/>, which no other definition matches.</summary>
    public static TechniqueFrameworkEntity Custom(string domain = "acme-ttp") => new()
    {
        Id = CustomId,
        Name = "Acme Techniques",
        Domain = domain,
        Format = TechniqueFileFormat.StixBundle,
        Settings = new TechniqueFrameworkSettings
        {
            Match = new TechniqueFrameworkMatch { StixDomain = domain },
            Stix = new StixFormatOptions { IdSource = "acme" }
        }
    };

    public static List<TechniqueFrameworkEntity> All() => [Enterprise(), Atlas(), Capec(), D3fend(), Custom()];

    /// <summary>Reads a test file as the framework it matches among <see cref="All"/>.</summary>
    public static TechniqueReleaseEntity Read(string json, string version = null, IReadOnlyCollection<TechniqueFrameworkEntity> frameworks = null)
    {
        using var document = JsonDocument.Parse(json);
        return ReleaseFileReader.Read(document.RootElement, version, frameworks ?? All());
    }

    public static Task<TechniqueReleaseEntity> ReadAsync(string json, string version = null) =>
        ReleaseFileReader.ReadAsync(new MemoryStream(Encoding.UTF8.GetBytes(json)), version, All(), null, TestContext.Current.CancellationToken);
}
