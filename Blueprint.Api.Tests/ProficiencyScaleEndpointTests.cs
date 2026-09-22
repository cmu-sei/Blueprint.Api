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
using Blueprint.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Blueprint.Api.Tests;

/// <summary>
/// <c>ProficiencyScaleService</c> / <c>ProficiencyScaleController</c> - the seven routes over the scales
/// whose levels <see cref="ProficiencyLevelEndpointTests"/> covers. Reads want
/// <c>ViewCompetencyFrameworks</c>; every write, including the read-only download, wants
/// <c>ManageCompetencyFrameworks</c>.
/// </summary>
/// <remarks>
/// 401 and 403 for these routes live in <see cref="RouteAuthorizationTests"/>; this file covers the happy
/// path and the not-found path per route, per the thin protocol.
/// <para />
/// BUG: as with the level service, this one checks no permission at all - the controller's seven
/// <c>ForbiddenException</c> throws are the whole guard. Nothing here is MSEL-scoped, so there are no role
/// actors: a proficiency scale is global reference data.
/// </remarks>
public class ProficiencyScaleEndpointTests(DatabaseFixture fixture, BlueprintAppFactory factory)
    : ApiTestBase(fixture, factory), IClassFixture<BlueprintAppFactory>
{
    // ---------------------------------------------------------------------------------------------
    // GET proficiencyScales
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task GetAll_WithViewCompetencyFrameworks_ReturnsEveryScaleByNameWithItsLevels()
    {
        var actor = await Actor()
            .WithSystemPermissions(SystemPermission.ViewCompetencyFrameworks).SeedAsync();
        var beta = BlueprintAppFactory.ProficiencyScale(name: "beta");
        var alpha = BlueprintAppFactory.ProficiencyScale(name: "alpha");
        await Seed(beta, alpha);
        await Seed(BlueprintAppFactory.ProficiencyLevel(alpha.Id, "novice"));

        var response = await Client(actor).GetAsync("api/proficiencyScales", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var list = await response.Content
            .ReadFromJsonAsync<List<ViewModels.ProficiencyScale>>(JsonOptions, Ct);
        Assert.Equal(["alpha", "beta"], list.Select(x => x.Name));
        Assert.Equal(["novice"], list[0].ProficiencyLevels.Select(x => x.Name));
        Assert.Empty(list[1].ProficiencyLevels);
    }

    // ---------------------------------------------------------------------------------------------
    // GET proficiencyScales/{id}
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Get_WithViewCompetencyFrameworks_Is200_WithTheScalesLevels()
    {
        var actor = await Actor()
            .WithSystemPermissions(SystemPermission.ViewCompetencyFrameworks).SeedAsync();
        var scale = BlueprintAppFactory.ProficiencyScale(name: "wanted");
        var otherScale = BlueprintAppFactory.ProficiencyScale(name: "unwanted");
        await Seed(scale, otherScale);
        await Seed(
            BlueprintAppFactory.ProficiencyLevel(scale.Id, "mine"),
            BlueprintAppFactory.ProficiencyLevel(otherScale.Id, "theirs"));

        var response = await Client(actor).GetAsync($"api/proficiencyScales/{scale.Id}", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var answered = await response.Content
            .ReadFromJsonAsync<ViewModels.ProficiencyScale>(JsonOptions, Ct);
        Assert.Equal(scale.Id, answered.Id);
        Assert.Equal("wanted", answered.Name);
        Assert.Equal(["mine"], answered.ProficiencyLevels.Select(x => x.Name));
    }

    [Fact]
    public async Task Get_ForAnIdThatIsNotThere_Is404()
    {
        var actor = await Actor()
            .WithSystemPermissions(SystemPermission.ViewCompetencyFrameworks).SeedAsync();

        var response = await Client(actor).GetAsync($"api/proficiencyScales/{Guid.NewGuid()}", Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ApiErrorBody>(JsonOptions, Ct);
        Assert.Equal("Proficiency Scale not found", error.title);
    }

    // ---------------------------------------------------------------------------------------------
    // POST proficiencyScales
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Create_WithManageCompetencyFrameworks_Is201()
    {
        var actor = await Actor()
            .WithSystemPermissions(SystemPermission.ManageCompetencyFrameworks).SeedAsync();

        var response = await Client(actor).PostAsJsonAsync(
            "api/proficiencyScales", Body() with { name = "created" }, Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content
            .ReadFromJsonAsync<ViewModels.ProficiencyScale>(JsonOptions, Ct);
        Assert.Equal("created", created.Name);
        Assert.Equal(actor.Id, created.CreatedBy);
        Assert.Empty(created.ProficiencyLevels);
        Assert.EndsWith($"/api/proficiencyscales/{created.Id}", response.Headers.Location.ToString());

        var stored = await NewContext().ProficiencyScales.SingleAsync(x => x.Id == created.Id, Ct);
        Assert.Equal("created", stored.Name);
    }

    /// <remarks>
    /// BUG: <c>ProficiencyScaleEntity</c>'s <c>Name</c> is uniquely indexed and nothing checks it first, so
    /// a name already taken is a 500 from the index where the case deserves a 409 - the same shape as the
    /// duplicate pairs in <c>MselCompetencyService</c> and <c>TeamCompetencyService</c>, and worse here
    /// because the name is the only thing a client picks. It is also what makes the round trip below fail.
    /// </remarks>
    [Fact]
    public async Task Create_WithANameThatIsAlreadyTaken_Is500()
    {
        var actor = await Actor()
            .WithSystemPermissions(SystemPermission.ManageCompetencyFrameworks).SeedAsync();
        await Seed(BlueprintAppFactory.ProficiencyScale(name: "clash"));

        var response = await Client(actor).PostAsJsonAsync(
            "api/proficiencyScales", Body() with { name = "clash" }, Ct);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Single(await NewContext().ProficiencyScales.ToListAsync(Ct));
    }

    // ---------------------------------------------------------------------------------------------
    // PUT proficiencyScales/{id}
    // ---------------------------------------------------------------------------------------------

    /// <remarks>
    /// BUG: the PUT answers an <b>empty</b> <c>proficiencyLevels</c> where the GET beside it answers the
    /// scale's levels, because <c>UpdateAsync</c> loads the row without <c>Include</c> and maps the answer
    /// from it. The levels survive - the bare profile maps an empty collection onto an already-empty one,
    /// so EF sees no change - but a client that renames a scale and stores what came back has just lost
    /// every level it was showing. Both halves are asserted here.
    /// </remarks>
    [Fact]
    public async Task Update_WithManageCompetencyFrameworks_Is200_AnsweringNoLevelsAndKeepingThem()
    {
        var actor = await Actor()
            .WithSystemPermissions(SystemPermission.ManageCompetencyFrameworks).SeedAsync();
        var scale = BlueprintAppFactory.ProficiencyScale(name: "before");
        await Seed(scale);
        await Seed(BlueprintAppFactory.ProficiencyLevel(scale.Id, "kept"));

        var response = await Client(actor).PutAsJsonAsync(
            $"api/proficiencyScales/{scale.Id}",
            Body() with { id = scale.Id, name = "after", description = "edited" },
            Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var answered = await response.Content
            .ReadFromJsonAsync<ViewModels.ProficiencyScale>(JsonOptions, Ct);
        Assert.Equal("after", answered.Name);
        Assert.Empty(answered.ProficiencyLevels);

        await using var context = NewContext();
        var stored = await context.ProficiencyScales.SingleAsync(x => x.Id == scale.Id, Ct);
        Assert.Equal("after", stored.Name);
        Assert.Equal("edited", stored.Description);
        Assert.Equal(actor.Id, stored.ModifiedBy);
        Assert.Equal(["kept"], (await context.ProficiencyLevels.ToListAsync(Ct)).Select(x => x.Name));
    }

    [Fact]
    public async Task Update_ForAnIdThatIsNotThere_Is404()
    {
        var actor = await Actor()
            .WithSystemPermissions(SystemPermission.ManageCompetencyFrameworks).SeedAsync();
        var id = Guid.NewGuid();

        var response = await Client(actor).PutAsJsonAsync(
            $"api/proficiencyScales/{id}", Body() with { id = id }, Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ApiErrorBody>(JsonOptions, Ct);
        Assert.Equal("Proficiency Scale not found", error.title);
    }

    // ---------------------------------------------------------------------------------------------
    // DELETE proficiencyScales/{id}
    // ---------------------------------------------------------------------------------------------

    /// <remarks>
    /// BUG: the delete takes every level of the scale with it - <c>ProficiencyLevelEntity</c>'s foreign key
    /// is <c>OnDelete(DeleteBehavior.Cascade)</c> - with no dependent count, no 409 and nothing in the
    /// answer saying so. A competency framework's levels are what its assessments are recorded against, so
    /// this is the same shape as <c>DELETE injectTypes/{id}</c> (<c>3437aed</c>) one tier down.
    /// </remarks>
    [Fact]
    public async Task Delete_WithManageCompetencyFrameworks_Is204_TakingTheLevelsWithIt()
    {
        var actor = await Actor()
            .WithSystemPermissions(SystemPermission.ManageCompetencyFrameworks).SeedAsync();
        var scale = BlueprintAppFactory.ProficiencyScale(name: "doomed");
        var survivor = BlueprintAppFactory.ProficiencyScale(name: "survivor");
        await Seed(scale, survivor);
        await Seed(
            BlueprintAppFactory.ProficiencyLevel(scale.Id, "gone"),
            BlueprintAppFactory.ProficiencyLevel(survivor.Id, "stays"));

        var response = await Client(actor).DeleteAsync($"api/proficiencyScales/{scale.Id}", Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        await using var context = NewContext();
        Assert.Equal(["survivor"], (await context.ProficiencyScales.ToListAsync(Ct)).Select(x => x.Name));
        Assert.Equal(["stays"], (await context.ProficiencyLevels.ToListAsync(Ct)).Select(x => x.Name));
    }

    [Fact]
    public async Task Delete_ForAnIdThatIsNotThere_Is404()
    {
        var actor = await Actor()
            .WithSystemPermissions(SystemPermission.ManageCompetencyFrameworks).SeedAsync();

        var response = await Client(actor).DeleteAsync($"api/proficiencyScales/{Guid.NewGuid()}", Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ApiErrorBody>(JsonOptions, Ct);
        Assert.Equal("Proficiency Scale not found", error.title);
    }

    // ---------------------------------------------------------------------------------------------
    // POST proficiencyScales/json/download
    // ---------------------------------------------------------------------------------------------

    /// <remarks>
    /// BUG: the export speaks a different dialect from every response in the API. It builds its own
    /// <c>JsonSerializerOptions</c> with <c>ReferenceHandler.Preserve</c>, so the file is PascalCase behind
    /// an <c>$id</c>/<c>$values</c> wrapper with raw integers, where every response is camelCase and writes
    /// an <c>int</c> as a JSON string through <c>JsonIntegerConverter</c>. So a file assembled from what
    /// <c>GET proficiencyScales</c> answered cannot be uploaded, and the two halves of this pair are the
    /// only things that can talk to each other. Same shape as the card, inject-type and unit exports.
    /// <para />
    /// Also: the download requires <c>ManageCompetencyFrameworks</c> though it writes nothing, so a caller
    /// who may read every scale on the six other routes may not export one.
    /// </remarks>
    [Fact]
    public async Task DownloadJson_WithManageCompetencyFrameworks_IsAFileNamingOnlyTheRequestedScales()
    {
        var actor = await Actor()
            .WithSystemPermissions(SystemPermission.ManageCompetencyFrameworks).SeedAsync();
        var scale = BlueprintAppFactory.ProficiencyScale(name: "exported");
        var otherScale = BlueprintAppFactory.ProficiencyScale(name: "notexported");
        await Seed(scale, otherScale);
        await Seed(
            BlueprintAppFactory.ProficiencyLevel(scale.Id, "competent", value: 2, displayOrder: 2),
            BlueprintAppFactory.ProficiencyLevel(otherScale.Id, "elsewhere"));

        var response = await Client(actor).PostAsJsonAsync(
            "api/proficiencyScales/json/download", new[] { scale.Id }, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/octet-stream", response.Content.Headers.ContentType.MediaType);
        Assert.Equal(
            "proficiency-scale-export.json", response.Content.Headers.ContentDisposition.FileNameStar);

        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.Contains("\"$values\"", body);
        Assert.Contains("\"Name\": \"exported\"", body);
        Assert.Contains("\"Name\": \"competent\"", body);
        Assert.Contains("\"Value\": 2", body);
        Assert.DoesNotContain("\"Value\": \"2\"", body);
        Assert.DoesNotContain("notexported", body);
        Assert.DoesNotContain("elsewhere", body);
    }

    [Fact]
    public async Task DownloadJson_ForAnIdThatIsNotThere_IsAnEmptyExport()
    {
        var actor = await Actor()
            .WithSystemPermissions(SystemPermission.ManageCompetencyFrameworks).SeedAsync();
        await Seed(BlueprintAppFactory.ProficiencyScale(name: "notasked"));

        var response = await Client(actor).PostAsJsonAsync(
            "api/proficiencyScales/json/download", new[] { Guid.NewGuid() }, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain("notasked", await response.Content.ReadAsStringAsync(Ct));
    }

    // ---------------------------------------------------------------------------------------------
    // POST proficiencyScales/json
    // ---------------------------------------------------------------------------------------------

    /// <remarks>
    /// BUG: a downloaded file cannot be uploaded to the installation it came from. The upload always
    /// creates - it mints a fresh <c>Id</c> for the scale and for every level, ignoring whatever the file
    /// carried - and the scale's <c>Name</c> is uniquely indexed, so the import is a 500 from the index
    /// rather than an update, a merge or a 409. So the pair is an export/import between installations
    /// only, and nothing in either route's surface says so.
    /// </remarks>
    [Fact]
    public async Task UploadJson_OfAFileThisInstallationExported_Is500FromTheNameIndex()
    {
        var actor = await Actor()
            .WithSystemPermissions(SystemPermission.ManageCompetencyFrameworks).SeedAsync();
        var scale = BlueprintAppFactory.ProficiencyScale(name: "original");
        await Seed(scale);
        await Seed(BlueprintAppFactory.ProficiencyLevel(scale.Id, "novice"));
        var exported = await Download(Client(actor), scale.Id);

        var response = await UploadJson(Client(actor), exported);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Single(await NewContext().ProficiencyScales.ToListAsync(Ct));
    }

    /// <remarks>
    /// Renamed past the unique index, the same file imports - and every id in it is discarded, so the
    /// round trip is a copy rather than a restore. A scale exported and re-imported to recover it comes
    /// back with different keys, and anything elsewhere that recorded a level id is pointing at nothing.
    /// The stored rows are read through a fresh context rather than asserted off the upload's answer:
    /// change-tracker fix-up populates the answered navigation, so it says nothing about what was written.
    /// </remarks>
    [Fact]
    public async Task UploadJson_OfARenamedExport_CreatesASecondScaleWithFreshIds()
    {
        var actor = await Actor()
            .WithSystemPermissions(SystemPermission.ManageCompetencyFrameworks).SeedAsync();
        var scale = BlueprintAppFactory.ProficiencyScale(name: "original");
        await Seed(scale);
        var level = BlueprintAppFactory.ProficiencyLevel(scale.Id, "novice", value: 2, displayOrder: 2);
        await Seed(level);
        var exported = await Download(Client(actor), scale.Id);

        var response = await UploadJson(Client(actor), exported.Replace("original", "imported"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var created = await response.Content
            .ReadFromJsonAsync<List<ViewModels.ProficiencyScale>>(JsonOptions, Ct);
        var answered = Assert.Single(created);
        Assert.Equal("imported", answered.Name);
        Assert.NotEqual(scale.Id, answered.Id);
        Assert.Equal(actor.Id, answered.CreatedBy);

        await using var context = NewContext();
        Assert.Equal(2, await context.ProficiencyScales.CountAsync(Ct));
        var imported = await context.ProficiencyLevels
            .SingleAsync(x => x.ProficiencyScaleId == answered.Id, Ct);
        Assert.Equal("novice", imported.Name);
        Assert.Equal(2, imported.Value);
        Assert.NotEqual(level.Id, imported.Id);
    }

    private async Task<string> Download(HttpClient client, Guid id)
    {
        var response = await client.PostAsJsonAsync(
            "api/proficiencyScales/json/download", new[] { id }, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await response.Content.ReadAsStringAsync(Ct);
    }

    /// <remarks>
    /// The multipart content must be awaited inside its own <c>using</c>: <c>TestServer</c> reads the body
    /// during <c>SendAsync</c>, so returning the task unawaited disposes the content first.
    /// </remarks>
    private async Task<HttpResponseMessage> UploadJson(HttpClient client, string json)
    {
        using var content = new MultipartFormDataContent();

        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(json));
        file.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        content.Add(file, "ToUpload", "proficiency-scale-export.json");

        return await client.PostAsync("api/proficiencyScales/json", content, Ct);
    }

    /// <remarks>
    /// <c>ViewModels.ProficiencyScale</c> derives from <c>Base</c>, whose <c>DateCreated</c> and
    /// <c>CreatedBy</c> are non-nullable, so this record omits them rather than sending nulls.
    /// </remarks>
    private static ProficiencyScaleBody Body() =>
        new() { name = "seeded", description = "seeded" };

    private record ProficiencyScaleBody
    {
        public Guid id { get; init; }
        public string name { get; init; }
        public string description { get; init; }
    }

    /// <remarks>
    /// The shape <c>JsonExceptionFilter</c> answers with - <c>ViewModels.ApiError</c>, read here as a
    /// record so a test can assert which name a 404 carries.
    /// </remarks>
    private record ApiErrorBody
    {
        public int status { get; init; }
        public string title { get; init; }
        public string detail { get; init; }
    }
}
