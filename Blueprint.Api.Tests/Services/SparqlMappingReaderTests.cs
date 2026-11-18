// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

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
/// <c>SparqlMappingReader</c> - a mappings file in SPARQL results JSON into weighted pairs, read with the variable
/// names of a mapping definition. The fixture and definition are the test D3FEND ones.
/// </summary>
public class SparqlMappingReaderTests
{
    private static Task<SparqlMappingReader.Result> Parse(string json, TechniqueMappingDefinition definition = null) =>
        SparqlMappingReader.ReadAsync(new MemoryStream(Encoding.UTF8.GetBytes(json)), definition ?? TechniqueFrameworks.D3fendMappings(), TestContext.Current.CancellationToken);

    [Fact]
    public async Task Parse_CountsEveryRow_AndAggregatesThemIntoPairs()
    {
        var result = await Parse(D3fendMappings.Standard());

        Assert.Equal(9, result.RowsRead);
        Assert.Equal(
            [
                ("T0800", "FileAnalysis"),
                ("T1003", "CredentialHardening"),
                ("T1003.005", "CertificateBasedAuthentication"),
                ("T1003.005", "CredentialHardening"),
                ("T1003.005", "NetworkTrafficFiltering"),
                ("T1059", "FileAnalysis"),
            ],
            result.Pairs.Select(p => (p.SourceId, p.Target)).Order());
    }

    /// <summary>The same path is listed once per query that inherits it; the weight counts paths, not rows.</summary>
    [Fact]
    public async Task Parse_WeighsAPairByItsDistinctArtifactPaths()
    {
        var result = await Parse(D3fendMappings.Standard());

        Assert.Equal(3, Pair(result, "T1003.005", "CredentialHardening").Weight);
        Assert.Equal(1, Pair(result, "T1003.005", "CertificateBasedAuthentication").Weight);
        Assert.Equal(1, Pair(result, "T0800", "FileAnalysis").Weight);
    }

    [Fact]
    public async Task Parse_WordsTheRationaleFromTheMostCommonPath_AndSpellsOutTheRelation()
    {
        var result = await Parse(D3fendMappings.Standard());

        Assert.Equal("hardens Credential; T1003.005 accesses Encrypted Credential", Pair(result, "T1003.005", "CredentialHardening").Rationale);
        Assert.Equal("hardens Credential; T1003 may access Credential", Pair(result, "T1003", "CredentialHardening").Rationale);
    }

    [Fact]
    public async Task Parse_TakesTheTacticLabelAndTheClassLocalName()
    {
        var result = await Parse(D3fendMappings.Standard());

        var pair = Pair(result, "T1003.005", "NetworkTrafficFiltering");
        Assert.Equal("Isolate", pair.Tactic);
        Assert.Equal("Detect", Pair(result, "T1059", "FileAnalysis").Tactic);
    }

    /// <summary>A sub-technique with no rows of its own has no pair; the fallback to its parent is the service's.</summary>
    [Fact]
    public async Task Parse_GivesASubTechniqueWithoutRowsNoPair()
    {
        var result = await Parse(D3fendMappings.Standard());

        Assert.DoesNotContain(result.Pairs, p => p.SourceId == "T1059.001");
    }

    /// <summary>
    /// The real file is 60 MB, read a buffer at a time: a row split across two reads must come out whole, and a row
    /// longer than the buffer must still be read.
    /// </summary>
    [Fact]
    public async Task Parse_ReadsAFileLargerThanItsBuffer_RowByRow()
    {
        var rows = Enumerable.Range(0, 3000)
            .Select(i => D3fendMappings.Row($"T{1000 + i}", $"Technique {i}", null, "enterprise", "FileAnalysis", "File Analysis", "Detect",
                "analyzes", "File", "produces", "File", padding: new string('x', 400)))
            .Append(D3fendMappings.Row("T9999", "Long", null, "enterprise", "CredentialHardening", "Credential Hardening", "Harden",
                "hardens", "Credential", "accesses", "Credential", padding: new string('y', 600 * 1024)))
            .ToArray();
        var json = D3fendMappings.File(rows);
        Assert.True(json.Length > 1024 * 1024);

        var result = await Parse(json);

        Assert.Equal(3001, result.RowsRead);
        Assert.Equal(3001, result.Pairs.Count);
        Assert.Equal(1, Pair(result, "T9999", "CredentialHardening").Weight);
        Assert.Equal("analyzes File; T2999 produces File", Pair(result, "T2999", "FileAnalysis").Rationale);
    }

    [Fact]
    public async Task Parse_RejectsAStixBundle()
    {
        var ex = await Assert.ThrowsAsync<InvalidTechniqueBundleException>(() => Parse(TechniqueBundles.Enterprise()));

        Assert.Contains("not a mappings file", ex.Message);
    }

    /// <summary>The variables come from the definition: name others and the same file has none of them.</summary>
    [Fact]
    public async Task Parse_WithADefinitionNamingOtherVariables_RejectsTheFile_NamingThem()
    {
        var definition = TechniqueFrameworks.D3fendMappings();
        definition.SourceIdVariable = "subject";
        definition.TargetVariable = "object";

        var ex = await Assert.ThrowsAsync<InvalidTechniqueBundleException>(() => Parse(D3fendMappings.Standard(), definition));

        Assert.Contains("subject and object", ex.Message);
    }

    /// <summary>Matching by id keeps the target as the file gives it, rather than taking its local name.</summary>
    [Fact]
    public async Task Parse_MatchingTargetsById_KeepsTheTargetAsGiven()
    {
        var definition = TechniqueFrameworks.D3fendMappings();
        definition.TargetMatch = MappingTargetMatch.ExternalId;

        var result = await Parse(D3fendMappings.Standard(), definition);

        Assert.All(result.Pairs, p => Assert.Contains("#", p.Target));
    }

    [Fact]
    public async Task Parse_WithADefinitionThatNamesNoTargetVariable_IsRefused()
    {
        var definition = TechniqueFrameworks.D3fendMappings();
        definition.TargetVariable = null;

        await Assert.ThrowsAsync<InvalidTechniqueBundleException>(() => Parse(D3fendMappings.Standard(), definition));
    }

    [Fact]
    public async Task Parse_RejectsSparqlResultsOfAnotherQuery()
    {
        var ex = await Assert.ThrowsAsync<InvalidTechniqueBundleException>(() => Parse(D3fendMappings.OtherSparqlResults()));

        Assert.Contains("off_tech_id", ex.Message);
    }

    [Fact]
    public async Task Parse_RejectsAFileThatIsNotJson()
    {
        var ex = await Assert.ThrowsAsync<InvalidTechniqueBundleException>(() => Parse("off_tech_id,def_tech\nT1003,CredentialHardening"));

        Assert.StartsWith("The file is not valid JSON", ex.Message);
    }

    [Fact]
    public async Task Parse_RejectsAMappingsFileWithNoRows()
    {
        await Assert.ThrowsAsync<InvalidTechniqueBundleException>(() => Parse(D3fendMappings.File()));
    }

    private static SparqlMappingReader.Pair Pair(SparqlMappingReader.Result result, string sourceId, string target) =>
        result.Pairs.Single(p => p.SourceId == sourceId && p.Target == target);
}
