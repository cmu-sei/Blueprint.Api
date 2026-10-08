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
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Blueprint.Api.Tests.Controllers;

/// <summary>
/// <c>CatalogService</c> / <c>CatalogController</c> - the ten routes over catalogs, the reusable libraries
/// of injects a MSEL's timeline is filled from. Reads want <c>ViewCatalogs</c> or a unit the catalog is
/// assigned to (<c>CatalogViewRequirement</c>); writes want <c>ManageCatalogs</c> and consult no unit.
/// </summary>
/// <remarks>
/// 401 and 403 for these routes live in <see cref="RouteAuthorizationTests"/>; this file covers the happy
/// path and the not-found path per route, per the thin protocol.
/// <para />
/// The two file routes speak a different dialect from the rest of the API: both build their own
/// <c>JsonSerializerOptions</c> with <c>ReferenceHandler.Preserve</c>, so the file is PascalCase behind an
/// <c>$id</c>/<c>$values</c> wrapper with raw integers where every response is camelCase with <c>int</c> as
/// a JSON string. The pair agrees with itself, so a downloaded file uploads.
/// </remarks>
public class CatalogEndpointTests(DatabaseFixture fixture, BlueprintAppFactory factory)
    : ApiTestBase(fixture, factory), IClassFixture<BlueprintAppFactory>
{
    // ---------------------------------------------------------------------------------------------
    // GET catalogs, GET my-catalogs, GET users/{userId}/catalogs
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task GetAll_WithViewCatalogs_ReturnsEveryCatalog()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewCatalogs).SeedAsync();
        var injectType = TestData.InjectType();
        await Seed(injectType);
        var mine = TestData.Catalog(injectType.Id, actor.Id);
        var theirs = TestData.Catalog(injectType.Id);
        await Seed(mine, theirs);

        var catalogs = await Get<List<ViewModels.Catalog>>(Client(actor), "api/catalogs");

        Assert.Equal(2, catalogs.Count);
        Assert.Contains(mine.Id, catalogs.Select(x => x.Id));
        Assert.Contains(theirs.Id, catalogs.Select(x => x.Id));
    }

    /// <summary>Get mine with no permissions returns the callers own public and unit catalogs.</summary>
    [Fact]
    public async Task GetMine_WithNoPermissions_ReturnsTheCallersOwnPublicAndUnitCatalogs()
    {
        var actor = await Actor().SeedAsync();
        var injectType = TestData.InjectType();
        var unit = TestData.Unit();
        await Seed(injectType, unit);
        var mine = TestData.Catalog(injectType.Id, actor.Id);
        var open = TestData.Catalog(injectType.Id, isPublic: true);
        var viaUnit = TestData.Catalog(injectType.Id);
        var hidden = TestData.Catalog(injectType.Id);
        await Seed(mine, open, viaUnit, hidden);
        await Seed(
            TestData.UnitUser(actor.Id, unit.Id),
            TestData.CatalogUnit(unit.Id, viaUnit.Id));

        var catalogs = await Get<List<ViewModels.Catalog>>(Client(actor), "api/my-catalogs");

        var ids = catalogs.Select(x => x.Id).ToList();
        Assert.Equal(3, ids.Count);
        Assert.Contains(mine.Id, ids);
        Assert.Contains(open.Id, ids);
        Assert.Contains(viaUnit.Id, ids);
        Assert.DoesNotContain(hidden.Id, ids);
    }

    [Fact]
    public async Task GetUserCatalogs_WithManageUsers_ReturnsThatUsersCatalogs()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageUsers).SeedAsync();
        var injectType = TestData.InjectType();
        var other = TestData.User();
        await Seed(injectType, other);
        var theirs = TestData.Catalog(injectType.Id, other.Id);
        var mine = TestData.Catalog(injectType.Id, actor.Id);
        await Seed(theirs, mine);

        var catalogs = await Get<List<ViewModels.Catalog>>(
            Client(actor), $"api/users/{other.Id}/catalogs");

        Assert.Single(catalogs);
        Assert.Equal(theirs.Id, catalogs[0].Id);
    }

    [Fact]
    public async Task GetUserCatalogs_for_the_callers_own_id_without_ManageUsers_returns_its_catalogs()
    {
        var actor = await Actor().SeedAsync();
        var injectType = TestData.InjectType();
        var other = TestData.User();
        await Seed(injectType, other);
        var mine = TestData.Catalog(injectType.Id, actor.Id);
        var theirs = TestData.Catalog(injectType.Id, other.Id);
        await Seed(mine, theirs);

        var catalogs = await Get<List<ViewModels.Catalog>>(
            Client(actor), $"api/users/{actor.Id}/catalogs");

        Assert.Equal(mine.Id, Assert.Single(catalogs).Id);
    }

    // ---------------------------------------------------------------------------------------------
    // GET catalogs/{id}
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Get_WithViewCatalogs_Is200()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewCatalogs).SeedAsync();
        var (_, catalog) = await SeedCatalog();

        var answered = await Get<ViewModels.Catalog>(Client(actor), $"api/catalogs/{catalog.Id}");

        Assert.Equal(catalog.Id, answered.Id);
        Assert.Equal(catalog.Name, answered.Name);
        Assert.False(answered.IsPublic);
    }

    /// <remarks>
    /// The minimum a caller can hold and still read a private catalog: membership of a unit it is assigned
    /// to, which is all <c>CatalogViewRequirement</c> asks for.
    /// </remarks>
    [Fact]
    public async Task Get_ForAMemberOfAUnitTheCatalogIsAssignedTo_Is200()
    {
        var actor = await Actor().SeedAsync();
        var (_, catalog) = await SeedCatalog();
        var unit = TestData.Unit();
        await Seed(unit);
        await Seed(
            TestData.UnitUser(actor.Id, unit.Id),
            TestData.CatalogUnit(unit.Id, catalog.Id));

        var response = await Client(actor).GetAsync($"api/catalogs/{catalog.Id}", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>Get for an id that is not there is answered with a 204.</summary>
    [Fact]
    public async Task Get_ForAnIdThatIsNotThere_Is204()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewCatalogs).SeedAsync();

        var response = await Client(actor).GetAsync($"api/catalogs/{Guid.NewGuid()}", Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsStringAsync(Ct));
    }

    // ---------------------------------------------------------------------------------------------
    // POST catalogs
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Create_WithManageCatalogs_Is201()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageCatalogs).SeedAsync();
        var injectType = TestData.InjectType();
        await Seed(injectType);

        var response = await Client(actor).PostAsJsonAsync(
            "api/catalogs", Body(injectType.Id) with { name = "new-catalog" }, Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<ViewModels.Catalog>(JsonOptions, Ct);
        Assert.Equal("new-catalog", created.Name);
        Assert.Equal(actor.Id, created.CreatedBy);
        Assert.EndsWith($"/api/catalogs/{created.Id}", response.Headers.Location.ToString());

        var stored = await ReadBack(rb => rb.Catalogs.SingleOrDefaultAsync(x => x.Id == created.Id, Ct));
        Assert.Equal(injectType.Id, stored.InjectTypeId);
    }

    // ---------------------------------------------------------------------------------------------
    // POST catalogs/{id}/copy
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Copy_WithManageCatalogs_Is201()
    {
        var actor = await Actor().WithName("Copier").WithSystemPermissions(SystemPermission.ManageCatalogs)
            .SeedAsync();
        var (injectType, catalog) = await SeedCatalog(isPublic: true);
        var inject = TestData.Inject(injectType.Id);
        await Seed(inject);
        await Seed(TestData.CatalogInject(catalog.Id, inject.Id));

        var response = await Client(actor).PostAsync($"api/catalogs/{catalog.Id}/copy", null, Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var copy = await response.Content.ReadFromJsonAsync<ViewModels.Catalog>(JsonOptions, Ct);
        Assert.NotEqual(catalog.Id, copy.Id);
        Assert.Equal($"{catalog.Name} - Copier", copy.Name);
        Assert.False(copy.IsPublic);

        await using var context = NewContext();
        Assert.Equal(actor.Id, (await context.Catalogs.SingleAsync(x => x.Id == copy.Id, Ct)).CreatedBy);
        Assert.Single(await context.CatalogInjects.Where(x => x.CatalogId == copy.Id).ToListAsync(Ct));
    }

    /// <summary>Copy for an id that is not there is answered with a 500.</summary>
    [Fact]
    public async Task Copy_ForAnIdThatIsNotThere_Is500()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageCatalogs).SeedAsync();

        var response = await Client(actor).PostAsync($"api/catalogs/{Guid.NewGuid()}/copy", null, Ct);

        var failure = await AssertJsonError<Blueprint.Api.ViewModels.ApiError>(HttpStatusCode.InternalServerError, response);
        Assert.Equal("Object reference not set to an instance of an object.", failure.Title);
        Assert.Contains("CatalogService.privateCatalogCopyAsync", failure.Detail);
    }

    // ---------------------------------------------------------------------------------------------
    // PUT catalogs/{id}
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Update_WithManageCatalogs_Is200()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageCatalogs).SeedAsync();
        var (injectType, catalog) = await SeedCatalog();

        var response = await Client(actor).PutAsJsonAsync(
            $"api/catalogs/{catalog.Id}",
            Body(injectType.Id) with { id = catalog.Id, name = "renamed", isPublic = true },
            Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var stored = await ReadBack(rb => rb.Catalogs.SingleAsync(x => x.Id == catalog.Id, Ct));
        Assert.Equal("renamed", stored.Name);
        Assert.True(stored.IsPublic);
    }

    [Fact]
    public async Task Update_ForAnIdThatIsNotThere_Is404()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageCatalogs).SeedAsync();
        var injectType = TestData.InjectType();
        await Seed(injectType);
        var id = Guid.NewGuid();

        var response = await Client(actor).PutAsJsonAsync(
            $"api/catalogs/{id}", Body(injectType.Id) with { id = id }, Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ---------------------------------------------------------------------------------------------
    // DELETE catalogs/{id}
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Delete_WithManageCatalogs_Is204()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageCatalogs).SeedAsync();
        var (_, catalog) = await SeedCatalog();

        var response = await Client(actor).DeleteAsync($"api/catalogs/{catalog.Id}", Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Empty(await ReadBack(rb => rb.Catalogs.Where(x => x.Id == catalog.Id).ToListAsync(Ct)));
    }

    [Fact]
    public async Task Delete_ForAnIdThatIsNotThere_Is404()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageCatalogs).SeedAsync();

        var response = await Client(actor).DeleteAsync($"api/catalogs/{Guid.NewGuid()}", Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ---------------------------------------------------------------------------------------------
    // GET catalogs/{id}/json, POST catalogs/json
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task DownloadJson_WithManageCatalogs_IsAFileNamedForTheCatalog()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageCatalogs).SeedAsync();
        var (_, catalog) = await SeedCatalog();

        var response = await Client(actor).GetAsync($"api/catalogs/{catalog.Id}/json", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/octet-stream", response.Content.Headers.ContentType.MediaType);
        Assert.Equal($"{catalog.Name}.json", response.Content.Headers.ContentDisposition.FileNameStar);
        Assert.Contains($"\"{catalog.Name}\"", await response.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task DownloadJson_ForAnIdThatIsNotThere_Is404()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageCatalogs).SeedAsync();

        var response = await Client(actor).GetAsync($"api/catalogs/{Guid.NewGuid()}/json", Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <remarks>
    /// The round trip is the test: <c>UploadJsonAsync</c> routes the file through the same
    /// <c>privateCatalogCopyAsync</c> the copy route uses, so an uploaded catalog is a copy of the one the
    /// file came from - a fresh id, the uploader's name appended and <c>IsPublic</c> forced false.
    /// </remarks>
    [Fact]
    public async Task UploadJson_TakesADownloadedFileAndMakesACopyOfIt()
    {
        var actor = await Actor().WithName("Uploader").WithSystemPermissions(SystemPermission.ManageCatalogs)
            .SeedAsync();
        var (_, catalog) = await SeedCatalog(isPublic: true);
        var file = await Client(actor).GetStringAsync($"api/catalogs/{catalog.Id}/json", Ct);

        var response = await UploadJson(Client(actor), file);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var uploaded = await response.Content.ReadFromJsonAsync<ViewModels.Catalog>(JsonOptions, Ct);
        Assert.NotEqual(catalog.Id, uploaded.Id);
        Assert.Equal($"{catalog.Name} - Uploader", uploaded.Name);
        Assert.False(uploaded.IsPublic);
        Assert.Equal(2, await ReadBack(rb => rb.Catalogs.CountAsync(Ct)));
    }

    private async Task<(InjectTypeEntity InjectType, CatalogEntity Catalog)> SeedCatalog(
        bool isPublic = false)
    {
        var injectType = TestData.InjectType();
        await Seed(injectType);
        var catalog = TestData.Catalog(injectType.Id, isPublic: isPublic);
        await Seed(catalog);

        return (injectType, catalog);
    }

    private async Task<T> Get<T>(HttpClient client, string url)
    {
        var response = await client.GetAsync(url, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await response.Content.ReadFromJsonAsync<T>(JsonOptions, Ct);
    }

    /// <remarks>
    /// See <c>InvitationEndpointTests.InvitationBody</c> for why this is a record and why it declares no
    /// audit fields: <c>ViewModels.Catalog</c> derives from <c>Base</c>, whose <c>DateCreated</c> and
    /// <c>CreatedBy</c> are non-nullable, so a body sending them null is a 400 before the controller.
    /// </remarks>
    private static CatalogBody Body(Guid injectTypeId) =>
        new() { name = $"catalog-{Guid.NewGuid()}", injectTypeId = injectTypeId };

    private record CatalogBody
    {
        public Guid id { get; init; }
        public string name { get; init; }
        public string description { get; init; }
        public Guid injectTypeId { get; init; }
        public bool isPublic { get; init; }
    }

    /// <remarks>
    /// The <c>await</c> before the <c>using</c> falls out of scope is load-bearing; see
    /// <c>OrganizationEndpointTests.UploadJson</c>.
    /// </remarks>
    private async Task<HttpResponseMessage> UploadJson(HttpClient client, string json)
    {
        using var content = new MultipartFormDataContent();

        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(json));
        file.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        content.Add(file, "ToUpload", "catalog.json");

        return await client.PostAsync("api/catalogs/json", content, Ct);
    }
}
