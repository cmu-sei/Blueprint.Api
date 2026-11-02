// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System.IO;
using System.Text;
using System.Threading.Tasks;
using Blueprint.Api.Data.Models;
using Blueprint.Api.Infrastructure.Exceptions;
using Blueprint.Api.Services;
using Blueprint.Api.Tests.Support;
using Xunit;

namespace Blueprint.Api.Tests.Services;

/// <summary><c>ReleaseFileParser</c> - which parser an uploaded file goes to, by its shape rather than its name.</summary>
public class ReleaseFileParserTests
{
    private static async Task<AttackReleaseEntity> Parse(string json) =>
        await ReleaseFileParser.ParseAsync(new MemoryStream(Encoding.UTF8.GetBytes(json)), null, TestContext.Current.CancellationToken);

    [Fact]
    public async Task Parse_OfAStixBundle_IsAnAttackRelease()
    {
        var release = await Parse(AttackBundles.Enterprise("1.0"));

        Assert.Equal("enterprise-attack", release.Domain);
        Assert.Equal(4, release.Techniques.Count);
    }

    [Fact]
    public async Task Parse_OfAnAtlasBundle_IsAnAtlasRelease()
    {
        var release = await Parse(AttackBundles.Atlas());

        Assert.Equal("ATLAS", release.Domain);
    }

    [Fact]
    public async Task Parse_OfAJsonLdGraph_IsAD3fendRelease()
    {
        var release = await Parse(D3fendOntologies.Ontology());

        Assert.Equal("d3fend", release.Domain);
        Assert.True(release.IsDefensive);
        Assert.Equal(3, release.Techniques.Count);
    }

    /// <summary>A file of neither shape is refused with both formats named, so the administrator knows what to upload.</summary>
    [Theory]
    [InlineData("""{"type":"report"}""")]
    [InlineData("""[]""")]
    public async Task Parse_OfNeitherShape_IsRefusedNamingBothFormats(string json)
    {
        var ex = await Assert.ThrowsAsync<InvalidAttackBundleException>(() => Parse(json));

        Assert.Contains("STIX bundle", ex.Message);
        Assert.Contains("D3FEND ontology", ex.Message);
    }

    [Fact]
    public async Task Parse_OfSomethingThatIsNotJson_IsRefused()
    {
        var ex = await Assert.ThrowsAsync<InvalidAttackBundleException>(() => Parse("not json"));

        Assert.Contains("not valid JSON", ex.Message);
    }
}
