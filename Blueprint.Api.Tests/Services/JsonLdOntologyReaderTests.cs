// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Blueprint.Api.Data.Models;
using Blueprint.Api.Infrastructure.Exceptions;
using Blueprint.Api.Services;
using Blueprint.Api.Tests.Support;
using Xunit;

namespace Blueprint.Api.Tests.Services;

/// <summary>
/// <c>JsonLdOntologyReader</c> - a JSON-LD ontology into a release, its tactics and its techniques, with the
/// properties and classes the framework definition names. The fixture and definition are the test D3FEND ones.
/// </summary>
public class JsonLdOntologyReaderTests
{
    private static Task<TechniqueReleaseEntity> Parse(string json, string version = null) => TechniqueFrameworks.ReadAsync(json, version);

    private static TechniqueReleaseEntity Read(string json, TechniqueFrameworkEntity framework)
    {
        using var document = System.Text.Json.JsonDocument.Parse(json);
        return JsonLdOntologyReader.Read(document.RootElement, null, framework);
    }

    [Fact]
    public async Task Parse_ReadsTheReleaseFromTheOntologyNode()
    {
        var release = await Parse(D3fendOntologies.Ontology());

        Assert.Equal("d3fend", release.Domain);
        Assert.True(release.IsDefensive);
        Assert.Equal("1.6.0", release.Version);
        Assert.Equal("MITRE D3FEND", release.Name);
        Assert.Equal("Test D3FEND ontology", release.Description);
        Assert.Equal(new DateTime(2026, 8, 31, 0, 12, 0, DateTimeKind.Utc), release.ReleaseDate);
        Assert.Equal(TechniqueIds.ReleaseId("d3fend", "1.6.0"), release.Id);
    }

    /// <summary>The analytic techniques carry d3fend-ids too, but only the countermeasures are techniques here.</summary>
    [Fact]
    public async Task Parse_TakesTheDefensiveTechniques_AndLeavesTheAnalyticOnes()
    {
        var release = await Parse(D3fendOntologies.Ontology());

        Assert.Equal(["D3-CBAN", "D3-CH", "D3-FA"], release.Techniques.Select(t => t.ExternalId).Order());
    }

    [Fact]
    public async Task Parse_OrdersTacticsByDisplayOrder()
    {
        var release = await Parse(D3fendOntologies.Ontology());

        var tactics = release.Tactics.OrderBy(t => t.DisplayOrder).ToList();
        Assert.Equal(["harden", "detect"], tactics.Select(t => t.ShortName));
        Assert.Equal(["d3f:Harden", "d3f:Detect"], tactics.Select(t => t.ExternalId));
        Assert.Equal("Harden", tactics[0].Name);
        Assert.Equal("The harden tactic raises the cost of exploitation.", tactics[0].Description);
        Assert.Equal("https://d3fend.mitre.org/tactic/d3f:Harden/", tactics[0].Url);
    }

    [Fact]
    public async Task Parse_LinksASubTechniqueToItsParent_AndGivesItTheParentsTactic()
    {
        var release = await Parse(D3fendOntologies.Ontology());
        var parent = release.Techniques.Single(t => t.ExternalId == "D3-CH");
        var child = release.Techniques.Single(t => t.ExternalId == "D3-CBAN");

        Assert.Null(parent.ParentId);
        Assert.Equal(parent.Id, child.ParentId);
        Assert.Same(parent, child.Parent);
        Assert.Equal("Credential Hardening", parent.Name);
        Assert.Equal("Credential hardening makes a credential harder to guess or reuse.", parent.Description);
        Assert.Equal("harden", parent.Tactics);
        Assert.Equal("harden", child.Tactics);
        Assert.Null(parent.Platforms);
    }

    [Fact]
    public async Task Parse_KeepsEveryTacticATechniqueEnables()
    {
        var release = await Parse(D3fendOntologies.Ontology());

        Assert.Equal("detect,harden", release.Techniques.Single(t => t.ExternalId == "D3-FA").Tactics);
    }

    /// <summary>The site's pages are /technique/&lt;class id&gt;/, with the trailing slash (without it the site redirects).</summary>
    [Fact]
    public async Task Parse_LinksATechniqueToItsPageByClassId()
    {
        var release = await Parse(D3fendOntologies.Ontology());

        Assert.Equal("https://d3fend.mitre.org/technique/d3f:CredentialHardening/", release.Techniques.Single(t => t.ExternalId == "D3-CH").Url);
        Assert.Equal("d3f:CertificateBasedAuthentication", release.Techniques.Single(t => t.ExternalId == "D3-CBAN").StixId);
    }

    /// <summary>The ids are what lets a MSEL exported from one Blueprint keep its techniques on another.</summary>
    [Fact]
    public async Task Parse_GivesTheSameReleaseTheSameIds_AndAnotherVersionOtherIds()
    {
        var first = await Parse(D3fendOntologies.Ontology("1.6.0"));
        var again = await Parse(D3fendOntologies.Ontology("1.6.0"));
        var next = await Parse(D3fendOntologies.Ontology("1.7.0"));
        var attack = await TechniqueFrameworks.ReadAsync(TechniqueBundles.Enterprise("1.6.0"));

        Assert.Equal(first.Id, again.Id);
        Assert.Equal(
            first.Techniques.OrderBy(t => t.ExternalId).Select(t => t.Id),
            again.Techniques.OrderBy(t => t.ExternalId).Select(t => t.Id));
        Assert.NotEqual(first.Id, next.Id);
        Assert.NotEqual(
            first.Techniques.Single(t => t.ExternalId == "D3-CH").Id,
            next.Techniques.Single(t => t.ExternalId == "D3-CH").Id);
        Assert.NotEqual(first.Id, attack.Id);
    }

    [Fact]
    public async Task Parse_OfAnOntologyThatDoesNotNameItsVersion_TakesTheOneGiven()
    {
        var release = await Parse(D3fendOntologies.Ontology(includeVersion: false), "1.5.0");

        Assert.Equal("1.5.0", release.Version);
    }

    [Fact]
    public async Task Parse_OfAnOntologyThatDoesNotNameItsVersion_WithNoneGiven_IsRefused()
    {
        var ex = await Assert.ThrowsAsync<InvalidTechniqueBundleException>(() => Parse(D3fendOntologies.Ontology(includeVersion: false)));

        Assert.Contains("version", ex.Message);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"type":"bundle","objects":[]}""")]
    [InlineData("""{"@graph":{}}""")]
    public async Task Parse_OfSomethingThatIsNotTheOntology_IsRefused(string json) =>
        await Assert.ThrowsAsync<InvalidTechniqueBundleException>(() => Parse(json));

    [Fact]
    public void Read_OfAGraphWithoutTheFrameworksTechniques_IsRefused()
    {
        var ex = Assert.Throws<InvalidTechniqueBundleException>(() => Read(D3fendOntologies.WithoutTechniques(), TechniqueFrameworks.D3fend()));

        Assert.Contains("no tactics and techniques", ex.Message);
    }

    /// <summary>The classes and properties come from the definition: name others and the same graph reads as nothing.</summary>
    [Fact]
    public void Read_WithADefinitionNamingOtherClasses_FindsNoTechniques()
    {
        var framework = TechniqueFrameworks.D3fend();
        framework.Settings.JsonLd.TechniqueClass = "ex:SomeOtherClass";

        Assert.Throws<InvalidTechniqueBundleException>(() => Read(D3fendOntologies.Ontology(), framework));
    }

    [Fact]
    public void Read_WithADefinitionThatLacksItsIdProperty_IsRefused()
    {
        var framework = TechniqueFrameworks.D3fend();
        framework.Settings.JsonLd.IdProperty = null;

        var ex = Assert.Throws<InvalidTechniqueBundleException>(() => Read(D3fendOntologies.Ontology(), framework));

        Assert.Contains("id property", ex.Message);
    }

    /// <summary>With no URL templates there are no links: the reader knows no site of its own.</summary>
    [Fact]
    public void Read_WithNoUrlTemplates_LinksNothing()
    {
        var framework = TechniqueFrameworks.D3fend();
        framework.Settings.TechniqueUrlTemplate = null;
        framework.Settings.TacticUrlTemplate = null;

        var release = Read(D3fendOntologies.Ontology(), framework);

        Assert.All(release.Techniques, t => Assert.Null(t.Url));
        Assert.All(release.Tactics, t => Assert.Null(t.Url));
    }

    [Fact]
    public void Read_TakesThePublisherAndNoticeFromTheOntologyPropertiesTheDefinitionNames()
    {
        var release = Read(D3fendOntologies.Ontology("1.6.0"), TechniqueFrameworks.D3fend());

        Assert.Equal("The MITRE Corporation", release.Source);
        Assert.Contains("D3FEND™ and the D3FEND logo are trademarks of The MITRE Corporation.", release.Notice);
        Assert.Contains("Copyright 2022 The MITRE Corporation.", release.Notice);
        Assert.Null(release.TermsUrl);
    }

    /// <summary>
    /// A definition that names no publisher or notice property gives the release neither: the title is not taken
    /// for the publisher.
    /// </summary>
    [Fact]
    public void Read_WithADefinitionThatNamesNoPublisherOrNoticeProperty_CarriesNeither()
    {
        var framework = TechniqueFrameworks.D3fend();
        framework.Settings.JsonLd.PublisherProperty = null;
        framework.Settings.JsonLd.NoticeProperty = null;

        var release = Read(D3fendOntologies.Ontology("1.6.0"), framework);

        Assert.Null(release.Source);
        Assert.Null(release.Notice);
    }

    /// <summary>
    /// OWL lets a class name itself as a superclass, and two classes name each other: the link that would close the
    /// loop is left out, as EF cannot insert a technique that is its own ancestor.
    /// </summary>
    [Fact]
    public void Read_OfAClassHierarchyThatLoops_LinksNoTechniqueToItself()
    {
        const string json = """
            {
              "@graph": [
                { "@id": "d3f:Harden", "@type": "d3f:DefensiveTactic", "rdfs:label": "Harden" },
                { "@id": "d3f:Self", "d3f:d3fend-id": "D3-S", "d3f:enables": { "@id": "d3f:Harden" },
                  "rdfs:subClassOf": [ { "@id": "d3f:Self" }, { "@id": "d3f:DefensiveTechnique" } ] },
                { "@id": "d3f:Left", "d3f:d3fend-id": "D3-L",
                  "rdfs:subClassOf": [ { "@id": "d3f:Right" }, { "@id": "d3f:DefensiveTechnique" } ] },
                { "@id": "d3f:Right", "d3f:d3fend-id": "D3-R",
                  "rdfs:subClassOf": [ { "@id": "d3f:Left" }, { "@id": "d3f:DefensiveTechnique" } ] }
              ]
            }
            """;
        using var document = System.Text.Json.JsonDocument.Parse(json);

        var release = JsonLdOntologyReader.Read(document.RootElement, "1.0", TechniqueFrameworks.D3fend());

        Assert.Null(release.Techniques.Single(t => t.ExternalId == "D3-S").Parent);
        var left = release.Techniques.Single(t => t.ExternalId == "D3-L");
        var right = release.Techniques.Single(t => t.ExternalId == "D3-R");
        Assert.True((left.Parent == right) ^ (right.Parent == left));
        Assert.True(left.Parent == null || right.Parent == null);
    }
}
