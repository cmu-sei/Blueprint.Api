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
/// <c>CiteDutyService</c> / <c>CiteDutyController</c> - the seven routes over the roles a team is told to
/// fill at one point in an exercise, pushed to cite.api alongside the actions. The routes mirror
/// <c>CiteActionEndpointTests</c>' with <c>citeDuties</c> for <c>citeActions</c> and
/// <c>ManageCiteDuties</c> for <c>ManageCiteActions</c>. 401 and 403 live in
/// <c>RouteAuthorizationTests</c>.
/// </summary>
public class CiteDutyEndpointTests(DatabaseFixture fixture, BlueprintAppFactory factory)
    : ApiTestBase(fixture, factory), IClassFixture<BlueprintAppFactory>
{
    // ---------------------------------------------------------------------------------------------
    // GET citeDuties/templates
    // ---------------------------------------------------------------------------------------------

    /// <summary>Templates with no permissions returns only the templates.</summary>
    [Fact]
    public async Task Templates_WithNoPermissions_ReturnsOnlyTheTemplates()
    {
        var actor = await Actor().SeedAsync();
        var (msel, team) = await SeedMselAndTeam();
        var template = TestData.CiteDuty();
        await Seed(template, TestData.CiteDuty(msel.Id, team.Id));

        var response = await Client(actor).GetAsync("api/citeDuties/templates", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var list = await response.Content.ReadFromJsonAsync<List<ViewModels.CiteDuty>>(JsonOptions, Ct);
        Assert.Single(list);
        Assert.Equal(template.Id, list[0].Id);
        Assert.True(list[0].IsTemplate);
    }

    // ---------------------------------------------------------------------------------------------
    // GET msels/{mselId}/citeDuties
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task GetByMsel_ForAViewerOfTheMsel_ReturnsOnlyThatMselsDuties()
    {
        var (msel, team) = await SeedMselAndTeam();
        var actor = await Actor().OnMsel(msel, MselRole.Viewer).SeedAsync();
        var mine = TestData.CiteDuty(msel.Id, team.Id, "mine");
        var (otherMsel, otherTeam) = await SeedMselAndTeam();
        await Seed(mine, TestData.CiteDuty(otherMsel.Id, otherTeam.Id, "theirs"));

        var response = await Client(actor).GetAsync($"api/msels/{msel.Id}/citeDuties", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var list = await response.Content.ReadFromJsonAsync<List<ViewModels.CiteDuty>>(JsonOptions, Ct);
        Assert.Single(list);
        Assert.Equal("mine", list[0].Name);
        Assert.Equal(team.Id, list[0].Team.Id);
    }

    /// <summary>Get by MSEL for a MSEL that is not there is answered with a 500 for a caller without ViewMsels.</summary>
    [Fact]
    public async Task GetByMsel_ForAMselThatIsNotThere_Is500()
    {
        var actor = await Actor().OnNewMsel(MselRole.Viewer).SeedAsync();

        var response = await Client(actor).GetAsync($"api/msels/{Guid.NewGuid()}/citeDuties", Ct);

        var failure = await AssertJsonError<Blueprint.Api.ViewModels.ApiError>(HttpStatusCode.InternalServerError, response);
        Assert.Equal("Object reference not set to an instance of an object.", failure.Title);
        Assert.Contains("CiteDutyService.GetByMselAsync", failure.Detail);
    }

    // Same case as GetByMsel_ForAMselThatIsNotThere_Is500.
    [Fact]
    public async Task GetByMsel_ForAMselThatIsNotThere_WithViewMsels_IsAnEmptyList()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewMsels).SeedAsync();

        var response = await Client(actor).GetAsync($"api/msels/{Guid.NewGuid()}/citeDuties", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(await response.Content.ReadFromJsonAsync<List<ViewModels.CiteDuty>>(JsonOptions, Ct));
    }

    [Fact]
    public async Task GetByMsel_with_ViewMsels_and_no_role_returns_the_msels_duties()
    {
        var (msel, team) = await SeedMselAndTeam();
        var row = TestData.CiteDuty(msel.Id, team.Id);
        await Seed(row);
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewMsels).SeedAsync();

        var response = await Client(actor).GetAsync($"api/msels/{msel.Id}/citeDuties", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var list = await response.Content.ReadFromJsonAsync<List<ViewModels.CiteDuty>>(JsonOptions, Ct);
        Assert.Equal(row.Id, Assert.Single(list).Id);
    }

    // ---------------------------------------------------------------------------------------------
    // GET citeDuties/{id}
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Get_ForAViewerOfTheMsel_Is200()
    {
        var (msel, team) = await SeedMselAndTeam();
        var actor = await Actor().OnMsel(msel, MselRole.Viewer).SeedAsync();
        var duty = TestData.CiteDuty(msel.Id, team.Id, "incident commander");
        await Seed(duty);

        var response = await Client(actor).GetAsync($"api/citeDuties/{duty.Id}", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var answered = await response.Content.ReadFromJsonAsync<ViewModels.CiteDuty>(JsonOptions, Ct);
        Assert.Equal(duty.Id, answered.Id);
        Assert.Equal(msel.Id, answered.MselId);
        Assert.Equal("incident commander", answered.Name);
    }

    /// <summary>Get for an id that is not there is answered with a 500.</summary>
    [Fact]
    public async Task Get_ForAnIdThatIsNotThere_Is500()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewMsels).SeedAsync();

        var response = await Client(actor).GetAsync($"api/citeDuties/{Guid.NewGuid()}", Ct);

        var failure = await AssertJsonError<Blueprint.Api.ViewModels.ApiError>(HttpStatusCode.InternalServerError, response);
        Assert.Equal("Sequence contains no elements.", failure.Title);
        Assert.Contains("CiteDutyService.GetAsync", failure.Detail);
    }

    // ---------------------------------------------------------------------------------------------
    // POST citeDuties
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Create_ForAnEditorOfTheMsel_Is201()
    {
        var (msel, team) = await SeedMselAndTeam();
        var actor = await Actor().OnMsel(msel, MselRole.Editor).SeedAsync();

        var response = await Client(actor).PostAsJsonAsync(
            "api/citeDuties", Body(msel.Id, team.Id) with { name = "created" }, Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<ViewModels.CiteDuty>(JsonOptions, Ct);
        Assert.Equal(msel.Id, created.MselId);
        Assert.Equal(team.Id, created.TeamId);
        Assert.Equal(actor.Id, created.CreatedBy);
        Assert.EndsWith($"/api/citeduties/{created.Id}", response.Headers.Location.ToString());

        var stored = await ReadBack(rb => rb.CiteDuties.SingleAsync(x => x.Id == created.Id, Ct));
        Assert.Equal("created", stored.Name);
    }

    [Fact]
    public async Task Create_ATemplate_WithManageCiteDuties_Is201()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageCiteDuties).SeedAsync();

        var response = await Client(actor).PostAsJsonAsync(
            "api/citeDuties", Body(null, null) with { isTemplate = true }, Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<ViewModels.CiteDuty>(JsonOptions, Ct);
        Assert.Null(created.MselId);
        Assert.True(created.IsTemplate);
    }

    /// <summary>Create for a MSEL that is not there is answered with a 500.</summary>
    [Fact]
    public async Task Create_ForAMselThatIsNotThere_Is500()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();

        var response = await Client(actor).PostAsJsonAsync(
            "api/citeDuties", Body(Guid.NewGuid(), null), Ct);

        var failure = await AssertJsonError<Blueprint.Api.ViewModels.ApiError>(HttpStatusCode.InternalServerError, response);
        Assert.Equal("An error occurred while saving the entity changes. See the inner exception for details.", failure.Title);
        Assert.Contains("CiteDutyService.CreateAsync", failure.Detail);
    }

    // ---------------------------------------------------------------------------------------------
    // PUT citeDuties/{id}
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Update_ForAnEditorOfTheMsel_Is200()
    {
        var (msel, team) = await SeedMselAndTeam();
        var actor = await Actor().OnMsel(msel, MselRole.Editor).SeedAsync();
        var duty = TestData.CiteDuty(msel.Id, team.Id, "before");
        await Seed(duty);

        var response = await Client(actor).PutAsJsonAsync(
            $"api/citeDuties/{duty.Id}",
            Body(msel.Id, team.Id) with { id = duty.Id, name = "after" },
            Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var stored = await ReadBack(rb => rb.CiteDuties.SingleAsync(x => x.Id == duty.Id, Ct));
        Assert.Equal("after", stored.Name);
    }

    [Fact]
    public async Task Update_ForAnIdThatIsNotThere_Is404()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();
        var (msel, team) = await SeedMselAndTeam();
        var id = Guid.NewGuid();

        var response = await Client(actor).PutAsJsonAsync(
            $"api/citeDuties/{id}", Body(msel.Id, team.Id) with { id = id }, Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>Update decides from the request body and steals the row.</summary>
    [Fact]
    public async Task Update_DecidesFromTheRequestBodyAndStealsTheRow()
    {
        var (mine, myTeam) = await SeedMselAndTeam();
        var actor = await Actor().OnMsel(mine, MselRole.Editor).SeedAsync();
        var (theirs, theirTeam) = await SeedMselAndTeam();
        var duty = TestData.CiteDuty(theirs.Id, theirTeam.Id);
        await Seed(duty);

        var response = await Client(actor).PutAsJsonAsync(
            $"api/citeDuties/{duty.Id}",
            Body(mine.Id, myTeam.Id) with { id = duty.Id, name = "stolen" },
            Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var stored = await ReadBack(rb => rb.CiteDuties.SingleAsync(x => x.Id == duty.Id, Ct));
        Assert.Equal(mine.Id, stored.MselId);
        Assert.Equal("stolen", stored.Name);
    }

    // ---------------------------------------------------------------------------------------------
    // DELETE citeDuties/{id}
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Delete_ForAnEditorOfTheMsel_Is204()
    {
        var (msel, team) = await SeedMselAndTeam();
        var actor = await Actor().OnMsel(msel, MselRole.Editor).SeedAsync();
        var duty = TestData.CiteDuty(msel.Id, team.Id);
        await Seed(duty);

        var response = await Client(actor).DeleteAsync($"api/citeDuties/{duty.Id}", Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Empty(await ReadBack(rb => rb.CiteDuties.ToListAsync(Ct)));
    }

    /// <remarks>
    /// <c>DeleteAsync</c> checks existence before the permission, so this is a clean 404 for a stranger.
    /// </remarks>
    [Fact]
    public async Task Delete_ForAnIdThatIsNotThere_Is404_EvenForAStranger()
    {
        var stranger = await Actor().SeedAsync();

        var response = await Client(stranger).DeleteAsync($"api/citeDuties/{Guid.NewGuid()}", Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ---------------------------------------------------------------------------------------------
    // POST citeDuties/json/download, POST citeDuties/json
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task DownloadJson_WithManageCiteDuties_IsAFileOfTheRequestedDuties()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageCiteDuties).SeedAsync();
        var wanted = TestData.CiteDuty(name: "wanted");
        var unwanted = TestData.CiteDuty(name: "unwanted");
        await Seed(wanted, unwanted);

        var response = await Client(actor).PostAsJsonAsync(
            "api/citeDuties/json/download", new[] { wanted.Id }, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/octet-stream", response.Content.Headers.ContentType.MediaType);
        Assert.Equal("cite-duty-templates.json", response.Content.Headers.ContentDisposition.FileNameStar);
        var file = await response.Content.ReadAsStringAsync(Ct);
        Assert.Contains("wanted", file);
        Assert.DoesNotContain("unwanted", file);
    }

    /// <remarks>
    /// The round trip: the file is PascalCase behind <c>ReferenceHandler.Preserve</c>'s
    /// <c>$id</c>/<c>$values</c> wrapper where every response is camelCase, but the pair agrees with
    /// itself. The upload forces a fresh id, <c>IsTemplate</c>, and a null MSEL and team.
    /// </remarks>
    [Fact]
    public async Task UploadJson_TakesADownloadedFileAndMakesTemplatesOfIt()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageCiteDuties).SeedAsync();
        var (msel, team) = await SeedMselAndTeam();
        var duty = TestData.CiteDuty(msel.Id, team.Id, "exported");
        await Seed(duty);
        var download = await Client(actor).PostAsJsonAsync(
            "api/citeDuties/json/download", new[] { duty.Id }, Ct);
        var file = await download.Content.ReadAsStringAsync(Ct);

        var response = await UploadJson(Client(actor), file);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<List<ViewModels.CiteDuty>>(JsonOptions, Ct);
        Assert.Single(created);
        Assert.NotEqual(duty.Id, created[0].Id);
        Assert.Null(created[0].MselId);
        Assert.Null(created[0].TeamId);
        Assert.True(created[0].IsTemplate);
        Assert.Equal("exported", created[0].Name);
        Assert.Equal(2, await ReadBack(rb => rb.CiteDuties.CountAsync(Ct)));
    }

    // ---------------------------------------------------------------------------------------------
    // Denials on the role path
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Create_is_forbidden_for_a_viewer_of_the_msel()
    {
        var (msel, team) = await SeedMselAndTeam();
        var actor = await Actor().OnMsel(msel, MselRole.Viewer).SeedAsync();

        var response = await Client(actor).PostAsJsonAsync("api/citeDuties", Body(msel.Id, team.Id), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(await ReadBack(rb => rb.CiteDuties.ToListAsync(Ct)));
    }

    [Fact]
    public async Task Create_is_forbidden_for_a_caller_holding_Editor_only_in_another_msel()
    {
        var (msel, team) = await SeedMselAndTeam();
        var actor = await Actor().OnNewMsel(MselRole.Editor).SeedAsync();

        var response = await Client(actor).PostAsJsonAsync("api/citeDuties", Body(msel.Id, team.Id), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(await ReadBack(rb => rb.CiteDuties.ToListAsync(Ct)));
    }

    [Fact]
    public async Task Create_ATemplate_is_forbidden_for_a_caller_holding_only_EditMsels()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();

        var response = await Client(actor).PostAsJsonAsync(
            "api/citeDuties", Body(null, null) with { isTemplate = true }, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(await ReadBack(rb => rb.CiteDuties.ToListAsync(Ct)));
    }

    [Fact]
    public async Task Update_is_forbidden_for_a_viewer_of_the_msel()
    {
        var (msel, team) = await SeedMselAndTeam();
        var actor = await Actor().OnMsel(msel, MselRole.Viewer).SeedAsync();
        var row = TestData.CiteDuty(msel.Id, team.Id, "before");
        await Seed(row);

        var response = await Client(actor).PutAsJsonAsync(
            $"api/citeDuties/{row.Id}", Body(msel.Id, team.Id) with { id = row.Id, name = "changed" }, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.NotEqual("changed", (await ReadBack(rb => rb.CiteDuties.SingleAsync(x => x.Id == row.Id, Ct))).Name);
    }

    [Fact]
    public async Task Update_is_forbidden_for_a_caller_holding_Editor_only_in_another_msel()
    {
        var (msel, team) = await SeedMselAndTeam();
        var actor = await Actor().OnNewMsel(MselRole.Editor).SeedAsync();
        var row = TestData.CiteDuty(msel.Id, team.Id, "before");
        await Seed(row);

        var response = await Client(actor).PutAsJsonAsync(
            $"api/citeDuties/{row.Id}", Body(msel.Id, team.Id) with { id = row.Id, name = "changed" }, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.NotEqual("changed", (await ReadBack(rb => rb.CiteDuties.SingleAsync(x => x.Id == row.Id, Ct))).Name);
    }

    [Fact]
    public async Task Delete_is_forbidden_for_a_viewer_of_the_msel()
    {
        var (msel, team) = await SeedMselAndTeam();
        var actor = await Actor().OnMsel(msel, MselRole.Viewer).SeedAsync();
        var row = TestData.CiteDuty(msel.Id, team.Id, "before");
        await Seed(row);

        var response = await Client(actor).DeleteAsync($"api/citeDuties/{row.Id}", Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Single(await ReadBack(rb => rb.CiteDuties.Where(x => x.Id == row.Id).ToListAsync(Ct)));
    }

    [Fact]
    public async Task Delete_is_forbidden_for_a_caller_holding_Editor_only_in_another_msel()
    {
        var (msel, team) = await SeedMselAndTeam();
        var actor = await Actor().OnNewMsel(MselRole.Editor).SeedAsync();
        var row = TestData.CiteDuty(msel.Id, team.Id, "before");
        await Seed(row);

        var response = await Client(actor).DeleteAsync($"api/citeDuties/{row.Id}", Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Single(await ReadBack(rb => rb.CiteDuties.Where(x => x.Id == row.Id).ToListAsync(Ct)));
    }

    // ---------------------------------------------------------------------------------------------
    // Templates: the ManageCiteDuties tier
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Update_of_a_template_with_ManageCiteDuties_stores_the_change()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageCiteDuties).SeedAsync();
        var row = TestData.CiteDuty(name: "before");
        await Seed(row);

        var response = await Client(actor).PutAsJsonAsync(
            $"api/citeDuties/{row.Id}", Body(null, null) with { id = row.Id, name = "after", isTemplate = true }, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("after", (await ReadBack(rb => rb.CiteDuties.SingleAsync(x => x.Id == row.Id, Ct))).Name);
    }

    [Fact]
    public async Task Update_of_a_template_is_forbidden_for_a_caller_holding_only_EditMsels()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();
        var row = TestData.CiteDuty(name: "before");
        await Seed(row);

        var response = await Client(actor).PutAsJsonAsync(
            $"api/citeDuties/{row.Id}", Body(null, null) with { id = row.Id, name = "changed", isTemplate = true }, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("before", (await ReadBack(rb => rb.CiteDuties.SingleAsync(x => x.Id == row.Id, Ct))).Name);
    }

    [Fact]
    public async Task Delete_of_a_template_with_ManageCiteDuties_removes_it()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageCiteDuties).SeedAsync();
        var row = TestData.CiteDuty();
        await Seed(row);

        var response = await Client(actor).DeleteAsync($"api/citeDuties/{row.Id}", Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Empty(await ReadBack(rb => rb.CiteDuties.Where(x => x.Id == row.Id).ToListAsync(Ct)));
    }

    [Fact]
    public async Task Delete_of_a_template_is_forbidden_for_a_caller_holding_only_EditMsels()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();
        var row = TestData.CiteDuty();
        await Seed(row);

        var response = await Client(actor).DeleteAsync($"api/citeDuties/{row.Id}", Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Single(await ReadBack(rb => rb.CiteDuties.Where(x => x.Id == row.Id).ToListAsync(Ct)));
    }

    private async Task<(MselEntity Msel, TeamEntity Team)> SeedMselAndTeam()
    {
        var msel = TestData.Msel();
        await Seed(msel);
        var team = TestData.Team(msel.Id);
        await Seed(team);

        return (msel, team);
    }

    private static CiteDutyBody Body(Guid? mselId, Guid? teamId) =>
        new() { mselId = mselId, teamId = teamId, name = "seeded" };

    /// <remarks>
    /// <c>ViewModels.CiteDuty</c> derives from <c>Base</c>, whose <c>DateCreated</c> and
    /// <c>CreatedBy</c> are non-nullable, so this record omits them rather than sending nulls.
    /// </remarks>
    private record CiteDutyBody
    {
        public Guid id { get; init; }
        public Guid? mselId { get; init; }
        public Guid? teamId { get; init; }
        public string name { get; init; }
        public bool isTemplate { get; init; }
    }

    private async Task<HttpResponseMessage> UploadJson(HttpClient client, string json)
    {
        using var content = new MultipartFormDataContent();

        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(json));
        file.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        content.Add(file, "ToUpload", "cite-duties.json");

        return await client.PostAsync("api/citeDuties/json", content, Ct);
    }
}
