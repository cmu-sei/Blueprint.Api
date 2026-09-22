// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Threading.Tasks;
using Blueprint.Api.Data.Enumerations;
using Blueprint.Api.Data.Models;
using Blueprint.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Blueprint.Api.Tests;

/// <summary>
/// <c>CiteDutyService</c> / <c>CiteDutyController</c> - the seven routes over the roles a team is told to
/// fill at one point in an exercise, pushed to cite.api alongside the actions.
/// </summary>
/// <remarks>
/// This pair is a line-for-line twin of <see cref="CiteActionEndpointTests"/>'s: the same seven routes
/// with <c>citeDuties</c> for <c>citeActions</c> and <c>ManageCiteDuties</c> for <c>ManageCiteActions</c>,
/// the same MSEL-or-template permission branch on every write, and a <c>string Name</c> where the action
/// carries three integers and a description. Every defect characterized there is present here, so this
/// file pins the same behaviour and leaves the reasoning in the other file rather than repeating it.
/// <para />
/// 401 and 403 live in <see cref="RouteAuthorizationTests"/>, except <c>GET citeDuties/templates</c>,
/// which resolves no permission at all.
/// </remarks>
public class CiteDutyEndpointTests(DatabaseFixture fixture, BlueprintAppFactory factory)
    : ApiTestBase(fixture, factory), IClassFixture<BlueprintAppFactory>
{
    // ---------------------------------------------------------------------------------------------
    // GET citeDuties/templates
    // ---------------------------------------------------------------------------------------------

    /// <remarks>
    /// BUG: no permission and no MSEL - the route asks the caller for nothing but a token.
    /// </remarks>
    [Fact]
    public async Task Templates_WithNoPermissions_ReturnsOnlyTheTemplates()
    {
        var actor = await Actor().SeedAsync();
        var (msel, team) = await SeedMselAndTeam();
        var template = BlueprintAppFactory.CiteDuty();
        await Seed(template, BlueprintAppFactory.CiteDuty(msel.Id, team.Id));

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
        var mine = BlueprintAppFactory.CiteDuty(msel.Id, team.Id, "mine");
        var (otherMsel, otherTeam) = await SeedMselAndTeam();
        await Seed(mine, BlueprintAppFactory.CiteDuty(otherMsel.Id, otherTeam.Id, "theirs"));

        var response = await Client(actor).GetAsync($"api/msels/{msel.Id}/citeDuties", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var list = await response.Content.ReadFromJsonAsync<List<ViewModels.CiteDuty>>(JsonOptions, Ct);
        Assert.Single(list);
        Assert.Equal("mine", list[0].Name);
        Assert.Equal(team.Id, list[0].Team.Id);
    }

    /// <remarks>
    /// BUG: the template fall-through dereferences an unguarded <c>FindAsync</c>
    /// (<c>CiteDutyService.cs:67-68</c>), so whether a MSEL exists depends on who asks.
    /// </remarks>
    [Fact]
    public async Task GetByMsel_ForAMselThatIsNotThere_Is500_Or200ForAViewMselsHolder()
    {
        var stranger = await Actor().SeedAsync();
        var privileged = await Actor().WithSystemPermissions(SystemPermission.ViewMsels).SeedAsync();
        var id = Guid.NewGuid();

        var refused = await Client(stranger).GetAsync($"api/msels/{id}/citeDuties", Ct);
        Assert.Equal(HttpStatusCode.InternalServerError, refused.StatusCode);

        var answered = await Client(privileged).GetAsync($"api/msels/{id}/citeDuties", Ct);
        Assert.Equal(HttpStatusCode.OK, answered.StatusCode);
        Assert.Empty(await answered.Content.ReadFromJsonAsync<List<ViewModels.CiteDuty>>(JsonOptions, Ct));
    }

    // ---------------------------------------------------------------------------------------------
    // GET citeDuties/{id}
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Get_ForAViewerOfTheMsel_Is200()
    {
        var (msel, team) = await SeedMselAndTeam();
        var actor = await Actor().OnMsel(msel, MselRole.Viewer).SeedAsync();
        var duty = BlueprintAppFactory.CiteDuty(msel.Id, team.Id, "incident commander");
        await Seed(duty);

        var response = await Client(actor).GetAsync($"api/citeDuties/{duty.Id}", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var answered = await response.Content.ReadFromJsonAsync<ViewModels.CiteDuty>(JsonOptions, Ct);
        Assert.Equal(duty.Id, answered.Id);
        Assert.Equal(msel.Id, answered.MselId);
        Assert.Equal("incident commander", answered.Name);
    }

    /// <remarks>
    /// BUG: <c>SingleAsync</c> plus a dead null check naming <c>DataValueEntity</c>
    /// (<c>CiteDutyService.cs:88</c>) - the <b>sixth</b> copy of that line on the branch, after
    /// <c>OrganizationService</c>, <c>MoveService</c>, <c>CardService</c>, <c>InjectService</c> and
    /// <c>CiteActionService</c>. An unknown id is a 500.
    /// </remarks>
    [Fact]
    public async Task Get_ForAnIdThatIsNotThere_Is500()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewMsels).SeedAsync();

        var response = await Client(actor).GetAsync($"api/citeDuties/{Guid.NewGuid()}", Ct);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
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

        var stored = await NewContext().CiteDuties.SingleAsync(x => x.Id == created.Id, Ct);
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

    /// <remarks>
    /// BUG: nothing validates the MSEL the body names, so an unknown one is a foreign-key 500.
    /// </remarks>
    [Fact]
    public async Task Create_ForAMselThatIsNotThere_Is500()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();

        var response = await Client(actor).PostAsJsonAsync(
            "api/citeDuties", Body(Guid.NewGuid(), null), Ct);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    // ---------------------------------------------------------------------------------------------
    // PUT citeDuties/{id}
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Update_ForAnEditorOfTheMsel_Is200()
    {
        var (msel, team) = await SeedMselAndTeam();
        var actor = await Actor().OnMsel(msel, MselRole.Editor).SeedAsync();
        var duty = BlueprintAppFactory.CiteDuty(msel.Id, team.Id, "before");
        await Seed(duty);

        var response = await Client(actor).PutAsJsonAsync(
            $"api/citeDuties/{duty.Id}",
            Body(msel.Id, team.Id) with { id = duty.Id, name = "after" },
            Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var stored = await NewContext().CiteDuties.SingleAsync(x => x.Id == duty.Id, Ct);
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

    /// <remarks>
    /// BUG: <c>UpdateAsync</c> decides from the request body's <c>MselId</c> before it looks the row up
    /// and the profile maps that id both ways, so an editor of any MSEL may steal every other MSEL's
    /// duties. Tenth instance of the shape on the branch; <c>DeleteAsync</c> below is the model to copy.
    /// </remarks>
    [Fact]
    public async Task Update_DecidesFromTheRequestBodyAndStealsTheRow()
    {
        var (mine, myTeam) = await SeedMselAndTeam();
        var actor = await Actor().OnMsel(mine, MselRole.Editor).SeedAsync();
        var (theirs, theirTeam) = await SeedMselAndTeam();
        var duty = BlueprintAppFactory.CiteDuty(theirs.Id, theirTeam.Id);
        await Seed(duty);

        var response = await Client(actor).PutAsJsonAsync(
            $"api/citeDuties/{duty.Id}",
            Body(mine.Id, myTeam.Id) with { id = duty.Id, name = "stolen" },
            Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var stored = await NewContext().CiteDuties.SingleAsync(x => x.Id == duty.Id, Ct);
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
        var duty = BlueprintAppFactory.CiteDuty(msel.Id, team.Id);
        await Seed(duty);

        var response = await Client(actor).DeleteAsync($"api/citeDuties/{duty.Id}", Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Empty(await NewContext().CiteDuties.ToListAsync(Ct));
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
        var wanted = BlueprintAppFactory.CiteDuty(name: "wanted");
        var unwanted = BlueprintAppFactory.CiteDuty(name: "unwanted");
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
        var duty = BlueprintAppFactory.CiteDuty(msel.Id, team.Id, "exported");
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
        Assert.Equal(2, await NewContext().CiteDuties.CountAsync(Ct));
    }

    private async Task<(MselEntity Msel, TeamEntity Team)> SeedMselAndTeam()
    {
        var msel = BlueprintAppFactory.Msel();
        await Seed(msel);
        var team = BlueprintAppFactory.Team(msel.Id);
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
