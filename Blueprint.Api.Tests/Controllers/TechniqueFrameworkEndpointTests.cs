// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Threading.Tasks;
using Blueprint.Api.Data.Enumerations;
using Blueprint.Api.Data.Models;
using Blueprint.Api.Tests.Support;
using Blueprint.Api.ViewModels;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Blueprint.Api.Tests.Controllers;

/// <summary>
/// <c>TechniqueFrameworkController</c> - the framework definitions an administrator creates, edits, imports (as a
/// create) and exports (as a read). Blueprint ships none, so a fresh database has none.
/// </summary>
/// <remarks>401 and 403 for these routes live in <see cref="RouteAuthorizationTests"/>.</remarks>
public class TechniqueFrameworkEndpointTests(DatabaseFixture fixture, BlueprintAppFactory factory)
    : ApiTestBase(fixture, factory), IClassFixture<BlueprintAppFactory>
{
    /// <summary>A definition as an exported file holds it.</summary>
    private const string CapecDefinition = """
        {
          "name": "MITRE CAPEC",
          "description": "Attack patterns.",
          "domain": "capec",
          "isDefensive": false,
          "format": "StixBundle",
          "settings": {
            "match": { "referenceSource": "capec", "idPrefix": "CAPEC-" },
            "stix": {
              "idSource": "capec", "idPrefix": "CAPEC-", "grouping": "Hierarchy",
              "abstractionProperty": "x_capec_abstraction", "groupAbstraction": "Meta", "parentRefsProperty": "x_capec_child_of_refs",
              "statusProperty": "x_capec_status", "skipStatuses": ["Deprecated", "Obsolete"],
              "versionProperty": "x_capec_version", "platformsProperty": "x_capec_domains", "weaknessSource": "cwe"
            },
            "groupLabel": "Categories",
            "weaknessUrlTemplate": "https://cwe.mitre.org/data/definitions/{id}.html",
            "downloadHint": "stix-capec.json",
            "downloadUrl": "https://github.com/mitre/cti/tree/master/capec/2.1"
          }
        }
        """;

    [Fact]
    public async Task Get_OfAFreshDatabase_IsEmpty()
    {
        var frameworks = await Get<List<TechniqueFramework>>(RootClient, "api/techniqueframeworks");

        Assert.Empty(frameworks);
    }

    /// <summary>Importing a definition is creating it from the exported JSON; the file's release then imports.</summary>
    [Fact]
    public async Task Create_FromAnExportedDefinition_Is201_AndItsReleaseThenImports()
    {
        var created = await Post(CapecDefinition);

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var framework = await Read<TechniqueFramework>(created);
        Assert.Equal("MITRE CAPEC", framework.Name);
        Assert.Equal(TechniqueFileFormat.StixBundle, framework.Format);
        Assert.Equal(StixGrouping.Hierarchy, framework.Settings.Stix.Grouping);
        Assert.Equal(["Deprecated", "Obsolete"], framework.Settings.Stix.SkipStatuses);

        var release = await Read<TechniqueRelease>(await UploadRelease(CapecBundles.Capec()));
        Assert.Equal("capec", release.Domain);
        Assert.Equal(framework.Id, release.TechniqueFrameworkId);
        Assert.Equal("Categories", release.GroupLabel);
        Assert.Equal("https://cwe.mitre.org/data/definitions/{id}.html", release.WeaknessUrlTemplate);
        Assert.Null(release.MappingRelation);

        var listed = Assert.Single(await Get<List<TechniqueFramework>>(RootClient, "api/techniqueframeworks"));
        Assert.Equal(1, listed.ReleaseCount);
    }

    /// <summary>What a read answers is what an export writes, and it imports again as the same definition.</summary>
    [Fact]
    public async Task Get_AnswersADefinitionThatCreatesTheSameAgain()
    {
        var original = await Read<TechniqueFramework>(await Post(CapecDefinition));
        var exported = await Get<TechniqueFramework>(RootClient, $"api/techniqueframeworks/{original.Id}");
        await RootClient.DeleteAsync($"api/techniqueframeworks/{original.Id}", Ct);

        var again = await Read<TechniqueFramework>(await RootClient.PostAsJsonAsync("api/techniqueframeworks", exported, JsonOptions, Ct));

        Assert.NotEqual(original.Id, again.Id);
        Assert.Equal(original.Domain, again.Domain);
        Assert.Equal(
            System.Text.Json.JsonSerializer.Serialize(original.Settings, TechniqueFrameworkConfiguration.SettingsJson),
            System.Text.Json.JsonSerializer.Serialize(again.Settings, TechniqueFrameworkConfiguration.SettingsJson));
    }

    [Fact]
    public async Task Create_OfASecondFrameworkForADomain_Is409()
    {
        await Post(CapecDefinition);

        var response = await Post(CapecDefinition);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Theory]
    [InlineData("""{"name":"","domain":"x","format":"StixBundle","settings":{"match":{"stixDomain":"x"}}}""", "name")]
    [InlineData("""{"name":"X","domain":" ","format":"StixBundle","settings":{"match":{"stixDomain":"x"}}}""", "domain")]
    [InlineData("""{"name":"X","domain":"x","format":"StixBundle","settings":{"match":{}}}""", "match condition")]
    [InlineData("""{"name":"X","domain":"x","format":"StixBundle","settings":{"match":{"property":"p"}}}""", "STIX bundle")]
    [InlineData("""{"name":"X","domain":"x","format":"JsonLdOntology","settings":{"match":{"property":"p"}}}""", "id property")]
    [InlineData("""{"name":"X","domain":"x","format":"StixBundle","settings":{"match":{"stixDomain":"x"},"stix":{"grouping":"Hierarchy"}}}""", "hierarchy")]
    [InlineData("""{"name":"X","domain":"x","format":"StixBundle","settings":{"match":{"referenceSource":"x"},"stix":{"tacticType":"t","tacticShortNameProperty":"s"},"termsUrl":"javascript:alert(1)"}}""", "terms URL")]
    [InlineData("""{"name":"X","domain":"x","format":"StixBundle","settings":{"match":{"referenceSource":"x"},"stix":{"tacticType":"t","tacticShortNameProperty":"s"},"techniqueUrlTemplate":"data:{id}"}}""", "technique URL template")]
    [InlineData("""{"name":"X","domain":"x","format":"StixBundle","settings":{"match":{"referenceSource":"x"},"stix":{"tacticType":"t","tacticShortNameProperty":"s"},"mappings":{"sourceIdVariable":"a"}}}""", "mapping definition")]
    [InlineData("""{"name":"X","domain":"x","format":"StixBundle","settings":{"match":{"referenceSource":"x"}}}""", "kill chain")]
    [InlineData("""{"name":"X","domain":"x","format":"StixBundle","settings":{"match":{"stixDomain":"x"},"stix":{"tacticType":"t","tacticShortNameProperty":"s"}}}""", "domains property")]
    [InlineData("""{"name":"X","domain":"x","format":"StixBundle","settings":{"match":{"collectionName":"x"},"stix":{"tacticType":"t","tacticShortNameProperty":"s"}}}""", "collection type")]
    public async Task Create_OfAnIncompleteDefinition_Is400_NamingWhatIsWrong(string body, string expected)
    {
        var response = await Post(body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(expected, await response.Content.ReadAsStringAsync(Ct));
        Assert.False(await ReadBack(rb => rb.TechniqueFrameworks.AnyAsync(Ct)));
    }

    [Fact]
    public async Task Update_ChangesTheDefinition_AndWhatItsReleasesShow()
    {
        var framework = await Read<TechniqueFramework>(await Post(CapecDefinition));
        var release = await Read<TechniqueRelease>(await UploadRelease(CapecBundles.Capec()));
        framework.Name = "CAPEC, renamed";
        framework.Settings.WeaknessUrlTemplate = "https://weaknesses.example/{fullId}";

        var response = await RootClient.PutAsJsonAsync($"api/techniqueframeworks/{framework.Id}", framework, JsonOptions, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("CAPEC, renamed", (await Read<TechniqueFramework>(response)).Name);
        var served = await Get<TechniqueRelease>(RootClient, $"api/techniquereleases/{release.Id}");
        Assert.Equal("CAPEC, renamed", served.TechniqueFrameworkName);
        Assert.Equal("https://weaknesses.example/{fullId}", served.WeaknessUrlTemplate);
        // the techniques are the ones imported
        Assert.Equal(release.TechniqueCount, served.TechniqueCount);
    }

    /// <summary>MSELs pin and pool by a release's domain and family, so a framework with releases keeps them.</summary>
    [Fact]
    public async Task Update_OfTheDomainOfAFrameworkWithReleases_Is409()
    {
        var framework = await Read<TechniqueFramework>(await Post(CapecDefinition));
        await UploadRelease(CapecBundles.Capec());
        framework.Domain = "capec-renamed";

        var response = await RootClient.PutAsJsonAsync($"api/techniqueframeworks/{framework.Id}", framework, JsonOptions, Ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("capec", (await ReadBack(rb => rb.TechniqueFrameworks.SingleAsync(f => f.Id == framework.Id, Ct))).Domain);
    }

    [Fact]
    public async Task Update_OfAFrameworkThatIsNotThere_Is404()
    {
        var response = await RootClient.PutAsync($"api/techniqueframeworks/{Guid.NewGuid()}", Json(CapecDefinition), Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Delete_Is204_AndKeepsItsReleases()
    {
        var framework = await Read<TechniqueFramework>(await Post(CapecDefinition));
        var release = await Read<TechniqueRelease>(await UploadRelease(CapecBundles.Capec()));

        var response = await RootClient.DeleteAsync($"api/techniqueframeworks/{framework.Id}", Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var kept = await ReadBack(rb => rb.TechniqueReleases.SingleAsync(r => r.Id == release.Id, Ct));
        Assert.Null(kept.TechniqueFrameworkId);
        Assert.Equal(release.TechniqueCount, await ReadBack(rb => rb.Techniques.CountAsync(t => t.TechniqueReleaseId == release.Id, Ct)));
    }

    /// <summary>The definitions are reference data the Techniques pages read, so any signed-in user reads them.</summary>
    [Fact]
    public async Task Get_ForAnyUser_AnswersTheDefinitions()
    {
        await Post(CapecDefinition);
        var actor = await Actor().SeedAsync();

        var frameworks = await Get<List<TechniqueFramework>>(Client(actor), "api/techniqueframeworks");

        Assert.Equal(["MITRE CAPEC"], frameworks.Select(f => f.Name));
    }

    // ---------------------------------------------------------------------------------------------

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    private Task<HttpResponseMessage> Post(string body) => RootClient.PostAsync("api/techniqueframeworks", Json(body), Ct);

    /// <remarks>Awaited inside the <c>using</c>: <c>TestServer</c> reads the body during <c>SendAsync</c>.</remarks>
    private async Task<HttpResponseMessage> UploadRelease(string json)
    {
        using var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(json));
        file.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        content.Add(file, "ToUpload", "release.json");

        var response = await RootClient.PostAsync("api/techniquereleases/import", content, Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return response;
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
