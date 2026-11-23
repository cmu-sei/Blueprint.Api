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
using Blueprint.Api.Infrastructure.Exceptions;
using Blueprint.Api.Services;
using Blueprint.Api.Tests.Support;
using Xunit;

namespace Blueprint.Api.Tests.Services;

/// <summary>
/// <c>ReleaseFileReader</c> - an uploaded file's format by its shape, and the framework it is a release of by the
/// match conditions of the definitions an administrator has entered. Nothing about a framework is in the code.
/// </summary>
public class ReleaseFileReaderTests
{
    private static Task<TechniqueReleaseEntity> Parse(string json) => TechniqueFrameworks.ReadAsync(json);

    private static TechniqueReleaseEntity Read(string json, IReadOnlyCollection<TechniqueFrameworkEntity> frameworks, Guid? frameworkId = null)
    {
        using var document = JsonDocument.Parse(json);
        return ReleaseFileReader.Read(document.RootElement, null, frameworks, frameworkId);
    }

    [Fact]
    public async Task Parse_OfAStixBundle_IsAReleaseOfTheFrameworkItMatches()
    {
        var release = await Parse(TechniqueBundles.Enterprise("1.0"));

        Assert.Equal("enterprise-attack", release.Domain);
        Assert.Equal(TechniqueFrameworks.EnterpriseId, release.TechniqueFrameworkId);
        Assert.Equal(4, release.Techniques.Count);
    }

    [Fact]
    public async Task Parse_OfAnAtlasBundle_IsAnAtlasRelease()
    {
        var release = await Parse(TechniqueBundles.Atlas());

        Assert.Equal("ATLAS", release.Domain);
        Assert.Equal(TechniqueFrameworks.AtlasId, release.TechniqueFrameworkId);
    }

    /// <summary>An ATT&amp;CK bundle cites CAPEC ids too, but only as cross-references, so it does not match CAPEC.</summary>
    [Fact]
    public async Task Parse_OfACapecBundle_IsACapecRelease()
    {
        var release = await Parse(CapecBundles.Capec());

        Assert.Equal("capec", release.Domain);
        Assert.Equal(TechniqueFrameworks.CapecId, release.TechniqueFrameworkId);
        Assert.False(release.IsDefensive);
        Assert.Equal(5, release.Techniques.Count);
    }

    [Fact]
    public async Task Parse_OfAJsonLdGraph_IsAReleaseOfTheOntologyFramework()
    {
        var release = await Parse(D3fendOntologies.Ontology());

        Assert.Equal("d3fend", release.Domain);
        Assert.True(release.IsDefensive);
        Assert.Equal(TechniqueFrameworks.D3fendId, release.TechniqueFrameworkId);
        Assert.Equal(3, release.Techniques.Count);
    }

    /// <summary>A file of neither format is refused naming both.</summary>
    [Theory]
    [InlineData("""{"type":"report"}""")]
    [InlineData("""[]""")]
    public async Task Parse_OfNeitherShape_IsRefusedNamingBothFormats(string json)
    {
        var ex = await Assert.ThrowsAsync<InvalidTechniqueBundleException>(() => Parse(json));

        Assert.Contains("STIX bundle", ex.Message);
        Assert.Contains("JSON-LD ontology", ex.Message);
    }

    [Fact]
    public async Task Parse_OfSomethingThatIsNotJson_IsRefused()
    {
        var ex = await Assert.ThrowsAsync<InvalidTechniqueBundleException>(() => Parse("not json"));

        Assert.Contains("not valid JSON", ex.Message);
    }

    [Fact]
    public async Task Parse_OfAMappingsFile_SaysItIsNotARelease()
    {
        var ex = await Assert.ThrowsAsync<InvalidTechniqueBundleException>(() => Parse(D3fendMappings.Standard()));

        Assert.Contains("mappings file, not a release", ex.Message);
    }

    // ---------------------------------------------------------------------------------------------
    // Matching a file to a framework
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void Read_WithNoFrameworkDefined_IsRefused_AskingForADefinition()
    {
        var ex = Assert.Throws<InvalidTechniqueBundleException>(() => Read(TechniqueBundles.Enterprise(), []));

        Assert.Equal("No technique framework matches this file. Add or import a framework definition first.", ex.Message);
    }

    [Fact]
    public void Read_WithOnlyFrameworksOfAnotherFormat_IsRefused()
    {
        var ex = Assert.Throws<InvalidTechniqueBundleException>(() => Read(TechniqueBundles.Enterprise(), [TechniqueFrameworks.D3fend()]));

        Assert.Equal(ReleaseFileReader.NoFrameworkMessage, ex.Message);
    }

    [Fact]
    public void Read_WhenTwoFrameworksMatch_IsRefused_NamingBoth()
    {
        var again = TechniqueFrameworks.Enterprise();
        again.Id = Guid.NewGuid();
        again.Name = "Another enterprise definition";
        again.Domain = "enterprise-copy";

        var ex = Assert.Throws<InvalidTechniqueBundleException>(() => Read(TechniqueBundles.Enterprise(), [TechniqueFrameworks.Enterprise(), again]));

        Assert.Contains("more than one technique framework: Another enterprise definition, Enterprise ATT&CK", ex.Message);
    }

    [Fact]
    public void Read_WhenTwoFrameworksMatch_ReadsTheOneChosen()
    {
        var again = TechniqueFrameworks.Enterprise();
        again.Id = Guid.NewGuid();
        again.Domain = "enterprise-copy";

        var release = Read(TechniqueBundles.Enterprise(), [TechniqueFrameworks.Enterprise(), again], again.Id);

        Assert.Equal("enterprise-copy", release.Domain);
        Assert.Equal(again.Id, release.TechniqueFrameworkId);
    }

    /// <summary>A chosen framework need not match the file's conditions, but it must read the file's format.</summary>
    [Fact]
    public void Read_AsAChosenFrameworkOfAnotherFormat_IsRefused()
    {
        var ex = Assert.Throws<InvalidTechniqueBundleException>(() =>
            Read(TechniqueBundles.Enterprise(), TechniqueFrameworks.All(), TechniqueFrameworks.D3fendId));

        Assert.Contains("is a STIX bundle, but MITRE D3FEND releases are a JSON-LD ontology", ex.Message);
    }

    [Fact]
    public void Read_AsAChosenFrameworkThatIsNotThere_Is404()
    {
        Assert.Throws<EntityNotFoundException<Blueprint.Api.ViewModels.TechniqueFramework>>(() => Read(TechniqueBundles.Enterprise(), TechniqueFrameworks.All(), Guid.NewGuid()));
    }

    /// <summary>A definition with no conditions would match every file of its format, so it matches none.</summary>
    [Fact]
    public void Read_WithAFrameworkThatHasNoMatchConditions_MatchesNothing()
    {
        var bare = TechniqueFrameworks.Enterprise();
        bare.Settings.Match = new TechniqueFrameworkMatch();

        Assert.Throws<InvalidTechniqueBundleException>(() => Read(TechniqueBundles.Enterprise(), [bare]));
    }

    [Theory]
    [InlineData("collection", true)]
    [InlineData("reference", true)]
    [InlineData("prefix", true)]
    [InlineData("wrong-collection", false)]
    [InlineData("wrong-prefix", false)]
    public void Read_MatchesEveryConditionGiven(string condition, bool matches)
    {
        var framework = TechniqueFrameworks.Enterprise();
        framework.Settings.Match = condition switch
        {
            "collection" => new TechniqueFrameworkMatch { CollectionName = "Enterprise ATT&CK" },
            "reference" => new TechniqueFrameworkMatch { ReferenceSource = "mitre-attack" },
            "prefix" => new TechniqueFrameworkMatch { ReferenceSource = "mitre-attack", IdPrefix = "T" },
            "wrong-collection" => new TechniqueFrameworkMatch { StixDomain = "enterprise-attack", CollectionName = "Something else" },
            _ => new TechniqueFrameworkMatch { ReferenceSource = "mitre-attack", IdPrefix = "X-" }
        };

        using var document = JsonDocument.Parse(TechniqueBundles.Enterprise());
        Assert.Equal(matches, ReleaseFileReader.Matching(document.RootElement, TechniqueFileFormat.StixBundle, [framework]).Count == 1);
    }

    [Theory]
    [InlineData("D3FEND", true)]
    [InlineData("something else", false)]
    public void Read_MatchesAnOntologyByItsTitle(string title, bool matches)
    {
        var framework = TechniqueFrameworks.D3fend();
        framework.Settings.Match = new TechniqueFrameworkMatch { OntologyTitleContains = title };

        using var document = JsonDocument.Parse(D3fendOntologies.Ontology());
        Assert.Equal(matches, ReleaseFileReader.Matching(document.RootElement, TechniqueFileFormat.JsonLdOntology, [framework]).Count == 1);
    }
}
