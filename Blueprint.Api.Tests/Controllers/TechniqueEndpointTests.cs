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
/// <c>TechniqueReleaseController</c> and <c>MselTechniqueController</c> - the ATT&amp;CK, ATLAS and D3FEND
/// releases an administrator imports, and the per-MSEL technique pool that Technique and DefensiveTechnique
/// columns pick from. Also the two ways a pool leaves its MSEL: copy, and JSON export to another installation.
/// </summary>
/// <remarks>401 and 403 for these routes live in <see cref="RouteAuthorizationTests"/>.</remarks>
public class TechniqueEndpointTests(DatabaseFixture fixture, BlueprintAppFactory factory)
    : ApiTestBase(fixture, factory), IClassFixture<BlueprintAppFactory>
{
    // ---------------------------------------------------------------------------------------------
    // Releases
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Import_WithManageCompetencyFrameworks_Is201_CountingWhatItRead()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageCompetencyFrameworks).SeedAsync();

        var response = await Upload(Client(actor), TechniqueBundles.Enterprise("1.0"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var release = await Read<TechniqueRelease>(response);
        Assert.Equal(StixTechniqueParser.ReleaseId("enterprise-attack", "1.0"), release.Id);
        Assert.False(release.IsDefensive);
        Assert.Equal(2, release.TacticCount);
        Assert.Equal(4, release.TechniqueCount);
        Assert.Equal(4, await ReadBack(rb => rb.Techniques.CountAsync(t => t.TechniqueReleaseId == release.Id, Ct)));
    }

    /// <summary>ATLAS shares ATT&amp;CK's data model, so the same endpoint imports it as a release of its own domain.</summary>
    [Fact]
    public async Task Import_OfAnAtlasBundle_Is201_AsAnAtlasRelease()
    {
        var response = await Upload(RootClient, TechniqueBundles.Atlas(), "stix-atlas.json");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var release = await Read<TechniqueRelease>(response);
        Assert.Equal(StixTechniqueParser.ReleaseId("ATLAS", "2026.10"), release.Id);
        Assert.Equal("ATLAS", release.Domain);
        Assert.Equal(2, release.TacticCount);
        Assert.Equal(3, release.TechniqueCount);
        var ids = await ReadBack(rb => rb.Techniques.Where(t => t.TechniqueReleaseId == release.Id).Select(t => t.ExternalId).ToListAsync(Ct));
        Assert.Equal(["AML.T0000", "AML.T0000.000", "AML.T0040"], ids.Order());
    }

    /// <summary>The D3FEND ontology is JSON-LD, not STIX; the same endpoint reads it as a release of the d3fend domain.</summary>
    [Fact]
    public async Task Import_OfTheD3fendOntology_Is201_AsAD3fendRelease()
    {
        var response = await Upload(RootClient, D3fendOntologies.Ontology(), "d3fend.json");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var release = await Read<TechniqueRelease>(response);
        Assert.Equal(StixTechniqueParser.ReleaseId("d3fend", "1.6.0"), release.Id);
        Assert.Equal("d3fend", release.Domain);
        Assert.True(release.IsDefensive);
        Assert.Equal("MITRE D3FEND", release.Name);
        Assert.Equal(2, release.TacticCount);
        Assert.Equal(3, release.TechniqueCount);
        var techniques = await Get<List<Technique>>(RootClient, $"api/techniquereleases/{release.Id}/techniques");
        var cban = techniques.Single(t => t.ExternalId == "D3-CBAN");
        Assert.True(cban.IsSubTechnique);
        Assert.Equal(techniques.Single(t => t.ExternalId == "D3-CH").Id, cban.ParentId);
        Assert.Equal(["harden"], cban.Tactics);
        Assert.Empty(cban.Platforms);
        Assert.Equal("https://d3fend.mitre.org/technique/d3f:CertificateBasedAuthentication/", cban.Url);
    }

    [Fact]
    public async Task Import_OfAReleaseAlreadyThere_Is409()
    {
        await SeedRelease("1.0");

        var response = await Upload(RootClient, TechniqueBundles.Enterprise("1.0"));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    /// <summary>Uploading the same file twice is a clean 409 naming the release, not a database error, and leaves one row.</summary>
    [Fact]
    public async Task Import_OfTheSameFileTwice_Is409_NamingTheRelease_AndKeepsOneRow()
    {
        var first = await Upload(RootClient, TechniqueBundles.Enterprise("1.0"));
        var second = await Upload(RootClient, TechniqueBundles.Enterprise("1.0"));

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Contains("enterprise-attack 1.0 has already been imported", await second.Content.ReadAsStringAsync(Ct));
        Assert.Equal(1, await ReadBack(rb => rb.TechniqueReleases.CountAsync(r => r.Domain == "enterprise-attack" && r.Version == "1.0", Ct)));
    }

    /// <summary>A version typed with stray spaces is the same release, so it is refused the same way.</summary>
    [Fact]
    public async Task Import_OfTheSameVersionTypedWithSpaces_Is409()
    {
        await SeedRelease("1.0");
        using var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(TechniqueBundles.Enterprise("1.0", includeVersion: false)));
        file.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        content.Add(file, "ToUpload", "enterprise-attack.json");

        var response = await RootClient.PostAsync("api/techniquereleases/import?version=%201.0%20", content, Ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    // ---------------------------------------------------------------------------------------------
    // A release's notice fields
    // ---------------------------------------------------------------------------------------------

    private static object Notice(string source = null, string sourceUrl = null, string notice = null, string termsUrl = null) =>
        new { source, sourceUrl, notice, termsUrl };

    /// <summary>The notice is the statement the file carries, whatever year that names, not one the server writes.</summary>
    [Fact]
    public async Task Import_OfAMitreBundle_ShowsTheStatementTheFileCarries()
    {
        var response = await Upload(RootClient, TechniqueBundles.Enterprise("1.0"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var release = await Read<TechniqueRelease>(response);
        Assert.Equal(2025, release.ReleaseDate.Value.Year);
        Assert.StartsWith("Copyright 2015-2025, The MITRE Corporation.", release.Notice);
    }

    [Fact]
    public async Task Update_ChangesTheFourNoticeFieldsOnly_AndLeavesTheMselPoolAlone()
    {
        var release = await SeedRelease("1.0");
        var msel = TestData.Msel();
        await Seed(msel);
        var pooled = Technique(release, "T1059");
        await Seed(new MselTechniqueEntity(msel.Id, pooled.Id) { Id = Guid.NewGuid() });
        var admin = await Actor().WithSystemPermissions(SystemPermission.ManageCompetencyFrameworks).SeedAsync();

        var response = await Client(admin).PutAsJsonAsync(
            $"api/techniquereleases/{release.Id}",
            Notice("  Acme Corp  ", "https://acme.example/ttp", "© Acme\nAcme is a mark of Acme Corp.", "https://acme.example/terms"), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var updated = await Read<TechniqueRelease>(response);
        Assert.Equal("Acme Corp", updated.Source);
        Assert.Equal("https://acme.example/ttp", updated.SourceUrl);
        Assert.Equal("© Acme\nAcme is a mark of Acme Corp.", updated.Notice);
        Assert.Equal("https://acme.example/terms", updated.TermsUrl);
        // nothing else about the release moved
        Assert.Equal(release.Id, updated.Id);
        Assert.Equal("enterprise-attack", updated.Domain);
        Assert.Equal("1.0", updated.Version);
        Assert.Equal(release.Name, updated.Name);
        Assert.Equal(release.Tactics.Count, updated.TacticCount);
        Assert.Equal(release.Techniques.Count, updated.TechniqueCount);
        var stored = await ReadBack(rb => rb.Techniques.Where(t => t.TechniqueReleaseId == release.Id).Select(t => t.ExternalId).ToListAsync(Ct));
        Assert.Equal(release.Techniques.Select(t => t.ExternalId).Order(), stored.Order());
        // and the MSEL's pool is the same row on the same technique
        var pool = await Get<List<MselTechnique>>(RootClient, $"api/msels/{msel.Id}/mseltechniques");
        Assert.Equal(pooled.Id, Assert.Single(pool).TechniqueId);
    }

    [Fact]
    public async Task Update_WithBlankFields_ClearsThem()
    {
        var release = await SeedRelease("1.0");

        var response = await RootClient.PutAsJsonAsync($"api/techniquereleases/{release.Id}", Notice("  ", "", null, " "), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var updated = await Read<TechniqueRelease>(response);
        Assert.Null(updated.Source);
        Assert.Null(updated.SourceUrl);
        Assert.Null(updated.Notice);
        Assert.Null(updated.TermsUrl);
    }

    [Fact]
    public async Task Update_OfACustomRelease_GivesItANoticeItThenCarries()
    {
        var created = await Read<TechniqueRelease>(await Upload(RootClient, TechniqueBundles.Custom(), "acme.json"));
        Assert.Null(created.Notice);

        await RootClient.PutAsJsonAsync($"api/techniquereleases/{created.Id}", Notice("Acme", null, "© Acme Corp", null), Ct);

        var releases = await Get<List<TechniqueRelease>>(RootClient, "api/techniquereleases");
        Assert.Equal("© Acme Corp", releases.Single(r => r.Id == created.Id).Notice);
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html,x")]
    [InlineData("not a url")]
    public async Task Update_WithATermsUrlThatIsNotAWebAddress_Is400_AndChangesNothing(string url)
    {
        var release = await SeedRelease("1.0");

        var response = await RootClient.PutAsJsonAsync($"api/techniquereleases/{release.Id}", Notice("Acme", null, "© Acme", url), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("The MITRE Corporation", (await ReadBack(rb => rb.TechniqueReleases.SingleAsync(r => r.Id == release.Id, Ct))).Source);
    }

    [Fact]
    public async Task Update_OfAMissingRelease_Is404()
    {
        var response = await RootClient.PutAsJsonAsync($"api/techniquereleases/{Guid.NewGuid()}", Notice("Acme"), Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Update_WithoutManageCompetencyFrameworks_Is403_AndChangesNothing()
    {
        var release = await SeedRelease("1.0");
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageMsels, SystemPermission.ViewMsels).SeedAsync();

        var response = await Client(actor).PutAsJsonAsync($"api/techniquereleases/{release.Id}", Notice("Acme", null, "© Acme", null), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("The MITRE Corporation", (await ReadBack(rb => rb.TechniqueReleases.SingleAsync(r => r.Id == release.Id, Ct))).Source);
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

        var techniques = await Get<List<Technique>>(Client(actor), $"api/techniquereleases/{release.Id}/techniques");
        var tactics = await Get<List<TechniqueTactic>>(Client(actor), $"api/techniquereleases/{release.Id}/tactics");

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

        var response = await RootClient.DeleteAsync($"api/techniquereleases/{release.Id}", Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.False(await ReadBack(rb => rb.Techniques.AnyAsync(t => t.TechniqueReleaseId == release.Id, Ct)));
    }

    [Fact]
    public async Task Delete_OfAReleaseAMselUses_Is409_AndKeepsIt()
    {
        var release = await SeedRelease("1.0");
        var msel = TestData.Msel();
        await Seed(msel);
        await Seed(new MselTechniqueEntity(msel.Id, Technique(release, "T1059").Id) { Id = Guid.NewGuid() });

        var response = await RootClient.DeleteAsync($"api/techniquereleases/{release.Id}", Ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.True(await ReadBack(rb => rb.TechniqueReleases.AnyAsync(r => r.Id == release.Id, Ct)));
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
            "api/mseltechniques", Body(msel.Id, Technique(release, "T1059.001").Id), Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var pool = await Get<List<MselTechnique>>(Client(viewer), $"api/msels/{msel.Id}/mseltechniques");
        Assert.Equal("T1059.001", Assert.Single(pool).Technique.ExternalId);
    }

    [Fact]
    public async Task Create_IsForbiddenForAnEditorOfTheMsel()
    {
        var release = await SeedRelease("1.0");
        var msel = TestData.Msel();
        await Seed(msel);
        var editor = await Actor().OnMsel(msel, MselRole.Editor).SeedAsync();

        var response = await Client(editor).PostAsJsonAsync(
            "api/mseltechniques", Body(msel.Id, Technique(release, "T1059").Id), Ct);

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
        await Seed(new MselTechniqueEntity(msel.Id, Technique(first, "T1059").Id) { Id = Guid.NewGuid() });

        var response = await RootClient.PostAsJsonAsync(
            "api/mseltechniques", Body(msel.Id, Technique(second, "T1053").Id), Ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(1, await ReadBack(rb => rb.MselTechniques.CountAsync(m => m.MselId == msel.Id, Ct)));
    }

    [Fact]
    public async Task Create_OfATechniqueAlreadyInThePool_Is409()
    {
        var release = await SeedRelease("1.0");
        var msel = TestData.Msel();
        await Seed(msel);
        await Seed(new MselTechniqueEntity(msel.Id, Technique(release, "T1059").Id) { Id = Guid.NewGuid() });

        var response = await RootClient.PostAsJsonAsync(
            "api/mseltechniques", Body(msel.Id, Technique(release, "T1059").Id), Ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task DeleteByIds_ForAnOwnerOfTheMsel_Is204()
    {
        var release = await SeedRelease("1.0");
        var msel = TestData.Msel();
        await Seed(msel);
        var technique = Technique(release, "T1059");
        await Seed(new MselTechniqueEntity(msel.Id, technique.Id) { Id = Guid.NewGuid() });
        var owner = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        var response = await Client(owner).DeleteAsync($"api/msels/{msel.Id}/techniques/{technique.Id}", Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.False(await ReadBack(rb => rb.MselTechniques.AnyAsync(m => m.MselId == msel.Id, Ct)));
    }

    [Fact]
    public async Task Upgrade_KeepsWhatTheNewReleaseHas_AndReportsWhatItDropped()
    {
        var first = await SeedRelease("1.0");
        var second = await SeedRelease("2.0");
        var msel = TestData.Msel();
        await Seed(msel);
        await Seed(
            new MselTechniqueEntity(msel.Id, Technique(first, "T1059").Id) { Id = Guid.NewGuid() },
            new MselTechniqueEntity(msel.Id, Technique(first, "T1003").Id) { Id = Guid.NewGuid() });
        var owner = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        var response = await Client(owner).PostAsync($"api/msels/{msel.Id}/techniques/upgrade/{second.Id}", null, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await Read<MselTechniqueUpgrade>(response);
        Assert.Equal(1, result.Kept);
        Assert.Equal(["T1003"], result.Dropped);
        var pool = await ReadBack(rb => rb.MselTechniques.Where(m => m.MselId == msel.Id).Select(m => m.TechniqueId).ToListAsync(Ct));
        Assert.Equal([Technique(second, "T1059").Id], pool);
    }

    // ---------------------------------------------------------------------------------------------
    // The pool pins one release per domain (enterprise-attack, capec, d3fend, ...), not one per family
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// A defensive release of any domain joins the pool beside the attack techniques, and pins only its own domain.
    /// </summary>
    [Fact]
    public async Task Create_OfATechniqueFromADefensiveReleaseOfAnotherDomain_Is201_BesideTheAttackTechniques()
    {
        var attack = await SeedRelease("1.0");
        var defensive = await SeedDefensiveRelease("other-defend", "2.0");
        var msel = TestData.Msel();
        await Seed(msel);
        await Seed(new MselTechniqueEntity(msel.Id, Technique(attack, "T1059").Id) { Id = Guid.NewGuid() });

        var response = await RootClient.PostAsJsonAsync(
            "api/mseltechniques", Body(msel.Id, Technique(defensive, "D3-CH").Id), Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var added = (await Read<MselTechnique>(response)).Technique;
        Assert.Equal("other-defend", added.Domain);
        Assert.True(added.IsDefensive);
        // the attack domain is still pinned to its own release and takes another of its techniques
        var second = await RootClient.PostAsJsonAsync("api/mseltechniques", Body(msel.Id, Technique(attack, "T1053").Id), Ct);
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
    }

    /// <summary>A D3FEND release is another domain, so it does not pin the attack techniques, nor they it.</summary>
    [Fact]
    public async Task Create_OfADefensiveTechnique_Is201_BesideTheTechniques()
    {
        var attack = await SeedRelease("1.0");
        var d3fend = await SeedD3fendRelease("1.6.0");
        var msel = TestData.Msel();
        await Seed(msel);
        await Seed(new MselTechniqueEntity(msel.Id, Technique(attack, "T1059").Id) { Id = Guid.NewGuid() });

        var response = await RootClient.PostAsJsonAsync(
            "api/mseltechniques", Body(msel.Id, Technique(d3fend, "D3-CH").Id), Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var added = (await Read<MselTechnique>(response)).Technique;
        Assert.Equal("d3fend", added.Domain);
        Assert.True(added.IsDefensive);
        var pool = await Get<List<MselTechnique>>(RootClient, $"api/msels/{msel.Id}/mseltechniques");
        Assert.Equal(["D3-CH", "T1059"], pool.Select(m => m.Technique.ExternalId).Order());
        Assert.Equal(["d3fend", "enterprise-attack"], pool.Select(m => m.Technique.Domain).Order());
    }

    /// <summary>A domain is pinned to its own release whichever family it is in.</summary>
    [Fact]
    public async Task Create_OfADefensiveTechniqueFromAnotherD3fendRelease_Is409()
    {
        var first = await SeedD3fendRelease("1.6.0");
        var second = await SeedD3fendRelease("1.7.0");
        var msel = TestData.Msel();
        await Seed(msel);
        await Seed(new MselTechniqueEntity(msel.Id, Technique(first, "D3-CH").Id) { Id = Guid.NewGuid() });

        var response = await RootClient.PostAsJsonAsync(
            "api/mseltechniques", Body(msel.Id, Technique(second, "D3-FA").Id), Ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("d3fend techniques are from 1.6.0", await response.Content.ReadAsStringAsync(Ct));
    }

    /// <summary>
    /// ATT&amp;CK, CAPEC and D3FEND sit side by side in one MSEL's pool: that is the point of pinning per domain, so
    /// "exploit a public-facing application" (T1190) and "SQL Injection" (CAPEC-66) can be planned together.
    /// </summary>
    [Fact]
    public async Task Create_OfTechniquesFromEnterpriseCapecAndD3fend_AllLandInOnePool()
    {
        var enterprise = await SeedRelease("1.0");
        var capec = await SeedCapecRelease("3.9");
        var d3fend = await SeedD3fendRelease("1.6.0");
        var msel = TestData.Msel();
        await Seed(msel);

        foreach (var technique in new[] { Technique(enterprise, "T1059"), Technique(capec, "CAPEC-66"), Technique(d3fend, "D3-CH") })
        {
            var response = await RootClient.PostAsJsonAsync("api/mseltechniques", Body(msel.Id, technique.Id), Ct);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        }

        var pool = await Get<List<MselTechnique>>(RootClient, $"api/msels/{msel.Id}/mseltechniques");
        Assert.Equal(["capec", "d3fend", "enterprise-attack"], pool.Select(m => m.Technique.Domain).Order());
    }

    /// <summary>Two releases of one domain never mix, whichever other domains the MSEL holds.</summary>
    [Fact]
    public async Task Create_OfATechniqueFromAnotherReleaseOfADomain_Is409_NamingTheDomain_EvenWithOtherDomainsInThePool()
    {
        var first = await SeedRelease("1.0");
        var second = await SeedRelease("2.0");
        var capec = await SeedCapecRelease("3.9");
        var msel = TestData.Msel();
        await Seed(msel);
        await Seed(
            new MselTechniqueEntity(msel.Id, Technique(first, "T1059").Id) { Id = Guid.NewGuid() },
            new MselTechniqueEntity(msel.Id, Technique(capec, "CAPEC-66").Id) { Id = Guid.NewGuid() });

        var refused = await RootClient.PostAsJsonAsync("api/mseltechniques", Body(msel.Id, Technique(second, "T1053").Id), Ct);
        var other = await RootClient.PostAsJsonAsync("api/mseltechniques", Body(msel.Id, Technique(capec, "CAPEC-8").Id), Ct);

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Contains("enterprise-attack techniques are from 1.0", await refused.Content.ReadAsStringAsync(Ct));
        Assert.Equal(HttpStatusCode.Created, other.StatusCode);
    }

    /// <summary>Upgrading one domain moves it alone: the CAPEC and D3FEND techniques keep their releases.</summary>
    [Fact]
    public async Task Upgrade_OfOneDomain_LeavesTheOtherDomainsAtTheirReleases()
    {
        var first = await SeedRelease("1.0");
        var second = await SeedRelease("2.0");
        var capec = await SeedCapecRelease("3.9");
        var d3fend = await SeedD3fendRelease("1.6.0");
        var msel = TestData.Msel();
        await Seed(msel);
        await Seed(
            new MselTechniqueEntity(msel.Id, Technique(first, "T1059").Id) { Id = Guid.NewGuid() },
            new MselTechniqueEntity(msel.Id, Technique(capec, "CAPEC-66").Id) { Id = Guid.NewGuid() },
            new MselTechniqueEntity(msel.Id, Technique(d3fend, "D3-CH").Id) { Id = Guid.NewGuid() });

        var response = await RootClient.PostAsync($"api/msels/{msel.Id}/techniques/upgrade/{second.Id}", null, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await Read<MselTechniqueUpgrade>(response);
        Assert.Equal(1, result.Kept);
        Assert.Empty(result.Dropped);
        var pool = await ReadBack(rb => rb.MselTechniques.Where(m => m.MselId == msel.Id).Select(m => m.TechniqueId).ToListAsync(Ct));
        Assert.Equal(
            new[] { Technique(second, "T1059").Id, Technique(capec, "CAPEC-66").Id, Technique(d3fend, "D3-CH").Id }.Order(),
            pool.Order());
    }

    /// <summary>A custom release is served with no publisher or notice, so a client shows none for it.</summary>
    [Fact]
    public async Task Import_OfACustomBundle_IsServedWithNoNotice_WhileAMitreOneCarriesIts()
    {
        var custom = await Upload(RootClient, TechniqueBundles.Custom(), "acme.json");
        var mitre = await Upload(RootClient, TechniqueBundles.Enterprise("1.0"));

        Assert.Equal(HttpStatusCode.Created, custom.StatusCode);
        Assert.Equal(HttpStatusCode.Created, mitre.StatusCode);
        var releases = await Get<List<TechniqueRelease>>(RootClient, "api/techniquereleases");
        var acme = releases.Single(r => r.Domain == "acme-ttp");
        Assert.Null(acme.Notice);
        Assert.Null(acme.Source);
        Assert.Equal("Tactics", acme.GroupLabel);
        Assert.Equal("The MITRE Corporation", releases.Single(r => r.Domain == "enterprise-attack").Source);
    }

    /// <summary>Upgrading to an ATT&amp;CK release moves that domain only; the D3FEND ones stay where they are.</summary>
    [Fact]
    public async Task Upgrade_ToAnTechniqueRelease_LeavesTheDefensiveTechniquesAlone()
    {
        var first = await SeedRelease("1.0");
        var second = await SeedRelease("2.0");
        var d3fend = await SeedD3fendRelease("1.6.0");
        var msel = TestData.Msel();
        await Seed(msel);
        await Seed(
            new MselTechniqueEntity(msel.Id, Technique(first, "T1059").Id) { Id = Guid.NewGuid() },
            new MselTechniqueEntity(msel.Id, Technique(first, "T1003").Id) { Id = Guid.NewGuid() },
            new MselTechniqueEntity(msel.Id, Technique(d3fend, "D3-CH").Id) { Id = Guid.NewGuid() });

        var response = await RootClient.PostAsync($"api/msels/{msel.Id}/techniques/upgrade/{second.Id}", null, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await Read<MselTechniqueUpgrade>(response);
        Assert.Equal(1, result.Kept);
        Assert.Equal(["T1003"], result.Dropped);
        var pool = await ReadBack(rb => rb.MselTechniques.Where(m => m.MselId == msel.Id).Select(m => m.TechniqueId).ToListAsync(Ct));
        Assert.Equal(new[] { Technique(second, "T1059").Id, Technique(d3fend, "D3-CH").Id }.Order(), pool.Order());
    }

    [Fact]
    public async Task Upgrade_ToAD3fendRelease_MovesTheDefensiveTechniquesOnly()
    {
        var attack = await SeedRelease("1.0");
        var first = await SeedD3fendRelease("1.6.0");
        var second = await SeedD3fendRelease("1.7.0");
        var msel = TestData.Msel();
        await Seed(msel);
        await Seed(
            new MselTechniqueEntity(msel.Id, Technique(attack, "T1059").Id) { Id = Guid.NewGuid() },
            new MselTechniqueEntity(msel.Id, Technique(first, "D3-CH").Id) { Id = Guid.NewGuid() },
            new MselTechniqueEntity(msel.Id, Technique(first, "D3-CBAN").Id) { Id = Guid.NewGuid() });

        var response = await RootClient.PostAsync($"api/msels/{msel.Id}/techniques/upgrade/{second.Id}", null, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await Read<MselTechniqueUpgrade>(response);
        Assert.Equal(2, result.Kept);
        Assert.Empty(result.Dropped);
        var pool = await ReadBack(rb => rb.MselTechniques.Where(m => m.MselId == msel.Id).Select(m => m.TechniqueId).ToListAsync(Ct));
        Assert.Equal(new[] { Technique(attack, "T1059").Id, Technique(second, "D3-CBAN").Id, Technique(second, "D3-CH").Id }.Order(), pool.Order());
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
        await Seed(new MselTechniqueEntity(msel.Id, Technique(release, "T1059").Id) { Id = Guid.NewGuid() });

        var response = await RootClient.PostAsync($"api/msels/{msel.Id}/copy", null, Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var copy = await Read<Msel>(response);
        Assert.Equal(
            Technique(release, "T1059").Id,
            await ReadBack(rb => rb.MselTechniques.Where(m => m.MselId == copy.Id).Select(m => m.TechniqueId).SingleAsync(Ct)));
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
            new MselTechniqueEntity(msel.Id, kept.Id) { Id = Guid.NewGuid() },
            new MselTechniqueEntity(msel.Id, lost.Id) { Id = Guid.NewGuid() });
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
        Assert.Contains(result.Changes, c => c.Field == "techniques" && c.Message.StartsWith("1 technique"));
        Assert.Equal(
            kept.Id,
            await ReadBack(rb => rb.MselTechniques.Where(m => m.MselId == result.Msel.Id).Select(m => m.TechniqueId).SingleAsync(Ct)));
    }

    // ---------------------------------------------------------------------------------------------

    private async Task<TechniqueReleaseEntity> SeedRelease(string version)
    {
        var release = await StixTechniqueParser.ParseAsync(
            new MemoryStream(Encoding.UTF8.GetBytes(TechniqueBundles.Enterprise(version))), null, Ct);
        await Seed(release);
        return release;
    }

    private async Task<TechniqueReleaseEntity> SeedCapecRelease(string version)
    {
        var release = await StixTechniqueParser.ParseAsync(
            new MemoryStream(Encoding.UTF8.GetBytes(CapecBundles.Capec(version))), null, Ct);
        await Seed(release);
        return release;
    }

    private async Task<TechniqueReleaseEntity> SeedD3fendRelease(string version)
    {
        var release = await D3fendOntologyParser.ParseAsync(
            new MemoryStream(Encoding.UTF8.GetBytes(D3fendOntologies.Ontology(version))), null, Ct);
        await Seed(release);
        return release;
    }

    /// <summary>A defensive release of a domain that is not d3fend, as a future framework's parser would make.</summary>
    private async Task<TechniqueReleaseEntity> SeedDefensiveRelease(string domain, string version)
    {
        var release = await D3fendOntologyParser.ParseAsync(
            new MemoryStream(Encoding.UTF8.GetBytes(D3fendOntologies.Ontology(version))), null, Ct);
        release.Domain = domain;
        Assert.True(release.IsDefensive);
        await Seed(release);
        return release;
    }

    private static TechniqueEntity Technique(TechniqueReleaseEntity release, string externalId) =>
        release.Techniques.Single(t => t.ExternalId == externalId);

    private static object Body(Guid mselId, Guid techniqueId) => new { mselId, techniqueId };

    /// <remarks>Awaited inside the <c>using</c>: <c>TestServer</c> reads the body during <c>SendAsync</c>.</remarks>
    private async Task<HttpResponseMessage> Upload(HttpClient client, string json, string fileName = "enterprise-attack.json")
    {
        using var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(json));
        file.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        content.Add(file, "ToUpload", fileName);

        return await client.PostAsync("api/techniquereleases/import", content, Ct);
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
