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
/// <c>AttackReleaseController</c> and <c>MselAttackTechniqueController</c> - the ATT&amp;CK releases an
/// administrator imports, and the per-MSEL technique pool that AttackTechnique columns pick from. Also the two
/// ways a pool leaves its MSEL: copy, and JSON export to another installation.
/// </summary>
/// <remarks>401 and 403 for these routes live in <see cref="RouteAuthorizationTests"/>.</remarks>
public class AttackEndpointTests(DatabaseFixture fixture, BlueprintAppFactory factory)
    : ApiTestBase(fixture, factory), IClassFixture<BlueprintAppFactory>
{
    // ---------------------------------------------------------------------------------------------
    // Releases
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Import_WithManageCompetencyFrameworks_Is201_CountingWhatItRead()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageCompetencyFrameworks).SeedAsync();

        var response = await Upload(Client(actor), AttackBundles.Enterprise("1.0"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var release = await Read<AttackRelease>(response);
        Assert.Equal(AttackStixParser.ReleaseId("enterprise-attack", "1.0"), release.Id);
        Assert.Equal(2, release.TacticCount);
        Assert.Equal(4, release.TechniqueCount);
        Assert.Equal(4, await ReadBack(rb => rb.AttackTechniques.CountAsync(t => t.AttackReleaseId == release.Id, Ct)));
    }

    /// <summary>ATLAS shares ATT&amp;CK's data model, so the same endpoint imports it as a release of its own domain.</summary>
    [Fact]
    public async Task Import_OfAnAtlasBundle_Is201_AsAnAtlasRelease()
    {
        var response = await Upload(RootClient, AttackBundles.Atlas(), "stix-atlas.json");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var release = await Read<AttackRelease>(response);
        Assert.Equal(AttackStixParser.ReleaseId("ATLAS", "2026.10"), release.Id);
        Assert.Equal("ATLAS", release.Domain);
        Assert.Equal(2, release.TacticCount);
        Assert.Equal(3, release.TechniqueCount);
        var ids = await ReadBack(rb => rb.AttackTechniques.Where(t => t.AttackReleaseId == release.Id).Select(t => t.ExternalId).ToListAsync(Ct));
        Assert.Equal(["AML.T0000", "AML.T0000.000", "AML.T0040"], ids.Order());
    }

    [Fact]
    public async Task Import_OfAReleaseAlreadyThere_Is409()
    {
        await SeedRelease("1.0");

        var response = await Upload(RootClient, AttackBundles.Enterprise("1.0"));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Import_OfAFileThatIsNotABundle_Is400()
    {
        var response = await Upload(RootClient, """{"type":"report"}""");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>Releases are reference data every MSEL screen loads, so any signed-in user reads them.</summary>
    [Fact]
    public async Task GetTechniques_ForAnyUser_AnswersTacticsPlatformsAndParent()
    {
        var release = await SeedRelease("1.0");
        var actor = await Actor().SeedAsync();

        var techniques = await Get<List<AttackTechnique>>(Client(actor), $"api/attackreleases/{release.Id}/techniques");
        var tactics = await Get<List<AttackTactic>>(Client(actor), $"api/attackreleases/{release.Id}/tactics");

        var powershell = techniques.Single(t => t.ExternalId == "T1059.001");
        Assert.True(powershell.IsSubTechnique);
        Assert.Equal(techniques.Single(t => t.ExternalId == "T1059").Id, powershell.ParentId);
        Assert.Equal(["execution", "persistence"], techniques.Single(t => t.ExternalId == "T1053").Tactics);
        Assert.Equal(["Windows"], powershell.Platforms);
        Assert.Equal(["TA0003", "TA0002"], tactics.Select(t => t.ExternalId));
    }

    [Fact]
    public async Task Delete_OfAReleaseNoMselUses_Is204_TakingItsTechniques()
    {
        var release = await SeedRelease("1.0");

        var response = await RootClient.DeleteAsync($"api/attackreleases/{release.Id}", Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.False(await ReadBack(rb => rb.AttackTechniques.AnyAsync(t => t.AttackReleaseId == release.Id, Ct)));
    }

    [Fact]
    public async Task Delete_OfAReleaseAMselUses_Is409_AndKeepsIt()
    {
        var release = await SeedRelease("1.0");
        var msel = TestData.Msel();
        await Seed(msel);
        await Seed(new MselAttackTechniqueEntity(msel.Id, Technique(release, "T1059").Id) { Id = Guid.NewGuid() });

        var response = await RootClient.DeleteAsync($"api/attackreleases/{release.Id}", Ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.True(await ReadBack(rb => rb.AttackReleases.AnyAsync(r => r.Id == release.Id, Ct)));
    }

    // ---------------------------------------------------------------------------------------------
    // The MSEL's pool
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Create_ForAnOwnerOfTheMsel_Is201_AndAViewerReadsIt()
    {
        var release = await SeedRelease("1.0");
        var msel = TestData.Msel();
        await Seed(msel);
        var owner = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();
        var viewer = await Actor().OnMsel(msel, MselRole.Viewer).SeedAsync();

        var response = await Client(owner).PostAsJsonAsync(
            "api/mselattacktechniques", Body(msel.Id, Technique(release, "T1059.001").Id), Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var pool = await Get<List<MselAttackTechnique>>(Client(viewer), $"api/msels/{msel.Id}/mselattacktechniques");
        Assert.Equal("T1059.001", Assert.Single(pool).AttackTechnique.ExternalId);
    }

    [Fact]
    public async Task Create_IsForbiddenForAnEditorOfTheMsel()
    {
        var release = await SeedRelease("1.0");
        var msel = TestData.Msel();
        await Seed(msel);
        var editor = await Actor().OnMsel(msel, MselRole.Editor).SeedAsync();

        var response = await Client(editor).PostAsJsonAsync(
            "api/mselattacktechniques", Body(msel.Id, Technique(release, "T1059").Id), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>A pool is pinned to one release; a technique from another is refused until it is upgraded.</summary>
    [Fact]
    public async Task Create_OfATechniqueFromAnotherRelease_Is409()
    {
        var first = await SeedRelease("1.0");
        var second = await SeedRelease("2.0");
        var msel = TestData.Msel();
        await Seed(msel);
        await Seed(new MselAttackTechniqueEntity(msel.Id, Technique(first, "T1059").Id) { Id = Guid.NewGuid() });

        var response = await RootClient.PostAsJsonAsync(
            "api/mselattacktechniques", Body(msel.Id, Technique(second, "T1053").Id), Ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(1, await ReadBack(rb => rb.MselAttackTechniques.CountAsync(m => m.MselId == msel.Id, Ct)));
    }

    [Fact]
    public async Task Create_OfATechniqueAlreadyInThePool_Is409()
    {
        var release = await SeedRelease("1.0");
        var msel = TestData.Msel();
        await Seed(msel);
        await Seed(new MselAttackTechniqueEntity(msel.Id, Technique(release, "T1059").Id) { Id = Guid.NewGuid() });

        var response = await RootClient.PostAsJsonAsync(
            "api/mselattacktechniques", Body(msel.Id, Technique(release, "T1059").Id), Ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task DeleteByIds_ForAnOwnerOfTheMsel_Is204()
    {
        var release = await SeedRelease("1.0");
        var msel = TestData.Msel();
        await Seed(msel);
        var technique = Technique(release, "T1059");
        await Seed(new MselAttackTechniqueEntity(msel.Id, technique.Id) { Id = Guid.NewGuid() });
        var owner = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        var response = await Client(owner).DeleteAsync($"api/msels/{msel.Id}/attacktechniques/{technique.Id}", Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.False(await ReadBack(rb => rb.MselAttackTechniques.AnyAsync(m => m.MselId == msel.Id, Ct)));
    }

    [Fact]
    public async Task Upgrade_KeepsWhatTheNewReleaseHas_AndReportsWhatItDropped()
    {
        var first = await SeedRelease("1.0");
        var second = await SeedRelease("2.0");
        var msel = TestData.Msel();
        await Seed(msel);
        await Seed(
            new MselAttackTechniqueEntity(msel.Id, Technique(first, "T1059").Id) { Id = Guid.NewGuid() },
            new MselAttackTechniqueEntity(msel.Id, Technique(first, "T1003").Id) { Id = Guid.NewGuid() });
        var owner = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        var response = await Client(owner).PostAsync($"api/msels/{msel.Id}/attacktechniques/upgrade/{second.Id}", null, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await Read<MselAttackTechniqueUpgrade>(response);
        Assert.Equal(1, result.Kept);
        Assert.Equal(["T1003"], result.Dropped);
        var pool = await ReadBack(rb => rb.MselAttackTechniques.Where(m => m.MselId == msel.Id).Select(m => m.AttackTechniqueId).ToListAsync(Ct));
        Assert.Equal([Technique(second, "T1059").Id], pool);
    }

    // ---------------------------------------------------------------------------------------------
    // Copy and JSON
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Copy_CarriesThePool()
    {
        var release = await SeedRelease("1.0");
        var msel = TestData.Msel();
        await Seed(msel);
        await Seed(new MselAttackTechniqueEntity(msel.Id, Technique(release, "T1059").Id) { Id = Guid.NewGuid() });

        var response = await RootClient.PostAsync($"api/msels/{msel.Id}/copy", null, Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var copy = await Read<Msel>(response);
        Assert.Equal(
            Technique(release, "T1059").Id,
            await ReadBack(rb => rb.MselAttackTechniques.Where(m => m.MselId == copy.Id).Select(m => m.AttackTechniqueId).SingleAsync(Ct)));
    }

    [Fact]
    public async Task JsonImport_KeepsTechniquesWhoseReleaseIsHere_AndDropsTheRest()
    {
        var release = await SeedRelease("1.0");
        var msel = TestData.Msel();
        await Seed(msel);
        var kept = Technique(release, "T1059");
        var lost = Technique(release, "T1053");
        await Seed(
            new MselAttackTechniqueEntity(msel.Id, kept.Id) { Id = Guid.NewGuid() },
            new MselAttackTechniqueEntity(msel.Id, lost.Id) { Id = Guid.NewGuid() });
        var exported = await (await RootClient.GetAsync($"api/msels/{msel.Id}/json", Ct)).Content.ReadAsStringAsync(Ct);
        // A release that another installation has and this one does not: an id nothing here matches.
        var file = exported.Replace(lost.Id.ToString(), Guid.NewGuid().ToString());

        using var content = new MultipartFormDataContent();
        var upload = new ByteArrayContent(Encoding.UTF8.GetBytes(file));
        upload.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        content.Add(upload, "ToUpload", "msel.json");
        var response = await RootClient.PostAsync("api/msels/json/import", content, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await Read<MselImportResult>(response);
        Assert.Contains(result.Changes, c => c.Field == "attackTechniques" && c.Message.StartsWith("1 technique"));
        Assert.Equal(
            kept.Id,
            await ReadBack(rb => rb.MselAttackTechniques.Where(m => m.MselId == result.Msel.Id).Select(m => m.AttackTechniqueId).SingleAsync(Ct)));
    }

    // ---------------------------------------------------------------------------------------------

    private async Task<AttackReleaseEntity> SeedRelease(string version)
    {
        var release = await AttackStixParser.ParseAsync(
            new MemoryStream(Encoding.UTF8.GetBytes(AttackBundles.Enterprise(version))), null, Ct);
        await Seed(release);
        return release;
    }

    private static AttackTechniqueEntity Technique(AttackReleaseEntity release, string externalId) =>
        release.Techniques.Single(t => t.ExternalId == externalId);

    private static object Body(Guid mselId, Guid attackTechniqueId) => new { mselId, attackTechniqueId };

    /// <remarks>Awaited inside the <c>using</c>: <c>TestServer</c> reads the body during <c>SendAsync</c>.</remarks>
    private async Task<HttpResponseMessage> Upload(HttpClient client, string json, string fileName = "enterprise-attack.json")
    {
        using var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(json));
        file.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        content.Add(file, "ToUpload", fileName);

        return await client.PostAsync("api/attackreleases/import", content, Ct);
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
