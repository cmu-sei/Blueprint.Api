// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Threading.Tasks;
using Blueprint.Api.Data.Enumerations;
using Blueprint.Api.Data.Models;
using Blueprint.Api.Services;
using Blueprint.Api.Tests.Support;
using Blueprint.Api.ViewModels;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Blueprint.Api.Tests.Controllers;

/// <summary>
/// The D3FEND "countered by" mappings: imported onto a D3FEND release from d3fend-full-mappings.json
/// (<c>AttackReleaseController</c>), then read per offensive technique and for a MSEL's whole attack pool.
/// </summary>
/// <remarks>401 and 403 for these routes live in <see cref="RouteAuthorizationTests"/>.</remarks>
public class AttackCountermeasureEndpointTests(DatabaseFixture fixture, BlueprintAppFactory factory)
    : ApiTestBase(fixture, factory), IClassFixture<BlueprintAppFactory>
{
    // ---------------------------------------------------------------------------------------------
    // Import
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Import_WithManageCompetencyFrameworks_Is201_CountingRowsPairsAndSkips()
    {
        var d3fend = await SeedD3fendRelease("1.6.0");
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageCompetencyFrameworks).SeedAsync();

        var response = await Upload(Client(actor), d3fend.Id, D3fendMappings.Standard());

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var result = await Read<AttackTechniqueCountermeasureImport>(response);
        Assert.Equal(d3fend.Id, result.AttackReleaseId);
        Assert.Equal(9, result.RowsRead);
        Assert.Equal(5, result.PairsStored);
        // Network Traffic Filtering is not a technique of this release
        Assert.Equal(1, result.PairsSkipped);
        Assert.Equal(4, result.OffensiveTechniqueCount);
        var stored = await ReadBack(rb => rb.AttackTechniqueCountermeasures
            .Where(c => c.ReleaseId == d3fend.Id)
            .Include(c => c.DefensiveTechnique)
            .ToListAsync(Ct));
        var pair = stored.Single(c => c.OffensiveExternalId == "T1003.005" && c.DefensiveTechnique.ExternalId == "D3-CH");
        Assert.Equal(3, pair.Weight);
        Assert.Equal("Harden", pair.DefensiveTactic);
        Assert.Equal("hardens Credential; T1003.005 accesses Encrypted Credential", pair.Rationale);
    }

    [Fact]
    public async Task Import_CountsOnTheRelease()
    {
        var d3fend = await SeedD3fendRelease("1.6.0");
        await Upload(RootClient, d3fend.Id, D3fendMappings.Standard());

        var release = await Get<AttackRelease>(RootClient, $"api/attackreleases/{d3fend.Id}");

        Assert.Equal(5, release.CountermeasureCount);
    }

    /// <summary>A re-import replaces the release's rows rather than adding to them or tripping the unique index.</summary>
    [Fact]
    public async Task Import_Again_ReplacesTheReleasesMappings()
    {
        var d3fend = await SeedD3fendRelease("1.6.0");
        await Upload(RootClient, d3fend.Id, D3fendMappings.Standard());
        var smaller = D3fendMappings.File(
            D3fendMappings.Row("T1059", "Command and Scripting Interpreter", null, "enterprise", "FileAnalysis", "File Analysis", "Detect",
                "analyzes", "File", "executes", "Executable Script"));

        var response = await Upload(RootClient, d3fend.Id, smaller);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(1, (await Read<AttackTechniqueCountermeasureImport>(response)).PairsStored);
        var stored = await ReadBack(rb => rb.AttackTechniqueCountermeasures.Where(c => c.ReleaseId == d3fend.Id).Select(c => c.OffensiveExternalId).ToListAsync(Ct));
        Assert.Equal(["T1059"], stored);
    }

    [Fact]
    public async Task Import_OfAFileThatIsNotTheMappings_Is400()
    {
        var d3fend = await SeedD3fendRelease("1.6.0");

        var response = await Upload(RootClient, d3fend.Id, """{"type":"bundle","objects":[]}""");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("d3fend-full-mappings.json", await response.Content.ReadAsStringAsync(Ct));
        Assert.False(await ReadBack(rb => rb.AttackTechniqueCountermeasures.AnyAsync(c => c.ReleaseId == d3fend.Id, Ct)));
    }

    [Fact]
    public async Task Import_OntoAnAttackRelease_Is400()
    {
        var attack = await SeedRelease("1.0");

        var response = await Upload(RootClient, attack.Id, D3fendMappings.Standard());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("not a D3FEND release", await response.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task Import_OntoAReleaseThatIsNotThere_Is404()
    {
        var response = await Upload(RootClient, Guid.NewGuid(), D3fendMappings.Standard());

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>The mappings file is not a release; the release endpoint says where it goes.</summary>
    [Fact]
    public async Task ImportRelease_OfTheMappingsFile_Is400_PointingAtTheD3fendRelease()
    {
        using var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(D3fendMappings.Standard()));
        file.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        content.Add(file, "ToUpload", "d3fend-full-mappings.json");

        var response = await RootClient.PostAsync("api/attackreleases/import", content, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("import the mappings onto that release", await response.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task DeleteRelease_TakesItsMappingsWithIt()
    {
        var d3fend = await SeedD3fendRelease("1.6.0");
        await Upload(RootClient, d3fend.Id, D3fendMappings.Standard());

        var response = await RootClient.DeleteAsync($"api/attackreleases/{d3fend.Id}", Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.False(await ReadBack(rb => rb.AttackTechniqueCountermeasures.AnyAsync(c => c.ReleaseId == d3fend.Id, Ct)));
    }

    // ---------------------------------------------------------------------------------------------

    private async Task<AttackReleaseEntity> SeedRelease(string version)
    {
        var release = await AttackStixParser.ParseAsync(
            new MemoryStream(Encoding.UTF8.GetBytes(AttackBundles.Enterprise(version))), null, Ct);
        await Seed(release);
        return release;
    }

    private async Task<AttackReleaseEntity> SeedD3fendRelease(string version)
    {
        var release = await D3fendOntologyParser.ParseAsync(
            new MemoryStream(Encoding.UTF8.GetBytes(D3fendOntologies.Ontology(version))), null, Ct);
        await Seed(release);
        return release;
    }

    private static AttackTechniqueEntity Technique(AttackReleaseEntity release, string externalId) =>
        release.Techniques.Single(t => t.ExternalId == externalId);

    /// <remarks>Awaited inside the <c>using</c>: <c>TestServer</c> reads the body during <c>SendAsync</c>.</remarks>
    private async Task<HttpResponseMessage> Upload(HttpClient client, Guid releaseId, string json)
    {
        using var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(json));
        file.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        content.Add(file, "ToUpload", "d3fend-full-mappings.json");

        return await client.PostAsync($"api/attackreleases/{releaseId}/countermeasures", content, Ct);
    }

    private async Task<T> Get<T>(HttpClient client, string url)
    {
        var response = await client.GetAsync(url, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await Read<T>(response);
    }

    private async Task<T> Read<T>(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<T>(JsonOptions, Ct);
}
