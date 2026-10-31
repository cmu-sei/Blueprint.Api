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

/// <summary><c>AttackStixParser</c> - an ATT&amp;CK STIX bundle into a release, its tactics and its techniques.</summary>
public class AttackStixParserTests
{
    private static async Task<AttackReleaseEntity> Parse(string json, string version = null) =>
        await AttackStixParser.ParseAsync(new MemoryStream(Encoding.UTF8.GetBytes(json)), version, TestContext.Current.CancellationToken);

    [Fact]
    public async Task Parse_ReadsTheReleaseFromTheCollection()
    {
        var release = await Parse(AttackBundles.Enterprise("1.0"));

        Assert.Equal("enterprise-attack", release.Domain);
        Assert.Equal("1.0", release.Version);
        Assert.Equal("Enterprise ATT&CK", release.Name);
        Assert.Equal(new DateTime(2025, 4, 15, 0, 0, 0, DateTimeKind.Utc), release.ReleaseDate);
    }

    [Fact]
    public async Task Parse_SkipsRevokedAndDeprecatedTechniques()
    {
        var release = await Parse(AttackBundles.Enterprise());

        Assert.Equal(["T1003", "T1053", "T1059", "T1059.001"], release.Techniques.Select(t => t.ExternalId).Order());
    }

    [Fact]
    public async Task Parse_OrdersTacticsAsTheMatrixDoes()
    {
        var release = await Parse(AttackBundles.Enterprise());

        Assert.Equal(["persistence", "execution"], release.Tactics.OrderBy(t => t.DisplayOrder).Select(t => t.ShortName));
    }

    [Fact]
    public async Task Parse_LinksASubTechniqueToItsParent_AndKeepsEveryTactic()
    {
        var release = await Parse(AttackBundles.Enterprise());
        var parent = release.Techniques.Single(t => t.ExternalId == "T1059");
        var child = release.Techniques.Single(t => t.ExternalId == "T1059.001");
        var scheduled = release.Techniques.Single(t => t.ExternalId == "T1053");

        Assert.Equal(parent.Id, child.ParentId);
        Assert.Null(parent.ParentId);
        Assert.Equal("execution,persistence", scheduled.Tactics);
        Assert.Equal("Linux,Windows", parent.Platforms);
        Assert.Equal("https://attack.mitre.org/techniques/T1059/001", child.Url);
    }

    /// <summary>The ids are what lets a MSEL exported from one Blueprint keep its techniques on another.</summary>
    [Fact]
    public async Task Parse_GivesTheSameReleaseTheSameIds_AndAnotherVersionOtherIds()
    {
        var first = await Parse(AttackBundles.Enterprise("1.0"));
        var again = await Parse(AttackBundles.Enterprise("1.0"));
        var next = await Parse(AttackBundles.Enterprise("2.0"));

        Assert.Equal(first.Id, again.Id);
        Assert.Equal(
            first.Techniques.OrderBy(t => t.ExternalId).Select(t => t.Id),
            again.Techniques.OrderBy(t => t.ExternalId).Select(t => t.Id));
        Assert.NotEqual(first.Id, next.Id);
        Assert.NotEqual(
            first.Techniques.Single(t => t.ExternalId == "T1059").Id,
            next.Techniques.Single(t => t.ExternalId == "T1059").Id);
    }

    /// <summary>RFC 9562 appendix A.4: the DNS namespace and "www.example.com".</summary>
    [Fact]
    public void CreateNameBasedGuid_MatchesTheRfcTestVector() =>
        Assert.Equal(
            new Guid("2ed6657d-e927-568b-95e1-2665a8aea6a2"),
            AttackStixParser.CreateNameBasedGuid(new Guid("6ba7b810-9dad-11d1-80b4-00c04fd430c8"), "www.example.com"));

    [Fact]
    public async Task Parse_OfABundleThatDoesNotNameItsVersion_TakesTheOneGiven()
    {
        var release = await Parse(AttackBundles.Enterprise(includeVersion: false), "8.2");

        Assert.Equal("8.2", release.Version);
    }

    [Fact]
    public async Task Parse_OfABundleThatDoesNotNameItsVersion_WithNoneGiven_IsRefused()
    {
        var ex = await Assert.ThrowsAsync<InvalidAttackBundleException>(() => Parse(AttackBundles.Enterprise(includeVersion: false)));

        Assert.Contains("version", ex.Message);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"type":"report"}""")]
    [InlineData("""{"type":"bundle","objects":[]}""")]
    public async Task Parse_OfSomethingThatIsNotAnAttackBundle_IsRefused(string json) =>
        await Assert.ThrowsAsync<InvalidAttackBundleException>(() => Parse(json));

    [Fact]
    public async Task Parse_OfABundleWhoseTacticsNameNoSource_IsRefused()
    {
        var ex = await Assert.ThrowsAsync<InvalidAttackBundleException>(() => Parse(AttackBundles.WithoutASource(), "1.0"));

        Assert.Contains("source", ex.Message);
    }

    // ---------------------------------------------------------------------------------------------
    // ATLAS: the same data model under its own catalogue
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Parse_OfAtlas_ReadsTheReleaseFromTheCollection()
    {
        var release = await Parse(AttackBundles.Atlas());

        Assert.Equal("ATLAS", release.Domain);
        Assert.Equal("2026.10", release.Version);
        Assert.Equal("ATLAS", release.Name);
        Assert.Equal(AttackStixParser.ReleaseId("ATLAS", "2026.10"), release.Id);
        Assert.Equal(new DateTime(2026, 10, 28, 0, 0, 0, DateTimeKind.Utc), release.ReleaseDate);
    }

    /// <summary>The ATT&amp;CK references ATLAS carries alongside its own are cross-references, not identity.</summary>
    [Fact]
    public async Task Parse_OfAtlas_IdentifiesTacticsAndTechniquesByTheirAtlasReference()
    {
        var release = await Parse(AttackBundles.Atlas());

        Assert.Equal(["AML.TA0002", "AML.TA0000"], release.Tactics.OrderBy(t => t.DisplayOrder).Select(t => t.ExternalId));
        Assert.Equal("https://atlas.mitre.org/tactics/AML.TA0002", release.Tactics.Single(t => t.ExternalId == "AML.TA0002").Url);
        Assert.Equal(["AML.T0000", "AML.T0000.000", "AML.T0040"], release.Techniques.Select(t => t.ExternalId).Order());
        Assert.All(release.Techniques, t => Assert.StartsWith("https://atlas.mitre.org/techniques/", t.Url));
        Assert.DoesNotContain(release.Techniques, t => t.ExternalId.StartsWith('T'));
    }

    [Fact]
    public async Task Parse_OfAtlas_LinksASubTechniqueToItsParent_AndMatchesTheAtlasKillChain()
    {
        var release = await Parse(AttackBundles.Atlas());
        var parent = release.Techniques.Single(t => t.ExternalId == "AML.T0000");
        var child = release.Techniques.Single(t => t.ExternalId == "AML.T0000.000");
        var access = release.Techniques.Single(t => t.ExternalId == "AML.T0040");

        Assert.Equal(parent.Id, child.ParentId);
        Assert.Null(parent.ParentId);
        Assert.Null(access.ParentId);
        Assert.Equal("reconnaissance", parent.Tactics);
        Assert.Equal("ai-model-access", access.Tactics);
        Assert.Equal("Agentic AI,Generative AI", parent.Platforms);
    }

    /// <summary>The two catalogues are separate releases, and each stays the same release on re-import.</summary>
    [Fact]
    public async Task Parse_OfAtlas_GivesIdsOfItsOwn_ApartFromAttack()
    {
        var atlas = await Parse(AttackBundles.Atlas());
        var again = await Parse(AttackBundles.Atlas());
        var attack = await Parse(AttackBundles.Enterprise("1.0"));

        Assert.Equal(atlas.Id, again.Id);
        Assert.Equal(
            atlas.Techniques.OrderBy(t => t.ExternalId).Select(t => t.Id),
            again.Techniques.OrderBy(t => t.ExternalId).Select(t => t.Id));
        Assert.NotEqual(atlas.Id, attack.Id);
        Assert.Equal("enterprise-attack", attack.Domain);
        Assert.Equal(["T1003", "T1053", "T1059", "T1059.001"], attack.Techniques.Select(t => t.ExternalId).Order());
    }
}
