// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Blueprint.Api.Data.Enumerations;
using Blueprint.Api.Data.Models;
using Blueprint.Api.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Blueprint.Api.Tests.Controllers;

/// <summary><c>PlayerApplicationTeamService</c> / <c>PlayerApplicationTeamController</c> - the eight routes
/// over the rows deciding which of a MSEL's teams sees which of its Player applications. Reads want
/// <c>ViewMsels</c> or a view of the MSEL; every write wants <c>EditMsels</c> and nothing else.</summary>
public class PlayerApplicationTeamEndpointTests(DatabaseFixture fixture, BlueprintAppFactory factory)
    : ApiTestBase(fixture, factory), IClassFixture<BlueprintAppFactory>
{
    // ---------------------------------------------------------------------------------------------
    // GET teamplayerApplications
    // ---------------------------------------------------------------------------------------------

    /// <summary>Get all with view MSELs returns every row in the installation.</summary>
    [Fact]
    public async Task GetAll_WithViewMsels_ReturnsEveryRowInTheInstallation()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewMsels).SeedAsync();
        var mine = await SeedGraph();
        var theirs = await SeedGraph();

        var response = await Client(actor).GetAsync("api/teamplayerApplications", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var list = await response.Content
            .ReadFromJsonAsync<List<ViewModels.PlayerApplicationTeam>>(JsonOptions, Ct);
        Assert.Equal(2, list.Count);
        Assert.Contains(mine.Row.Id, list.Select(x => x.Id));
        Assert.Contains(theirs.Row.Id, list.Select(x => x.Id));
    }

    // ---------------------------------------------------------------------------------------------
    // GET msels/{mselId}/teamplayerApplications
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task GetByMsel_ForAViewerOfTheMsel_ReturnsOnlyThatMselsRows()
    {
        var mine = await SeedGraph();
        var actor = await Actor().OnMsel(mine.Msel, MselRole.Viewer).SeedAsync();
        await SeedGraph();

        var response = await Client(actor)
            .GetAsync($"api/msels/{mine.Msel.Id}/teamplayerApplications", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var list = await response.Content
            .ReadFromJsonAsync<List<ViewModels.PlayerApplicationTeam>>(JsonOptions, Ct);
        Assert.Single(list);
        Assert.Equal(mine.Row.Id, list[0].Id);
        Assert.Equal(mine.Team.Id, list[0].TeamId);
    }

    /// <summary>Get by MSEL for a MSEL that is not there is answered with a 500 for a caller without ViewMsels.</summary>
    [Fact]
    public async Task GetByMsel_ForAMselThatIsNotThere_Is500()
    {
        var stranger = await Actor().SeedAsync();

        var response = await Client(stranger).GetAsync($"api/msels/{Guid.NewGuid()}/teamplayerApplications", Ct);

        var failure = await AssertJsonError<Blueprint.Api.ViewModels.ApiError>(HttpStatusCode.InternalServerError, response);
        Assert.Equal("Object reference not set to an instance of an object.", failure.Title);
        Assert.Contains("PlayerApplicationTeamService.GetByMselAsync", failure.Detail);
    }

    // Same case as GetByMsel_ForAMselThatIsNotThere_Is500.
    [Fact]
    public async Task GetByMsel_ForAMselThatIsNotThere_WithViewMsels_IsAnEmptyList()
    {
        var privileged = await Actor().WithSystemPermissions(SystemPermission.ViewMsels).SeedAsync();

        var response = await Client(privileged).GetAsync($"api/msels/{Guid.NewGuid()}/teamplayerApplications", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(await response.Content.ReadFromJsonAsync<List<ViewModels.PlayerApplicationTeam>>(JsonOptions, Ct));
    }

    // ---------------------------------------------------------------------------------------------
    // GET playerApplications/{playerApplicationId}/teamplayerApplications
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task GetByPlayerApplication_ForAViewerOfTheMsel_ReturnsOnlyThatApplicationsRows()
    {
        var graph = await SeedGraph();
        var actor = await Actor().OnMsel(graph.Msel, MselRole.Viewer).SeedAsync();
        var other = TestData.PlayerApplication(graph.Msel.Id, "other");
        await Seed(other);
        await Seed(TestData.PlayerApplicationTeam(other.Id, graph.Team.Id, 2));

        var response = await Client(actor).GetAsync(
            $"api/playerApplications/{graph.Application.Id}/teamplayerApplications", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var list = await response.Content
            .ReadFromJsonAsync<List<ViewModels.PlayerApplicationTeam>>(JsonOptions, Ct);
        Assert.Single(list);
        Assert.Equal(graph.Row.Id, list[0].Id);
    }

    /// <summary>Get by player application for an id that is not there is answered with a 500 for a caller without ViewMsels.</summary>
    [Fact]
    public async Task GetByPlayerApplication_ForAnIdThatIsNotThere_Is500()
    {
        var stranger = await Actor().SeedAsync();

        var response = await Client(stranger).GetAsync($"api/playerApplications/{Guid.NewGuid()}/teamplayerApplications", Ct);

        var failure = await AssertJsonError<Blueprint.Api.ViewModels.ApiError>(HttpStatusCode.InternalServerError, response);
        Assert.Equal("Object reference not set to an instance of an object.", failure.Title);
        Assert.Contains("PlayerApplicationTeamService.GetByPlayerApplicationAsync", failure.Detail);
    }

    // Same case as GetByPlayerApplication_ForAnIdThatIsNotThere_Is500.
    [Fact]
    public async Task GetByPlayerApplication_ForAnIdThatIsNotThere_WithViewMsels_IsAnEmptyList()
    {
        var privileged = await Actor().WithSystemPermissions(SystemPermission.ViewMsels).SeedAsync();

        var response = await Client(privileged).GetAsync($"api/playerApplications/{Guid.NewGuid()}/teamplayerApplications", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(await response.Content.ReadFromJsonAsync<List<ViewModels.PlayerApplicationTeam>>(JsonOptions, Ct));
    }

    /// <summary>A template belongs to no MSEL and so to no team: its list is empty for anyone, rather than
    /// the 500 a MSEL lookup on a null id would give a caller without <c>ViewMsels</c>.</summary>
    [Fact]
    public async Task GetByPlayerApplication_OfATemplate_IsAnEmptyList()
    {
        var stranger = await Actor().SeedAsync();
        var template = TestData.PlayerApplicationTemplate();
        await Seed(template);

        var response = await Client(stranger).GetAsync($"api/playerApplications/{template.Id}/teamplayerApplications", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(await response.Content.ReadFromJsonAsync<List<ViewModels.PlayerApplicationTeam>>(JsonOptions, Ct));
    }

    // ---------------------------------------------------------------------------------------------
    // GET teamplayerApplications/{id}
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Get_ForAViewerOfTheMsel_Is200()
    {
        var graph = await SeedGraph();
        var actor = await Actor().OnMsel(graph.Msel, MselRole.Viewer).SeedAsync();

        var response = await Client(actor)
            .GetAsync($"api/teamplayerApplications/{graph.Row.Id}", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var answered = await response.Content
            .ReadFromJsonAsync<ViewModels.PlayerApplicationTeam>(JsonOptions, Ct);
        Assert.Equal(graph.Row.Id, answered.Id);
        Assert.Equal(graph.Application.Id, answered.PlayerApplicationId);
        Assert.Equal(graph.Team.Id, answered.TeamId);
        Assert.Equal(1, answered.DisplayOrder);
    }

    /// <summary>Get for an id that is not there is answered with a 500 for a caller without ViewMsels.</summary>
    [Fact]
    public async Task Get_ForAnIdThatIsNotThere_Is500()
    {
        var stranger = await Actor().SeedAsync();

        var response = await Client(stranger).GetAsync($"api/teamplayerApplications/{Guid.NewGuid()}", Ct);

        var failure = await AssertJsonError<Blueprint.Api.ViewModels.ApiError>(HttpStatusCode.InternalServerError, response);
        Assert.Equal("Object reference not set to an instance of an object.", failure.Title);
        Assert.Contains("PlayerApplicationTeamService.GetAsync", failure.Detail);
    }

    [Fact]
    public async Task Get_ForAnIdThatIsNotThere_WithViewMsels_Is404()
    {
        var privileged = await Actor().WithSystemPermissions(SystemPermission.ViewMsels).SeedAsync();

        var response = await Client(privileged).GetAsync($"api/teamplayerApplications/{Guid.NewGuid()}", Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ---------------------------------------------------------------------------------------------
    // POST teamplayerApplications
    // ---------------------------------------------------------------------------------------------

    /// <summary>Create with edit MSELs is answered with a 201 and clamps the display order into range.</summary>
    [Fact]
    public async Task Create_WithEditMsels_Is201_AndClampsTheDisplayOrderIntoRange()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();
        var msel = TestData.Msel();
        await Seed(msel);
        var team = TestData.Team(msel.Id);
        var application = TestData.PlayerApplication(msel.Id);
        await Seed(team, application);

        var response = await Client(actor).PostAsJsonAsync(
            "api/teamplayerApplications", Body(application.Id, team.Id), Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content
            .ReadFromJsonAsync<ViewModels.PlayerApplicationTeam>(JsonOptions, Ct);
        Assert.Equal(1, created.DisplayOrder);

        var location = response.Headers.Location.ToString();
        Assert.Contains(created.Id.ToString(), location);
        Assert.DoesNotContain("playerapplicationteams", location);

        var stored = await ReadBack(rb => rb.PlayerApplicationTeams
            .SingleAsync(x => x.Id == created.Id, Ct));
        Assert.Equal(team.Id, stored.TeamId);
        Assert.Equal(1, stored.DisplayOrder);
    }

    /// <summary>Creating a row at a place in the team's order shifts the rows from that place down by one.</summary>
    [Fact]
    public async Task Create_InTheMiddle_ShiftsTheRestOfTheOrder()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();
        var graph = await SeedGraph();
        var second = TestData.PlayerApplication(graph.Msel.Id, "second", url: "http://second.example/");
        var added = TestData.PlayerApplication(graph.Msel.Id, "added", url: "http://added.example/");
        await Seed(second, added);
        var secondRow = TestData.PlayerApplicationTeam(second.Id, graph.Team.Id, 2);
        await Seed(secondRow);

        var response = await Client(actor).PostAsJsonAsync(
            "api/teamplayerApplications", Body(added.Id, graph.Team.Id) with { displayOrder = 2 }, Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<ViewModels.PlayerApplicationTeam>(JsonOptions, Ct);
        var order = await ReadBack(db => db.PlayerApplicationTeams
            .Where(x => x.TeamId == graph.Team.Id)
            .OrderBy(x => x.DisplayOrder)
            .Select(x => new { x.Id, x.DisplayOrder })
            .ToListAsync(Ct));
        Assert.Equal([graph.Row.Id, created.Id, secondRow.Id], order.Select(x => x.Id));
        Assert.Equal([1, 2, 3], order.Select(x => x.DisplayOrder));
    }

    /// <summary>No write consults a MSEL role, so an owner of the MSEL without EditMsels is refused.</summary>
    [Fact]
    public async Task NoWriteConsultsAMselRole_SoAnOwnerIsRefused()
    {
        var msel = TestData.Msel();
        await Seed(msel);
        var team = TestData.Team(msel.Id);
        var application = TestData.PlayerApplication(msel.Id);
        await Seed(team, application);
        var owner = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        var response = await Client(owner).PostAsJsonAsync(
            "api/teamplayerApplications", Body(application.Id, team.Id), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>
    /// A team shows each view once in Player: giving it the same application again, or another for a view it has
    /// (CITE opens on the dashboard, as CITE Dashboard does), is a 409 naming what it has, and nothing is added.
    /// </summary>
    [Fact]
    public async Task Create_ForAViewTheTeamHasAlready_Is409()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();
        var msel = TestData.Msel();
        await Seed(msel);
        var team = TestData.Team(msel.Id);
        team.ShortName = "BLUE";
        var cite = TestData.PlayerApplication(msel.Id, name: "CITE", url: "{citeUrl}?evaluation={citeEvaluationId}{theme}");
        var dashboard = TestData.PlayerApplication(msel.Id, name: "CITE Dashboard", url: "{citeUrl}?section=dashboard&evaluation={citeEvaluationId}{theme}");
        await Seed(team, cite, dashboard);
        await Seed(TestData.PlayerApplicationTeam(cite.Id, team.Id, 1));

        var again = await Client(actor).PostAsJsonAsync("api/teamplayerApplications", Body(cite.Id, team.Id), Ct);
        var sameView = await Client(actor).PostAsJsonAsync("api/teamplayerApplications", Body(dashboard.Id, team.Id), Ct);

        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, sameView.StatusCode);
        Assert.Contains("BLUE already has CITE, which shows the same view as CITE Dashboard", await sameView.Content.ReadAsStringAsync(Ct));
        Assert.Equal(1, await ReadBack(rb => rb.PlayerApplicationTeams.CountAsync(x => x.TeamId == team.Id, Ct)));
    }

    /// <summary>
    /// A team that already shows two applications for one view keeps both: only a new link is refused, so the 409 does
    /// not strand a MSEL saved before it. Another view can still be added to that team.
    /// </summary>
    [Fact]
    public async Task Create_ForATeamWithTwoApplicationsForOneView_AddsAnotherViewAndKeepsBoth()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();
        var msel = TestData.Msel();
        await Seed(msel);
        var team = TestData.Team(msel.Id);
        var cite = TestData.PlayerApplication(msel.Id, name: "CITE", url: "{citeUrl}?evaluation={citeEvaluationId}{theme}");
        var dashboard = TestData.PlayerApplication(msel.Id, name: "CITE Dashboard", url: "{citeUrl}?section=dashboard&evaluation={citeEvaluationId}{theme}");
        var gallery = TestData.PlayerApplication(msel.Id, name: "Gallery", url: "{galleryUrl}?exhibit={galleryExhibitId}{theme}");
        await Seed(team, cite, dashboard, gallery);
        await Seed(TestData.PlayerApplicationTeam(cite.Id, team.Id, 1), TestData.PlayerApplicationTeam(dashboard.Id, team.Id, 2));

        var response = await Client(actor).PostAsJsonAsync("api/teamplayerApplications", Body(gallery.Id, team.Id), Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(3, await ReadBack(rb => rb.PlayerApplicationTeams.CountAsync(x => x.TeamId == team.Id, Ct)));
    }

    [Fact]
    public async Task Create_WithEditMsels_and_no_role_on_the_msel_Is201()
    {
        var msel = TestData.Msel();
        await Seed(msel);
        var team = TestData.Team(msel.Id);
        var application = TestData.PlayerApplication(msel.Id);
        await Seed(team, application);
        var stranger = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();

        var response = await Client(stranger).PostAsJsonAsync(
            "api/teamplayerApplications", Body(application.Id, team.Id), Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    /// <summary>
    /// The application and the team must belong to the same MSEL, even for an <c>EditMsels</c> holder.
    /// </summary>
    [Fact]
    public async Task Create_ForATeamOfAnotherMsel_Is404AndCreatesNothing()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();
        var msel = TestData.Msel();
        var elsewhere = TestData.Msel();
        await Seed(msel, elsewhere);
        var team = TestData.Team(elsewhere.Id);
        var application = TestData.PlayerApplication(msel.Id);
        await Seed(team, application);

        var response = await Client(actor).PostAsJsonAsync(
            "api/teamplayerApplications", Body(application.Id, team.Id), Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.False(await ReadBack(db => db.PlayerApplicationTeams.AnyAsync(x => x.PlayerApplicationId == application.Id, Ct)));
    }

    /// <summary>A template is copied into a MSEL before any team sees it, so it takes no team row.</summary>
    [Fact]
    public async Task Create_ForATemplate_Is404AndCreatesNothing()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();
        var graph = await SeedGraph();
        var template = TestData.PlayerApplicationTemplate();
        await Seed(template);

        var response = await Client(actor).PostAsJsonAsync(
            "api/teamplayerApplications", Body(template.Id, graph.Team.Id), Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.False(await ReadBack(db => db.PlayerApplicationTeams.AnyAsync(x => x.PlayerApplicationId == template.Id, Ct)));
    }

    [Fact]
    public async Task Create_ForAnApplicationThatIsNotThere_Is404()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();
        var graph = await SeedGraph();

        var response = await Client(actor).PostAsJsonAsync(
            "api/teamplayerApplications", Body(Guid.NewGuid(), graph.Team.Id), Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(1, await ReadBack(db => db.PlayerApplicationTeams.CountAsync(x => x.TeamId == graph.Team.Id, Ct)));
    }

    // ---------------------------------------------------------------------------------------------
    // PUT teamplayerApplications/{id}
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Moving an application to another place in its team's order shifts the team's other applications
    /// to make room, so a team's order is changed in place rather than by removing and re-adding the row.
    /// </summary>
    [Fact]
    public async Task Update_MovesTheApplicationInItsTeamsOrder()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();
        var graph = await SeedGraph();
        var second = TestData.PlayerApplication(graph.Msel.Id, "second");
        var third = TestData.PlayerApplication(graph.Msel.Id, "third");
        await Seed(second, third);
        var secondRow = TestData.PlayerApplicationTeam(second.Id, graph.Team.Id, 2);
        var thirdRow = TestData.PlayerApplicationTeam(third.Id, graph.Team.Id, 3);
        await Seed(secondRow, thirdRow);

        var response = await Client(actor).PutAsJsonAsync(
            $"api/teamplayerApplications/{thirdRow.Id}", new { displayOrder = 1 }, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var answered = await response.Content.ReadFromJsonAsync<ViewModels.PlayerApplicationTeam>(JsonOptions, Ct);
        Assert.Equal(thirdRow.Id, answered.Id);
        Assert.Equal(1, answered.DisplayOrder);
        var order = await ReadBack(db => db.PlayerApplicationTeams
            .Where(x => x.TeamId == graph.Team.Id)
            .OrderBy(x => x.DisplayOrder)
            .Select(x => new { x.Id, x.DisplayOrder })
            .ToListAsync(Ct));
        Assert.Equal([thirdRow.Id, graph.Row.Id, secondRow.Id], order.Select(x => x.Id));
        Assert.Equal([1, 2, 3], order.Select(x => x.DisplayOrder));
    }

    /// <summary>
    /// The update first shipped at <c>playerApplicationteams/{id}</c>, the one route the other operations
    /// do not share; clients generated from that document still use it, so it still updates the row, and
    /// it says it is deprecated and which route replaces it.
    /// </summary>
    [Fact]
    public async Task Update_AtTheFormerRoute_StillUpdatesTheRowAndNamesItsSuccessor()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();
        var graph = await SeedGraph();
        var second = TestData.PlayerApplication(graph.Msel.Id, "second");
        await Seed(second);
        await Seed(TestData.PlayerApplicationTeam(second.Id, graph.Team.Id, 2));

        var response = await Client(actor).PutAsJsonAsync(
            $"api/playerApplicationteams/{graph.Row.Id}", new { displayOrder = 2 }, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, (await ReadBack(db => db.PlayerApplicationTeams.SingleAsync(x => x.Id == graph.Row.Id, Ct))).DisplayOrder);
        Assert.Equal(["@1795392000"], response.Headers.GetValues("Deprecation"));
        Assert.Equal(
            [$"</api/teamplayerApplications/{graph.Row.Id}>; rel=\"successor-version\""],
            response.Headers.GetValues("Link"));
    }

    /// <summary>The shared route is the current one, so it carries no deprecation.</summary>
    [Fact]
    public async Task Update_AtTheSharedRoute_IsNotMarkedDeprecated()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();
        var graph = await SeedGraph();

        var response = await Client(actor).PutAsJsonAsync(
            $"api/teamplayerApplications/{graph.Row.Id}", new { displayOrder = 1 }, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(response.Headers.Contains("Deprecation"));
    }

    /// <summary>
    /// Moves of different applications in one team that arrive together are applied one after another,
    /// so the team still ends with each place in its order held once.
    /// </summary>
    [Fact]
    public async Task Update_ConcurrentMovesInOneTeam_LeaveEachPlaceHeldOnce()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();
        var graph = await SeedGraph();
        var rows = new List<PlayerApplicationTeamEntity> { graph.Row };
        for (var order = 2; order <= 6; order++)
        {
            var application = TestData.PlayerApplication(graph.Msel.Id, $"app {order}");
            await Seed(application);
            var row = TestData.PlayerApplicationTeam(application.Id, graph.Team.Id, order);
            await Seed(row);
            rows.Add(row);
        }
        var client = Client(actor);

        // each row is moved to the front, or the back, at the same time as the others
        var responses = await Task.WhenAll(rows.Select((row, index) => client.PutAsJsonAsync(
            $"api/teamplayerApplications/{row.Id}", new { displayOrder = index % 2 == 0 ? 1 : rows.Count }, Ct)));

        Assert.All(responses, response => Assert.Equal(HttpStatusCode.OK, response.StatusCode));
        var orders = await ReadBack(db => db.PlayerApplicationTeams
            .Where(x => x.TeamId == graph.Team.Id)
            .Select(x => x.DisplayOrder)
            .ToListAsync(Ct));
        Assert.Equal(Enumerable.Range(1, rows.Count), orders.Order());
    }

    /// <summary>
    /// The API document names the update at the route the other operations share, once (routes are lowercased), so a generated
    /// client or a script reading the document is not sent to the former route.
    /// </summary>
    [Fact]
    public async Task TheApiDocument_PublishesTheUpdateAtTheSharedRouteOnly()
    {
        var response = await Client().GetAsync("/swagger/v1/swagger.json", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        var paths = document.RootElement.GetProperty("paths");
        Assert.True(paths.GetProperty("/api/teamplayerapplications/{id}").TryGetProperty("put", out var put));
        Assert.Equal("updatePlayerApplicationTeam", put.GetProperty("operationId").GetString());
        Assert.False(paths.TryGetProperty("/api/playerapplicationteams/{id}", out _));
    }

    /// <summary>
    /// The edit request carries only the display order, so a body naming another application or team
    /// leaves the row's pairing alone.
    /// </summary>
    [Fact]
    public async Task Update_WithEditMsels_Is200AndLeavesThePairingAlone()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();
        var graph = await SeedGraph();
        var elsewhere = TestData.Msel();
        await Seed(elsewhere);
        var other = TestData.PlayerApplication(elsewhere.Id, "other");
        var otherTeam = TestData.Team(elsewhere.Id);
        await Seed(other, otherTeam);

        var response = await Client(actor).PutAsJsonAsync(
            $"api/teamplayerApplications/{graph.Row.Id}",
            Body(other.Id, otherTeam.Id) with { id = graph.Row.Id, displayOrder = 1 },
            Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var stored = await ReadBack(db => db.PlayerApplicationTeams.SingleAsync(x => x.Id == graph.Row.Id, Ct));
        Assert.Equal(graph.Application.Id, stored.PlayerApplicationId);
        Assert.Equal(graph.Team.Id, stored.TeamId);
    }

    /// <summary>
    /// The display order is checked against the stored team's other applications, not a team the body
    /// names: here the stored team has one other row, so 2 is in range whatever the body says.
    /// </summary>
    [Fact]
    public async Task Update_ChecksTheDisplayOrderAgainstTheStoredTeam()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();
        var graph = await SeedGraph();
        var second = TestData.PlayerApplication(graph.Msel.Id, "second");
        await Seed(second);
        await Seed(TestData.PlayerApplicationTeam(second.Id, graph.Team.Id, 2));
        var emptyTeam = TestData.Team(graph.Msel.Id);
        await Seed(emptyTeam);

        var response = await Client(actor).PutAsJsonAsync(
            $"api/teamplayerApplications/{graph.Row.Id}",
            Body(graph.Application.Id, emptyTeam.Id) with { id = graph.Row.Id, displayOrder = 2 },
            Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, (await ReadBack(db => db.PlayerApplicationTeams.SingleAsync(x => x.Id == graph.Row.Id, Ct))).DisplayOrder);
    }

    /// <summary>
    /// A display order outside the team's order - missing, so 0, or past the end - is the caller's mistake,
    /// so it is answered with a 400 naming the valid range, and the row is left where it was.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public async Task Update_WithADisplayOrderOutOfRange_Is400(int displayOrder)
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();
        var graph = await SeedGraph();
        var second = TestData.PlayerApplication(graph.Msel.Id, "second");
        await Seed(second);
        await Seed(TestData.PlayerApplicationTeam(second.Id, graph.Team.Id, 2));

        var response = await Client(actor).PutAsJsonAsync(
            $"api/teamplayerApplications/{graph.Row.Id}",
            Body(graph.Application.Id, graph.Team.Id) with { id = graph.Row.Id, displayOrder = displayOrder },
            Ct);

        Assert.Equal(
            "The requested display order is not valid. It must be from 1 to 2.",
            (await AssertJsonError<Blueprint.Api.ViewModels.ApiError>(HttpStatusCode.BadRequest, response)).Title);
        Assert.Equal(1, (await ReadBack(db => db.PlayerApplicationTeams.SingleAsync(x => x.Id == graph.Row.Id, Ct))).DisplayOrder);
    }

    [Fact]
    public async Task Update_ForAnIdThatIsNotThere_Is404()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();
        var graph = await SeedGraph();
        var id = Guid.NewGuid();

        var response = await Client(actor).PutAsJsonAsync(
            $"api/teamplayerApplications/{id}",
            Body(graph.Application.Id, graph.Team.Id) with { id = id, displayOrder = 1 },
            Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ---------------------------------------------------------------------------------------------
    // DELETE teamplayerApplications/{id}
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Delete_WithEditMsels_Is204()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();
        var graph = await SeedGraph();

        var response = await Client(actor)
            .DeleteAsync($"api/teamplayerApplications/{graph.Row.Id}", Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Empty(await ReadBack(rb => rb.PlayerApplicationTeams.ToListAsync(Ct)));
    }

    /// <summary>Deleting an application from the middle of a team's order closes the gap it leaves.</summary>
    [Fact]
    public async Task Delete_FromTheMiddle_ClosesTheGap()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();
        var graph = await SeedGraph();
        var second = TestData.PlayerApplication(graph.Msel.Id, "second");
        var third = TestData.PlayerApplication(graph.Msel.Id, "third");
        await Seed(second, third);
        var secondRow = TestData.PlayerApplicationTeam(second.Id, graph.Team.Id, 2);
        var thirdRow = TestData.PlayerApplicationTeam(third.Id, graph.Team.Id, 3);
        await Seed(secondRow, thirdRow);

        var response = await Client(actor).DeleteAsync($"api/teamplayerApplications/{secondRow.Id}", Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var order = await ReadBack(db => db.PlayerApplicationTeams
            .Where(x => x.TeamId == graph.Team.Id)
            .OrderBy(x => x.DisplayOrder)
            .Select(x => new { x.Id, x.DisplayOrder })
            .ToListAsync(Ct));
        Assert.Equal([graph.Row.Id, thirdRow.Id], order.Select(x => x.Id));
        Assert.Equal([1, 2], order.Select(x => x.DisplayOrder));
    }

    [Fact]
    public async Task Delete_ForAnIdThatIsNotThere_Is404()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();

        var response = await Client(actor)
            .DeleteAsync($"api/teamplayerApplications/{Guid.NewGuid()}", Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ---------------------------------------------------------------------------------------------
    // DELETE teams/{teamId}/playerApplications/{playerApplicationId}
    // ---------------------------------------------------------------------------------------------

    /// <summary>Delete by ids with the ids in route order deletes the row.</summary>
    [Fact]
    public async Task DeleteByIds_InRouteOrder_DeletesTheRow()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();
        var graph = await SeedGraph();

        var response = await Client(actor).DeleteAsync(
            $"api/teams/{graph.Team.Id}/playerApplications/{graph.Application.Id}", Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Empty(await ReadBack(rb => rb.PlayerApplicationTeams.ToListAsync(Ct)));
    }

    [Fact]
    public async Task DeleteByIds_WithTheIdsSwapped_Is404AndKeepsTheRow()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();
        var graph = await SeedGraph();

        var response = await Client(actor).DeleteAsync(
            $"api/teams/{graph.Application.Id}/playerApplications/{graph.Team.Id}", Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Single(await ReadBack(rb => rb.PlayerApplicationTeams.ToListAsync(Ct)));
    }

    private async Task<(MselEntity Msel, TeamEntity Team, PlayerApplicationEntity Application,
        PlayerApplicationTeamEntity Row)> SeedGraph()
    {
        var msel = TestData.Msel();
        await Seed(msel);
        var team = TestData.Team(msel.Id);
        var application = TestData.PlayerApplication(msel.Id);
        await Seed(team, application);
        var row = TestData.PlayerApplicationTeam(application.Id, team.Id, 1);
        await Seed(row);

        return (msel, team, application, row);
    }

    /// <remarks>
    /// <c>ViewModels.PlayerApplicationTeam</c> does not derive from <c>Base</c> - the entity has no audit
    /// columns either, so nothing records who showed an application to a team or when - which is why this
    /// record declares no audit fields rather than omitting them deliberately as the sibling files do.
    /// </remarks>
    private static PlayerApplicationTeamBody Body(Guid playerApplicationId, Guid teamId) =>
        new() { playerApplicationId = playerApplicationId, teamId = teamId };

    private record PlayerApplicationTeamBody
    {
        public Guid id { get; init; }
        public Guid playerApplicationId { get; init; }
        public Guid teamId { get; init; }
        public int displayOrder { get; init; }
    }
}
