// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Blueprint.Api.Data.Models;
using Blueprint.Api.Infrastructure.Exceptions;
using Blueprint.Api.Services;
using Blueprint.Api.Tests.Support;
using Xunit;

namespace Blueprint.Api.Tests.Services;

/// <summary>
/// <c>StixBundleReader</c> - a STIX bundle into a release, its tactics and its techniques, as the framework the
/// bundle matches defines them. The definitions are the test ones in <see cref="TechniqueFrameworks"/>.
/// </summary>
public class StixBundleReaderTests
{
    private static Task<TechniqueReleaseEntity> Parse(string json, string version = null) => TechniqueFrameworks.ReadAsync(json, version);

    private static TechniqueReleaseEntity Read(string json, string version, TechniqueFrameworkEntity framework)
    {
        using var document = JsonDocument.Parse(json);
        return StixBundleReader.Read(document.RootElement, version, framework);
    }

    [Fact]
    public async Task Parse_ReadsTheReleaseFromTheCollection()
    {
        var release = await Parse(TechniqueBundles.Enterprise("1.0"));

        Assert.Equal("enterprise-attack", release.Domain);
        Assert.False(release.IsDefensive);
        Assert.Equal("1.0", release.Version);
        Assert.Equal("Enterprise ATT&CK", release.Name);
        Assert.Equal(new DateTime(2025, 4, 15, 0, 0, 0, DateTimeKind.Utc), release.ReleaseDate);
    }

    [Fact]
    public async Task Parse_SkipsRevokedAndDeprecatedTechniques()
    {
        var release = await Parse(TechniqueBundles.Enterprise());

        Assert.Equal(["T1003", "T1053", "T1059", "T1059.001"], release.Techniques.Select(t => t.ExternalId).Order());
    }

    [Fact]
    public async Task Parse_OrdersTacticsAsTheMatrixDoes()
    {
        var release = await Parse(TechniqueBundles.Enterprise());

        Assert.Equal(["persistence", "execution"], release.Tactics.OrderBy(t => t.DisplayOrder).Select(t => t.ShortName));
    }

    [Fact]
    public async Task Parse_LinksASubTechniqueToItsParent_AndKeepsEveryTactic()
    {
        var release = await Parse(TechniqueBundles.Enterprise());
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
        var first = await Parse(TechniqueBundles.Enterprise("1.0"));
        var again = await Parse(TechniqueBundles.Enterprise("1.0"));
        var next = await Parse(TechniqueBundles.Enterprise("2.0"));

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
            TechniqueIds.CreateNameBasedGuid(new Guid("6ba7b810-9dad-11d1-80b4-00c04fd430c8"), "www.example.com"));

    [Fact]
    public async Task Parse_OfABundleThatDoesNotNameItsVersion_TakesTheOneGiven()
    {
        var release = await Parse(TechniqueBundles.Enterprise(includeVersion: false), "8.2");

        Assert.Equal("8.2", release.Version);
    }

    [Fact]
    public async Task Parse_OfABundleThatDoesNotNameItsVersion_WithNoneGiven_IsRefused()
    {
        var ex = await Assert.ThrowsAsync<InvalidTechniqueBundleException>(() => Parse(TechniqueBundles.Enterprise(includeVersion: false)));

        Assert.Contains("version", ex.Message);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"type":"report"}""")]
    [InlineData("""{"type":"bundle","objects":[]}""")]
    public async Task Parse_OfSomethingThatIsNotAnAttackBundle_IsRefused(string json) =>
        await Assert.ThrowsAsync<InvalidTechniqueBundleException>(() => Parse(json));

    [Fact]
    public void Read_OfABundleWhoseTacticsNameNoSource_WithAFrameworkThatSetsNone_IsRefused()
    {
        var framework = TechniqueFrameworks.Enterprise();
        framework.Settings.Stix.IdSource = null;

        var ex = Assert.Throws<InvalidTechniqueBundleException>(() => Read(TechniqueBundles.WithoutASource(), "1.0", framework));

        Assert.Contains("id source", ex.Message);
    }

    /// <summary>With no id source set, the source most tactics name is the bundle's own catalogue.</summary>
    [Fact]
    public void Read_WithAFrameworkThatSetsNoIdSource_TakesTheOneMostTacticsName()
    {
        var framework = TechniqueFrameworks.Enterprise();
        framework.Settings.Stix.IdSource = null;

        var release = Read(TechniqueBundles.Enterprise("1.0"), null, framework);

        Assert.Equal(4, release.Techniques.Count);
    }

    /// <summary>The framework, not the file, decides the domain the release is filed under, and fills what the file leaves out.</summary>
    [Fact]
    public void Read_FilesTheReleaseUnderItsFrameworksDomain_AndTakesItsDefaults()
    {
        var framework = TechniqueFrameworks.Custom();
        framework.Domain = "another-domain";
        framework.Settings.SourceUrl = "https://example.org/site";
        framework.Settings.TechniqueUrlTemplate = "https://example.org/t/{id}";

        var release = Read(TechniqueBundles.Custom(), null, framework);

        Assert.Equal("another-domain", release.Domain);
        Assert.Equal(TechniqueIds.ReleaseId("another-domain", "1.0"), release.Id);
        Assert.Equal(framework.Id, release.TechniqueFrameworkId);
        Assert.Equal("https://example.org/site", release.SourceUrl);
        // the bundle's techniques carry no URL, so the framework's template makes one
        Assert.All(release.Techniques, t => Assert.StartsWith("https://example.org/t/", t.Url));
    }

    // ---------------------------------------------------------------------------------------------
    // CAPEC: attack patterns grouped by their Meta patterns, with no tactics at all
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Parse_OfCapec_ReadsTheReleaseFromTheBundle()
    {
        var release = await Parse(CapecBundles.Capec());

        Assert.Equal("capec", release.Domain);
        Assert.False(release.IsDefensive);
        Assert.Equal("3.9", release.Version);
        Assert.Equal("MITRE CAPEC", release.Name);
        Assert.Equal(new DateTime(2023, 1, 24, 0, 0, 0, DateTimeKind.Utc), release.ReleaseDate);
    }

    [Fact]
    public async Task Parse_OfCapec_TakesTheVersionGiven_OverTheOneTheBundleCarries()
    {
        var release = await Parse(CapecBundles.Capec(), "4.0");

        Assert.Equal("4.0", release.Version);
    }

    [Fact]
    public async Task Parse_OfCapecThatDoesNotNameItsVersion_WithNoneGiven_IsRefused()
    {
        var ex = await Assert.ThrowsAsync<InvalidTechniqueBundleException>(() => Parse(CapecBundles.Capec(includeVersion: false)));

        Assert.Contains("version", ex.Message);
        Assert.Equal("3.1", (await Parse(CapecBundles.Capec(includeVersion: false), "3.1")).Version);
    }

    [Fact]
    public async Task Parse_OfCapec_MakesTheMetaPatternsTheGroups_InAlphabeticalOrder()
    {
        var release = await Parse(CapecBundles.Capec());

        var groups = release.Tactics.OrderBy(t => t.DisplayOrder).ToList();
        Assert.Equal(["CAPEC-123", "CAPEC-248"], groups.Select(g => g.ExternalId));
        Assert.Equal(["Buffer Manipulation", "Command Injection"], groups.Select(g => g.Name));
        Assert.Equal(["capec-123", "capec-248"], groups.Select(g => g.ShortName));
        Assert.Equal("https://capec.mitre.org/data/definitions/248.html", groups[1].Url);
        Assert.DoesNotContain(release.Techniques, t => t.ExternalId is "CAPEC-123" or "CAPEC-248");
    }

    [Fact]
    public async Task Parse_OfCapec_SkipsDeprecatedAndObsoletePatterns()
    {
        var release = await Parse(CapecBundles.Capec());

        Assert.Equal(["CAPEC-10", "CAPEC-100", "CAPEC-66", "CAPEC-8", "CAPEC-9"], release.Techniques.Select(t => t.ExternalId).Order());
    }

    [Fact]
    public async Task Parse_OfCapec_MakesAStandardPatternATechnique_AndADetailedOneItsSubTechnique()
    {
        var release = await Parse(CapecBundles.Capec());
        var overflow = release.Techniques.Single(t => t.ExternalId == "CAPEC-100");

        Assert.Null(overflow.ParentId);
        Assert.Null(release.Techniques.Single(t => t.ExternalId == "CAPEC-66").ParentId);
        Assert.Equal(overflow.Id, release.Techniques.Single(t => t.ExternalId == "CAPEC-8").ParentId);
        Assert.Equal(overflow.Id, release.Techniques.Single(t => t.ExternalId == "CAPEC-10").ParentId);
    }

    [Fact]
    public async Task Parse_OfCapec_AttachesAPatternBelowASubTechniqueToTheTechniqueAboveBoth()
    {
        var release = await Parse(CapecBundles.Capec());

        var local = release.Techniques.Single(t => t.ExternalId == "CAPEC-9");
        Assert.Equal(release.Techniques.Single(t => t.ExternalId == "CAPEC-100").Id, local.ParentId);
        Assert.Equal(local.Parent, release.Techniques.Single(t => t.ExternalId == "CAPEC-100"));
    }

    [Fact]
    public async Task Parse_OfCapec_PutsEachPatternUnderTheMetaPatternAboveIt_ThroughAnyLevel()
    {
        var release = await Parse(CapecBundles.Capec());

        Assert.Equal("capec-248", release.Techniques.Single(t => t.ExternalId == "CAPEC-66").Tactics);
        Assert.Equal("capec-123", release.Techniques.Single(t => t.ExternalId == "CAPEC-100").Tactics);
        Assert.Equal("capec-123", release.Techniques.Single(t => t.ExternalId == "CAPEC-8").Tactics);
        Assert.Equal("capec-123", release.Techniques.Single(t => t.ExternalId == "CAPEC-9").Tactics);
    }

    [Fact]
    public async Task Parse_OfCapec_CarriesTheDomainsOfAttack_TheWeaknesses_AndTheOwaspAndWascLinks()
    {
        var release = await Parse(CapecBundles.Capec());

        var sql = release.Techniques.Single(t => t.ExternalId == "CAPEC-66");
        Assert.Equal("https://capec.mitre.org/data/definitions/66.html", sql.Url);
        Assert.Equal("Software", sql.Platforms);
        Assert.Equal("CWE-89,CWE-1286", sql.Weaknesses);
        Assert.Equal(
            "WASC|WASC-19 SQL Injection|http://projects.webappsec.org/SQL-Injection\nOWASP|SQL Injection|https://owasp.org/www-community/attacks/SQL_Injection",
            sql.ReferenceLinks);
        Assert.Equal("Software,Hardware", release.Techniques.Single(t => t.ExternalId == "CAPEC-100").Platforms);
        Assert.Equal("", release.Techniques.Single(t => t.ExternalId == "CAPEC-100").Weaknesses);
        Assert.Null(release.Techniques.Single(t => t.ExternalId == "CAPEC-100").ReferenceLinks);
    }

    [Fact]
    public async Task Parse_OfCapec_GivesIdsOfItsOwn_ThatDoNotChangeBetweenParses()
    {
        var first = await Parse(CapecBundles.Capec());
        var again = await Parse(CapecBundles.Capec());
        var other = await Parse(CapecBundles.Capec(), "4.0");

        Assert.Equal(TechniqueIds.ReleaseId("capec", "3.9"), first.Id);
        Assert.Equal(first.Techniques.Select(t => t.Id).Order(), again.Techniques.Select(t => t.Id).Order());
        Assert.Empty(first.Techniques.Select(t => t.Id).Intersect(other.Techniques.Select(t => t.Id)));
    }

    [Fact]
    public async Task Parse_OfAttackPatternsOfAnotherCatalogueWithNoTactics_MatchesNoFramework()
    {
        var ex = await Assert.ThrowsAsync<InvalidTechniqueBundleException>(() => Parse(CapecBundles.WithoutCapecIds(), "1.0"));

        Assert.Equal(ReleaseFileReader.NoFrameworkMessage, ex.Message);
    }

    /// <summary>A pattern without an id from the framework's source is left out; the rest still import.</summary>
    [Fact]
    public async Task Parse_OfCapecMixedWithAPatternThatIsNotCapec_LeavesThatPatternOut()
    {
        var mixed = CapecBundles.Capec().Replace(
            "\"objects\": [",
            "\"objects\": [ { \"type\": \"attack-pattern\", \"id\": \"attack-pattern--00000000-0000-4000-8000-000000000999\", \"name\": \"Stray\" },",
            StringComparison.Ordinal);

        var release = await Parse(mixed);

        Assert.Equal("capec", release.Domain);
        Assert.DoesNotContain(release.Techniques, t => t.Name == "Stray");
    }

    [Fact]
    public async Task Parse_OfCapecWhoseEveryPatternIsRetired_IsRefused()
    {
        var retired = CapecBundles.Capec()
            .Replace("\"Stable\"", "\"Deprecated\"", StringComparison.Ordinal)
            .Replace("\"Draft\"", "\"Obsolete\"", StringComparison.Ordinal);

        var ex = await Assert.ThrowsAsync<InvalidTechniqueBundleException>(() => Parse(retired));

        Assert.Contains("no techniques", ex.Message);
    }

    // ---------------------------------------------------------------------------------------------
    // ATLAS: the same data model under its own catalogue
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Parse_OfAtlas_ReadsTheReleaseFromTheCollection()
    {
        var release = await Parse(TechniqueBundles.Atlas());

        Assert.Equal("ATLAS", release.Domain);
        Assert.False(release.IsDefensive);
        Assert.Equal("2026.10", release.Version);
        Assert.Equal("ATLAS", release.Name);
        Assert.Equal(TechniqueIds.ReleaseId("ATLAS", "2026.10"), release.Id);
        Assert.Equal(new DateTime(2026, 10, 28, 0, 0, 0, DateTimeKind.Utc), release.ReleaseDate);
    }

    /// <summary>The ATT&amp;CK references ATLAS carries alongside its own are cross-references, not identity.</summary>
    [Fact]
    public async Task Parse_OfAtlas_IdentifiesTacticsAndTechniquesByTheirAtlasReference()
    {
        var release = await Parse(TechniqueBundles.Atlas());

        Assert.Equal(["AML.TA0002", "AML.TA0000"], release.Tactics.OrderBy(t => t.DisplayOrder).Select(t => t.ExternalId));
        Assert.Equal("https://atlas.mitre.org/tactics/AML.TA0002", release.Tactics.Single(t => t.ExternalId == "AML.TA0002").Url);
        Assert.Equal(["AML.T0000", "AML.T0000.000", "AML.T0040"], release.Techniques.Select(t => t.ExternalId).Order());
        Assert.All(release.Techniques, t => Assert.StartsWith("https://atlas.mitre.org/techniques/", t.Url));
        Assert.DoesNotContain(release.Techniques, t => t.ExternalId.StartsWith('T'));
    }

    [Fact]
    public async Task Parse_OfAtlas_LinksASubTechniqueToItsParent_AndMatchesTheAtlasKillChain()
    {
        var release = await Parse(TechniqueBundles.Atlas());
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
        var atlas = await Parse(TechniqueBundles.Atlas());
        var again = await Parse(TechniqueBundles.Atlas());
        var attack = await Parse(TechniqueBundles.Enterprise("1.0"));

        Assert.Equal(atlas.Id, again.Id);
        Assert.Equal(
            atlas.Techniques.OrderBy(t => t.ExternalId).Select(t => t.Id),
            again.Techniques.OrderBy(t => t.ExternalId).Select(t => t.Id));
        Assert.NotEqual(atlas.Id, attack.Id);
        Assert.Equal("enterprise-attack", attack.Domain);
        Assert.Equal(["T1003", "T1053", "T1059", "T1059.001"], attack.Techniques.Select(t => t.ExternalId).Order());
    }

    /// <summary>The publisher and the notice are what the bundle itself says: nothing is written by the parser.</summary>
    [Fact]
    public async Task Parse_OfAnAttackBundle_TakesThePublisherAndNoticeFromTheBundle()
    {
        var release = await Parse(TechniqueBundles.Enterprise());

        Assert.Equal("The MITRE Corporation", release.Source);
        Assert.Equal(
            "Copyright 2015-2025, The MITRE Corporation. MITRE ATT&CK and ATT&CK are registered trademarks of The MITRE Corporation.",
            release.Notice);
        Assert.Null(release.SourceUrl);
        // the file names no terms, so the framework's are given
        Assert.Equal("https://attack.mitre.org/resources/legal-and-branding/terms-of-use/", release.TermsUrl);
        Assert.Equal("Tactics", release.GroupLabel);
    }

    /// <summary>The ATLAS bundle names its publisher but ships no statement, so it carries no notice.</summary>
    [Fact]
    public async Task Parse_OfAtlas_NamesItsPublisherButCarriesNoNoticeTheFileDoesNotGive()
    {
        var release = await Parse(TechniqueBundles.Atlas());

        Assert.Equal("MITRE ATLAS", release.Source);
        Assert.Null(release.Notice);
        Assert.Null(release.TermsUrl);
    }

    [Fact]
    public async Task Parse_OfCapec_TakesItsNoticeFromTheBundleAndLabelsItsGroupsCategories()
    {
        var release = await Parse(CapecBundles.Capec());

        Assert.Equal("The MITRE Corporation", release.Source);
        Assert.Contains("CAPEC and the CAPEC logo are trademarks of The MITRE Corporation.", release.Notice);
        Assert.Null(release.TermsUrl);
        Assert.Equal("Categories", release.GroupLabel);
    }

    /// <summary>A bundle that carries no statement and names no publisher has none to show.</summary>
    [Fact]
    public async Task Parse_OfACustomBundle_CarriesNoPublisherOrNotice()
    {
        var release = await Parse(TechniqueBundles.Custom());

        Assert.Equal("acme-ttp", release.Domain);
        Assert.Null(release.Source);
        Assert.Null(release.SourceUrl);
        Assert.Null(release.Notice);
        Assert.Null(release.TermsUrl);
        Assert.Equal("Tactics", release.GroupLabel);
    }

    /// <summary>The domain is never guessed: a bundle that names none matches no framework that asks for one.</summary>
    [Fact]
    public async Task Parse_OfABundleThatNamesNoDomain_MatchesNoFramework()
    {
        var ex = await Assert.ThrowsAsync<InvalidTechniqueBundleException>(() => Parse(TechniqueBundles.Custom(domain: null)));

        Assert.Equal(ReleaseFileReader.NoFrameworkMessage, ex.Message);
    }
}
