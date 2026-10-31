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

/// <summary><c>D3fendOntologyParser</c> - the D3FEND JSON-LD ontology into a release, its tactics and its techniques.</summary>
public class D3fendOntologyParserTests
{
    private static async Task<AttackReleaseEntity> Parse(string json, string version = null) =>
        await D3fendOntologyParser.ParseAsync(new MemoryStream(Encoding.UTF8.GetBytes(json)), version, TestContext.Current.CancellationToken);

    [Fact]
    public async Task Parse_ReadsTheReleaseFromTheOntologyNode()
    {
        var release = await Parse(D3fendOntologies.Ontology());

        Assert.Equal("d3fend", release.Domain);
        Assert.Equal("1.6.0", release.Version);
        Assert.Equal("MITRE D3FEND", release.Name);
        Assert.Equal("Test D3FEND ontology", release.Description);
        Assert.Equal(new DateTime(2026, 8, 31, 0, 12, 0, DateTimeKind.Utc), release.ReleaseDate);
        Assert.Equal(AttackStixParser.ReleaseId("d3fend", "1.6.0"), release.Id);
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
        var attack = await AttackStixParser.ParseAsync(
            new MemoryStream(Encoding.UTF8.GetBytes(AttackBundles.Enterprise("1.6.0"))), null, TestContext.Current.CancellationToken);

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
        var ex = await Assert.ThrowsAsync<InvalidAttackBundleException>(() => Parse(D3fendOntologies.Ontology(includeVersion: false)));

        Assert.Contains("version", ex.Message);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"type":"bundle","objects":[]}""")]
    [InlineData("""{"@graph":{}}""")]
    public async Task Parse_OfSomethingThatIsNotTheOntology_IsRefused(string json) =>
        await Assert.ThrowsAsync<InvalidAttackBundleException>(() => Parse(json));

    [Fact]
    public async Task Parse_OfAGraphWithoutDefensiveTechniques_IsRefused()
    {
        var ex = await Assert.ThrowsAsync<InvalidAttackBundleException>(() => Parse(D3fendOntologies.WithoutTechniques()));

        Assert.Contains("no defensive tactics and techniques", ex.Message);
    }
}
