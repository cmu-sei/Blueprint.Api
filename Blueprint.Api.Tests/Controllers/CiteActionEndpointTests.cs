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
/// <c>CiteActionService</c> / <c>CiteActionController</c> - the seven routes over the actions a team is
/// told to take at one point in an exercise, which are pushed to cite.api. Every route branches on
/// whether the row names a MSEL: a MSEL-scoped action wants <c>EditMsels</c> or MSEL ownership or
/// editorship, and a template wants <c>ManageCiteActions</c>.
/// </summary>
/// <remarks>
/// 401 and 403 for these routes live in <see cref="RouteAuthorizationTests"/>, except
/// <c>GET citeActions/templates</c>, which resolves no permission at all and so cannot be refused.
/// </remarks>
public class CiteActionEndpointTests(DatabaseFixture fixture, BlueprintAppFactory factory)
    : ApiTestBase(fixture, factory), IClassFixture<BlueprintAppFactory>
{
    // ---------------------------------------------------------------------------------------------
    // GET citeActions/templates
    // ---------------------------------------------------------------------------------------------

    /// <summary>Templates with no permissions returns only the templates.</summary>
    [Fact]
    public async Task Templates_WithNoPermissions_ReturnsOnlyTheTemplates()
    {
        var actor = await Actor().SeedAsync();
        var (msel, team) = await SeedMselAndTeam();
        var template = TestData.CiteAction();
        await Seed(template, TestData.CiteAction(msel.Id, team.Id));

        var response = await Client(actor).GetAsync("api/citeActions/templates", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var list = await response.Content.ReadFromJsonAsync<List<ViewModels.CiteAction>>(JsonOptions, Ct);
        Assert.Single(list);
        Assert.Equal(template.Id, list[0].Id);
        Assert.True(list[0].IsTemplate);
    }

    // ---------------------------------------------------------------------------------------------
    // GET msels/{mselId}/citeActions
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task GetByMsel_ForAViewerOfTheMsel_ReturnsOnlyThatMselsActions()
    {
        var (msel, team) = await SeedMselAndTeam();
        var actor = await Actor().OnMsel(msel, MselRole.Viewer).SeedAsync();
        var mine = TestData.CiteAction(msel.Id, team.Id);
        var (otherMsel, otherTeam) = await SeedMselAndTeam();
        await Seed(mine, TestData.CiteAction(otherMsel.Id, otherTeam.Id));

        var response = await Client(actor).GetAsync($"api/msels/{msel.Id}/citeActions", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var list = await response.Content.ReadFromJsonAsync<List<ViewModels.CiteAction>>(JsonOptions, Ct);
        Assert.Single(list);
        Assert.Equal(mine.Id, list[0].Id);
        Assert.Equal(team.Id, list[0].Team.Id);
    }

    /// <summary>Get by MSEL for a MSEL that is not there is answered with a 500 or 200 for a view MSELs holder.</summary>
    [Fact]
    public async Task GetByMsel_ForAMselThatIsNotThere_Is500_Or200ForAViewMselsHolder()
    {
        var stranger = await Actor().SeedAsync();
        var privileged = await Actor().WithSystemPermissions(SystemPermission.ViewMsels).SeedAsync();
        var id = Guid.NewGuid();

        var refused = await Client(stranger).GetAsync($"api/msels/{id}/citeActions", Ct);
        Assert.Equal(HttpStatusCode.InternalServerError, refused.StatusCode);

        var answered = await Client(privileged).GetAsync($"api/msels/{id}/citeActions", Ct);
        Assert.Equal(HttpStatusCode.OK, answered.StatusCode);
        Assert.Empty(await answered.Content.ReadFromJsonAsync<List<ViewModels.CiteAction>>(JsonOptions, Ct));
    }

    // ---------------------------------------------------------------------------------------------
    // GET citeActions/{id}
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Get_ForAViewerOfTheMsel_Is200()
    {
        var (msel, team) = await SeedMselAndTeam();
        var actor = await Actor().OnMsel(msel, MselRole.Viewer).SeedAsync();
        var action = TestData.CiteAction(msel.Id, team.Id, 2, 3, 4, "do the thing");
        await Seed(action);

        var response = await Client(actor).GetAsync($"api/citeActions/{action.Id}", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var answered = await response.Content.ReadFromJsonAsync<ViewModels.CiteAction>(JsonOptions, Ct);
        Assert.Equal(action.Id, answered.Id);
        Assert.Equal(msel.Id, answered.MselId);
        Assert.Equal(2, answered.MoveNumber);
        Assert.Equal(3, answered.InjectNumber);
        Assert.Equal(4, answered.ActionNumber);
        Assert.Equal("do the thing", answered.Description);
    }

    /// <remarks>
    /// <c>MoveNumber</c>, <c>InjectNumber</c> and <c>ActionNumber</c> are <c>int</c>, so
    /// <c>JsonIntegerConverter</c> writes them as JSON strings.
    /// </remarks>
    [Fact]
    public async Task Get_SerializesTheIntegersAsStrings()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewMsels).SeedAsync();
        var (msel, team) = await SeedMselAndTeam();
        var action = TestData.CiteAction(msel.Id, team.Id, 5, 6, 7);
        await Seed(action);

        var body = await Client(actor).GetStringAsync($"api/citeActions/{action.Id}", Ct);

        Assert.Contains("\"moveNumber\":\"5\"", body);
        Assert.Contains("\"injectNumber\":\"6\"", body);
        Assert.Contains("\"actionNumber\":\"7\"", body);
    }

    /// <summary>Get for an id that is not there is answered with a 500.</summary>
    [Fact]
    public async Task Get_ForAnIdThatIsNotThere_Is500()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewMsels).SeedAsync();

        var response = await Client(actor).GetAsync($"api/citeActions/{Guid.NewGuid()}", Ct);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    // ---------------------------------------------------------------------------------------------
    // POST citeActions
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Create_ForAnEditorOfTheMsel_Is201()
    {
        var (msel, team) = await SeedMselAndTeam();
        var actor = await Actor().OnMsel(msel, MselRole.Editor).SeedAsync();

        var response = await Client(actor).PostAsJsonAsync(
            "api/citeActions",
            Body(msel.Id, team.Id) with { moveNumber = 1, description = "created" },
            Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<ViewModels.CiteAction>(JsonOptions, Ct);
        Assert.Equal(msel.Id, created.MselId);
        Assert.Equal(team.Id, created.TeamId);
        Assert.Equal(1, created.MoveNumber);
        Assert.Equal(actor.Id, created.CreatedBy);
        Assert.EndsWith($"/api/citeactions/{created.Id}", response.Headers.Location.ToString());

        var stored = await ReadBack(rb => rb.CiteActions.SingleAsync(x => x.Id == created.Id, Ct));
        Assert.Equal("created", stored.Description);
    }

    /// <summary>A body naming no MSEL is a template action, and only <c>ManageCiteActions</c> is asked
    /// for.</summary>
    [Fact]
    public async Task Create_ATemplate_WithManageCiteActions_Is201()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageCiteActions).SeedAsync();

        var response = await Client(actor).PostAsJsonAsync(
            "api/citeActions", Body(null, null) with { isTemplate = true }, Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<ViewModels.CiteAction>(JsonOptions, Ct);
        Assert.Null(created.MselId);
        Assert.True(created.IsTemplate);
    }

    /// <summary>Create for a MSEL that is not there is answered with a 500.</summary>
    [Fact]
    public async Task Create_ForAMselThatIsNotThere_Is500()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();

        var response = await Client(actor).PostAsJsonAsync(
            "api/citeActions", Body(Guid.NewGuid(), null), Ct);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    /// <summary>Create does not mark the MSEL modified.</summary>
    [Fact]
    public async Task Create_DoesNotMarkTheMselModified()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();
        var (msel, team) = await SeedMselAndTeam();
        var before = (await ReadBack(rb => rb.Msels.SingleAsync(x => x.Id == msel.Id, Ct))).DateModified;

        var response = await Client(actor).PostAsJsonAsync(
            "api/citeActions", Body(msel.Id, team.Id), Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var after = await ReadBack(rb => rb.Msels.SingleAsync(x => x.Id == msel.Id, Ct));
        Assert.Equal(before, after.DateModified);
    }

    // ---------------------------------------------------------------------------------------------
    // PUT citeActions/{id}
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Update_ForAnEditorOfTheMsel_Is200()
    {
        var (msel, team) = await SeedMselAndTeam();
        var actor = await Actor().OnMsel(msel, MselRole.Editor).SeedAsync();
        var action = TestData.CiteAction(msel.Id, team.Id, description: "before");
        await Seed(action);

        var response = await Client(actor).PutAsJsonAsync(
            $"api/citeActions/{action.Id}",
            Body(msel.Id, team.Id) with { id = action.Id, description = "after", actionNumber = 9 },
            Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var stored = await ReadBack(rb => rb.CiteActions.SingleAsync(x => x.Id == action.Id, Ct));
        Assert.Equal("after", stored.Description);
        Assert.Equal(9, stored.ActionNumber);
    }

    [Fact]
    public async Task Update_ForAnIdThatIsNotThere_Is404()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();
        var (msel, team) = await SeedMselAndTeam();
        var id = Guid.NewGuid();

        var response = await Client(actor).PutAsJsonAsync(
            $"api/citeActions/{id}", Body(msel.Id, team.Id) with { id = id }, Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>Update decides from the request body and steals the row.</summary>
    [Fact]
    public async Task Update_DecidesFromTheRequestBodyAndStealsTheRow()
    {
        var (mine, myTeam) = await SeedMselAndTeam();
        var actor = await Actor().OnMsel(mine, MselRole.Editor).SeedAsync();
        var (theirs, theirTeam) = await SeedMselAndTeam();
        var action = TestData.CiteAction(theirs.Id, theirTeam.Id);
        await Seed(action);

        var response = await Client(actor).PutAsJsonAsync(
            $"api/citeActions/{action.Id}",
            Body(mine.Id, myTeam.Id) with { id = action.Id, description = "stolen" },
            Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var stored = await ReadBack(rb => rb.CiteActions.SingleAsync(x => x.Id == action.Id, Ct));
        Assert.Equal(mine.Id, stored.MselId);
        Assert.Equal("stolen", stored.Description);
    }

    // ---------------------------------------------------------------------------------------------
    // DELETE citeActions/{id}
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Delete_ForAnEditorOfTheMsel_Is204()
    {
        var (msel, team) = await SeedMselAndTeam();
        var actor = await Actor().OnMsel(msel, MselRole.Editor).SeedAsync();
        var action = TestData.CiteAction(msel.Id, team.Id);
        await Seed(action);

        var response = await Client(actor).DeleteAsync($"api/citeActions/{action.Id}", Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Empty(await ReadBack(rb => rb.CiteActions.ToListAsync(Ct)));
    }

    /// <remarks>
    /// <c>DeleteAsync</c> checks existence <em>before</em> the permission, so an unknown id is a clean
    /// 404 for a stranger as well - the contrast with <c>UpdateAsync</c> above.
    /// </remarks>
    [Fact]
    public async Task Delete_ForAnIdThatIsNotThere_Is404_EvenForAStranger()
    {
        var stranger = await Actor().SeedAsync();

        var response = await Client(stranger).DeleteAsync($"api/citeActions/{Guid.NewGuid()}", Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ---------------------------------------------------------------------------------------------
    // POST citeActions/json/download, POST citeActions/json
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task DownloadJson_WithManageCiteActions_IsAFileOfTheRequestedActions()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageCiteActions).SeedAsync();
        var wanted = TestData.CiteAction(description: "wanted");
        var unwanted = TestData.CiteAction(description: "unwanted");
        await Seed(wanted, unwanted);

        var response = await Client(actor).PostAsJsonAsync(
            "api/citeActions/json/download", new[] { wanted.Id }, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/octet-stream", response.Content.Headers.ContentType.MediaType);
        Assert.Equal("cite-action-templates.json", response.Content.Headers.ContentDisposition.FileNameStar);
        var file = await response.Content.ReadAsStringAsync(Ct);
        Assert.Contains("wanted", file);
        Assert.DoesNotContain("unwanted", file);
    }

    /// <remarks>
    /// The round trip is the test: the two file routes build their own <c>JsonSerializerOptions</c> with
    /// <c>ReferenceHandler.Preserve</c>, so the file is PascalCase with raw integers where every response
    /// is camelCase with <c>int</c> as a JSON string - but the pair agrees with itself, so a downloaded
    /// file uploads. The upload forces a fresh id, <c>IsTemplate</c>, and a null MSEL and team.
    /// </remarks>
    [Fact]
    public async Task UploadJson_TakesADownloadedFileAndMakesTemplatesOfIt()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageCiteActions).SeedAsync();
        var (msel, team) = await SeedMselAndTeam();
        var action = TestData.CiteAction(msel.Id, team.Id, 1, 2, 3, "exported");
        await Seed(action);
        var download = await Client(actor).PostAsJsonAsync(
            "api/citeActions/json/download", new[] { action.Id }, Ct);
        var file = await download.Content.ReadAsStringAsync(Ct);

        var response = await UploadJson(Client(actor), file);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<List<ViewModels.CiteAction>>(JsonOptions, Ct);
        Assert.Single(created);
        Assert.NotEqual(action.Id, created[0].Id);
        Assert.Null(created[0].MselId);
        Assert.Null(created[0].TeamId);
        Assert.True(created[0].IsTemplate);
        Assert.Equal("exported", created[0].Description);
        Assert.Equal(3, created[0].ActionNumber);
        Assert.Equal(2, await ReadBack(rb => rb.CiteActions.CountAsync(Ct)));
    }

    // ---------------------------------------------------------------------------------------------
    // Denials on the role path
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Create_is_forbidden_for_a_viewer_of_the_msel()
    {
        var (msel, team) = await SeedMselAndTeam();
        var actor = await Actor().OnMsel(msel, MselRole.Viewer).SeedAsync();

        var response = await Client(actor).PostAsJsonAsync("api/citeActions", Body(msel.Id, team.Id), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(await ReadBack(rb => rb.CiteActions.ToListAsync(Ct)));
    }

    [Fact]
    public async Task Create_is_forbidden_for_a_caller_holding_Editor_only_in_another_msel()
    {
        var (msel, team) = await SeedMselAndTeam();
        var actor = await Actor().OnNewMsel(MselRole.Editor).SeedAsync();

        var response = await Client(actor).PostAsJsonAsync("api/citeActions", Body(msel.Id, team.Id), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(await ReadBack(rb => rb.CiteActions.ToListAsync(Ct)));
    }

    [Fact]
    public async Task Create_ATemplate_is_forbidden_for_a_caller_holding_only_EditMsels()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();

        var response = await Client(actor).PostAsJsonAsync(
            "api/citeActions", Body(null, null) with { isTemplate = true }, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(await ReadBack(rb => rb.CiteActions.ToListAsync(Ct)));
    }

    [Fact]
    public async Task Update_is_forbidden_for_a_viewer_of_the_msel()
    {
        var (msel, team) = await SeedMselAndTeam();
        var actor = await Actor().OnMsel(msel, MselRole.Viewer).SeedAsync();
        var row = TestData.CiteAction(msel.Id, team.Id, description: "before");
        await Seed(row);

        var response = await Client(actor).PutAsJsonAsync(
            $"api/citeActions/{row.Id}", Body(msel.Id, team.Id) with { id = row.Id, description = "changed" }, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.NotEqual("changed", (await ReadBack(rb => rb.CiteActions.SingleAsync(x => x.Id == row.Id, Ct))).Description);
    }

    [Fact]
    public async Task Update_is_forbidden_for_a_caller_holding_Editor_only_in_another_msel()
    {
        var (msel, team) = await SeedMselAndTeam();
        var actor = await Actor().OnNewMsel(MselRole.Editor).SeedAsync();
        var row = TestData.CiteAction(msel.Id, team.Id, description: "before");
        await Seed(row);

        var response = await Client(actor).PutAsJsonAsync(
            $"api/citeActions/{row.Id}", Body(msel.Id, team.Id) with { id = row.Id, description = "changed" }, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.NotEqual("changed", (await ReadBack(rb => rb.CiteActions.SingleAsync(x => x.Id == row.Id, Ct))).Description);
    }

    [Fact]
    public async Task Delete_is_forbidden_for_a_viewer_of_the_msel()
    {
        var (msel, team) = await SeedMselAndTeam();
        var actor = await Actor().OnMsel(msel, MselRole.Viewer).SeedAsync();
        var row = TestData.CiteAction(msel.Id, team.Id, description: "before");
        await Seed(row);

        var response = await Client(actor).DeleteAsync($"api/citeActions/{row.Id}", Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Single(await ReadBack(rb => rb.CiteActions.Where(x => x.Id == row.Id).ToListAsync(Ct)));
    }

    [Fact]
    public async Task Delete_is_forbidden_for_a_caller_holding_Editor_only_in_another_msel()
    {
        var (msel, team) = await SeedMselAndTeam();
        var actor = await Actor().OnNewMsel(MselRole.Editor).SeedAsync();
        var row = TestData.CiteAction(msel.Id, team.Id, description: "before");
        await Seed(row);

        var response = await Client(actor).DeleteAsync($"api/citeActions/{row.Id}", Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Single(await ReadBack(rb => rb.CiteActions.Where(x => x.Id == row.Id).ToListAsync(Ct)));
    }

    private async Task<(MselEntity Msel, TeamEntity Team)> SeedMselAndTeam()
    {
        var msel = TestData.Msel();
        await Seed(msel);
        var team = TestData.Team(msel.Id);
        await Seed(team);

        return (msel, team);
    }

    private static CiteActionBody Body(Guid? mselId, Guid? teamId) =>
        new() { mselId = mselId, teamId = teamId, description = "seeded" };

    /// <summary>
    /// The request body. <c>ViewModels.CiteAction</c> derives from <c>Base</c>, whose <c>DateCreated</c>
    /// and <c>CreatedBy</c> are non-nullable, so this record omits them rather than declaring them
    /// nullable - a body sending them null is a 400 before the controller.
    /// </summary>
    private record CiteActionBody
    {
        public Guid id { get; init; }
        public Guid? mselId { get; init; }
        public Guid? teamId { get; init; }
        public int moveNumber { get; init; }
        public int injectNumber { get; init; }
        public int actionNumber { get; init; }
        public string description { get; init; }
        public bool isTemplate { get; init; }
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
        content.Add(file, "ToUpload", "cite-actions.json");

        return await client.PostAsync("api/citeActions/json", content, Ct);
    }
}
