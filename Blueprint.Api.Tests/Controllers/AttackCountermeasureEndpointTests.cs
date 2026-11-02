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

    /// <summary>The import rule is "the release is defensive", not "its domain is d3fend".</summary>
    [Fact]
    public async Task Import_OntoADefensiveReleaseOfAnotherDomain_Is201_AndServesTheMsel()
    {
        var attack = await SeedRelease("1.0");
        var defensive = await D3fendOntologyParser.ParseAsync(
            new MemoryStream(Encoding.UTF8.GetBytes(D3fendOntologies.Ontology("2.0"))), null, Ct);
        defensive.Domain = "other-defend";
        await Seed(defensive);
        var msel = TestData.Msel();
        await Seed(msel);
        await Seed(new MselAttackTechniqueEntity(msel.Id, Technique(attack, "T1059").Id) { Id = Guid.NewGuid() });

        var response = await Upload(RootClient, defensive.Id, D3fendMappings.Standard());

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var result = await Get<MselCountermeasures>(RootClient, $"api/msels/{msel.Id}/attacktechniques/countermeasures");
        Assert.Equal(defensive.Id, result.AttackReleaseId);
        Assert.Equal(["D3-FA"], Assert.Single(result.Techniques).Countermeasures.Select(c => c.DefensiveTechnique.ExternalId));
    }

    [Fact]
    public async Task Import_OntoAnAttackRelease_Is400()
    {
        var attack = await SeedRelease("1.0");

        var response = await Upload(RootClient, attack.Id, D3fendMappings.Standard());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("not a defensive release", await response.Content.ReadAsStringAsync(Ct));
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
    // One technique's countermeasures, from a release
    // ---------------------------------------------------------------------------------------------

    /// <summary>Mappings are reference data like the releases, so any signed-in user reads them.</summary>
    [Fact]
    public async Task GetByRelease_ForAnyUser_RanksATechniquesCountermeasuresByWeight()
    {
        var d3fend = await SeedD3fendRelease("1.6.0");
        await Upload(RootClient, d3fend.Id, D3fendMappings.Standard());
        var actor = await Actor().SeedAsync();

        var list = await Get<List<AttackTechniqueCountermeasure>>(Client(actor), $"api/attackreleases/{d3fend.Id}/countermeasures?offensiveId=T1003.005");

        Assert.Equal(["D3-CH", "D3-CBAN"], list.Select(c => c.DefensiveTechnique.ExternalId));
        Assert.Equal([3, 1], list.Select(c => c.Weight));
        Assert.All(list, c => Assert.False(c.InheritedFromParent));
        Assert.All(list, c => Assert.Equal("T1003.005", c.OffensiveExternalId));
        Assert.All(list, c => Assert.Equal(d3fend.Id, c.AttackReleaseId));
        var hardening = list[0];
        Assert.Equal(Technique(d3fend, "D3-CH").Id, hardening.DefensiveTechniqueId);
        Assert.Equal("d3fend", hardening.DefensiveTechnique.Domain);
        Assert.True(hardening.DefensiveTechnique.IsDefensive);
        Assert.Equal("Credential Hardening", hardening.DefensiveTechnique.Name);
        Assert.Equal("Harden", hardening.DefensiveTactic);
        Assert.Equal("hardens Credential; T1003.005 accesses Encrypted Credential", hardening.Rationale);
    }

    /// <summary>D3FEND maps many techniques only at the parent, so a sub-technique with none of its own is answered with the parent's.</summary>
    [Fact]
    public async Task GetByRelease_ForASubTechniqueWithoutMappings_AnswersWithItsParents_Flagged()
    {
        var d3fend = await SeedD3fendRelease("1.6.0");
        await Upload(RootClient, d3fend.Id, D3fendMappings.Standard());

        var list = await Get<List<AttackTechniqueCountermeasure>>(RootClient, $"api/attackreleases/{d3fend.Id}/countermeasures?offensiveId=T1059.001");

        var analysis = Assert.Single(list);
        Assert.Equal("D3-FA", analysis.DefensiveTechnique.ExternalId);
        Assert.Equal("T1059", analysis.OffensiveExternalId);
        Assert.True(analysis.InheritedFromParent);
    }

    [Fact]
    public async Task GetByRelease_ForATechniqueNothingCounters_IsEmpty()
    {
        var d3fend = await SeedD3fendRelease("1.6.0");
        await Upload(RootClient, d3fend.Id, D3fendMappings.Standard());

        Assert.Empty(await Get<List<AttackTechniqueCountermeasure>>(RootClient, $"api/attackreleases/{d3fend.Id}/countermeasures?offensiveId=T1053"));
        // a technique, not a sub-technique: there is no parent to fall back to
        Assert.Empty(await Get<List<AttackTechniqueCountermeasure>>(RootClient, $"api/attackreleases/{d3fend.Id}/countermeasures?offensiveId=AML.T0000"));
    }

    [Fact]
    public async Task GetByRelease_WithoutAnId_ListsEveryMapping()
    {
        var d3fend = await SeedD3fendRelease("1.6.0");
        await Upload(RootClient, d3fend.Id, D3fendMappings.Standard());

        var list = await Get<List<AttackTechniqueCountermeasure>>(RootClient, $"api/attackreleases/{d3fend.Id}/countermeasures");

        Assert.Equal(["T0800", "T1003", "T1003.005", "T1003.005", "T1059"], list.Select(c => c.OffensiveExternalId));
        Assert.Equal(["D3-CH", "D3-CBAN"], list.Where(c => c.OffensiveExternalId == "T1003.005").Select(c => c.DefensiveTechnique.ExternalId));
    }

    [Fact]
    public async Task GetByRelease_OfAReleaseThatIsNotThere_Is404()
    {
        var response = await RootClient.GetAsync($"api/attackreleases/{Guid.NewGuid()}/countermeasures?offensiveId=T1003", Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ---------------------------------------------------------------------------------------------
    // A MSEL's whole attack pool
    // ---------------------------------------------------------------------------------------------

    /// <summary>The MSEL's defensive techniques pin a D3FEND release, so its mappings are the ones offered - not a newer release's.</summary>
    [Fact]
    public async Task GetByMsel_ForAViewer_UsesThePinnedDefensiveRelease_AndGroupsByTechnique()
    {
        var attack = await SeedRelease("1.0");
        var pinned = await SeedD3fendRelease("1.6.0");
        var newer = await SeedD3fendRelease("1.7.0");
        await Upload(RootClient, pinned.Id, D3fendMappings.Standard());
        await Upload(RootClient, newer.Id, D3fendMappings.Standard());
        var msel = TestData.Msel();
        await Seed(msel);
        await Seed(
            new MselAttackTechniqueEntity(msel.Id, Technique(attack, "T1003").Id) { Id = Guid.NewGuid() },
            new MselAttackTechniqueEntity(msel.Id, Technique(attack, "T1059.001").Id) { Id = Guid.NewGuid() },
            new MselAttackTechniqueEntity(msel.Id, Technique(attack, "T1053").Id) { Id = Guid.NewGuid() },
            new MselAttackTechniqueEntity(msel.Id, Technique(pinned, "D3-CH").Id) { Id = Guid.NewGuid() });
        var viewer = await Actor().OnMsel(msel, MselRole.Viewer).SeedAsync();

        var result = await Get<MselCountermeasures>(Client(viewer), $"api/msels/{msel.Id}/attacktechniques/countermeasures");

        Assert.Equal(msel.Id, result.MselId);
        Assert.Equal(pinned.Id, result.AttackReleaseId);
        Assert.True(result.HasMappings);
        Assert.Equal(["T1003", "T1053", "T1059.001"], result.Techniques.Select(t => t.OffensiveExternalId));
        var dumping = result.Techniques[0];
        Assert.Equal(Technique(attack, "T1003").Id, dumping.AttackTechniqueId);
        Assert.Null(dumping.InheritedFrom);
        var hardening = Assert.Single(dumping.Countermeasures);
        Assert.Equal("D3-CH", hardening.DefensiveTechnique.ExternalId);
        Assert.False(hardening.InheritedFromParent);
        Assert.Equal(pinned.Id, hardening.AttackReleaseId);
        Assert.Equal(Technique(pinned, "D3-CH").Id, hardening.DefensiveTechniqueId);
        Assert.Equal("hardens Credential; T1003 may access Credential", hardening.Rationale);
        Assert.Empty(result.Techniques[1].Countermeasures);
        Assert.Null(result.Techniques[1].InheritedFrom);
        var interpreter = result.Techniques[2];
        Assert.Equal("T1059", interpreter.InheritedFrom);
        Assert.Equal(["D3-FA"], interpreter.Countermeasures.Select(c => c.DefensiveTechnique.ExternalId));
        Assert.All(interpreter.Countermeasures, c => Assert.True(c.InheritedFromParent));
    }

    [Fact]
    public async Task GetByMsel_WithoutDefensiveTechniques_UsesTheNewestD3fendReleaseWithMappings()
    {
        var attack = await SeedRelease("1.0");
        var withMappings = await SeedD3fendRelease("1.6.0");
        await SeedD3fendRelease("1.7.0");
        await Upload(RootClient, withMappings.Id, D3fendMappings.Standard());
        var msel = TestData.Msel();
        await Seed(msel);
        await Seed(new MselAttackTechniqueEntity(msel.Id, Technique(attack, "T1059").Id) { Id = Guid.NewGuid() });

        var result = await Get<MselCountermeasures>(RootClient, $"api/msels/{msel.Id}/attacktechniques/countermeasures");

        Assert.Equal(withMappings.Id, result.AttackReleaseId);
        Assert.True(result.HasMappings);
        Assert.Equal(["D3-FA"], Assert.Single(result.Techniques).Countermeasures.Select(c => c.DefensiveTechnique.ExternalId));
    }

    /// <summary>A pinned release without mappings is still the one named, so the client can say which release wants them.</summary>
    [Fact]
    public async Task GetByMsel_WithAPinnedReleaseWithoutMappings_SaysSo()
    {
        var attack = await SeedRelease("1.0");
        var older = await SeedD3fendRelease("1.6.0");
        var pinned = await SeedD3fendRelease("1.7.0");
        await Upload(RootClient, older.Id, D3fendMappings.Standard());
        var msel = TestData.Msel();
        await Seed(msel);
        await Seed(
            new MselAttackTechniqueEntity(msel.Id, Technique(attack, "T1059").Id) { Id = Guid.NewGuid() },
            new MselAttackTechniqueEntity(msel.Id, Technique(pinned, "D3-CH").Id) { Id = Guid.NewGuid() });

        var result = await Get<MselCountermeasures>(RootClient, $"api/msels/{msel.Id}/attacktechniques/countermeasures");

        Assert.Equal(pinned.Id, result.AttackReleaseId);
        Assert.False(result.HasMappings);
        Assert.Empty(Assert.Single(result.Techniques).Countermeasures);
    }

    [Fact]
    public async Task GetByMsel_WithNoD3fendRelease_NamesNone()
    {
        var attack = await SeedRelease("1.0");
        var msel = TestData.Msel();
        await Seed(msel);
        await Seed(new MselAttackTechniqueEntity(msel.Id, Technique(attack, "T1059").Id) { Id = Guid.NewGuid() });

        var result = await Get<MselCountermeasures>(RootClient, $"api/msels/{msel.Id}/attacktechniques/countermeasures");

        Assert.Null(result.AttackReleaseId);
        Assert.False(result.HasMappings);
        Assert.Equal("T1059", Assert.Single(result.Techniques).OffensiveExternalId);
    }

    [Fact]
    public async Task GetByMsel_IsForbiddenForAUserNotOnTheMsel()
    {
        var msel = TestData.Msel();
        await Seed(msel);
        var stranger = await Actor().SeedAsync();

        var response = await Client(stranger).GetAsync($"api/msels/{msel.Id}/attacktechniques/countermeasures", Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
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
