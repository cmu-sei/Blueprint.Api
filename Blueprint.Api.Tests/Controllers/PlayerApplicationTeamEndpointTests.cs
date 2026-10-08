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

    // ---------------------------------------------------------------------------------------------
    // PUT playerApplicationteams/{id}
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Update_WithEditMsels_Is200()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();
        var graph = await SeedGraph();
        var other = TestData.PlayerApplication(graph.Msel.Id, "other");
        await Seed(other);

        var response = await Client(actor).PutAsJsonAsync(
            $"api/playerApplicationteams/{graph.Row.Id}",
            Body(other.Id, graph.Team.Id) with { id = graph.Row.Id, displayOrder = 1 },
            Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var stored = await ReadBack(rb => rb.PlayerApplicationTeams
            .SingleAsync(x => x.Id == graph.Row.Id, Ct));
        Assert.Equal(other.Id, stored.PlayerApplicationId);
    }

    /// <summary>Update without a display order is answered with a 500.</summary>
    [Fact]
    public async Task Update_WithoutADisplayOrder_Is500()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();
        var graph = await SeedGraph();

        var response = await Client(actor).PutAsJsonAsync(
            $"api/playerApplicationteams/{graph.Row.Id}",
            Body(graph.Application.Id, graph.Team.Id) with { id = graph.Row.Id },
            Ct);

        Assert.Equal("The requested display order is not valid", (await AssertJsonError<Blueprint.Api.ViewModels.ApiError>(HttpStatusCode.InternalServerError, response)).Title);
    }

    [Fact]
    public async Task Update_ForAnIdThatIsNotThere_Is404()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();
        var graph = await SeedGraph();
        var id = Guid.NewGuid();

        var response = await Client(actor).PutAsJsonAsync(
            $"api/playerApplicationteams/{id}",
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

    /// <summary>Delete by ids with the ids in route order is answered with a 404 and keeps the row.</summary>
    [Fact]
    public async Task DeleteByIds_InRouteOrder_Is404AndKeepsTheRow()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();
        var graph = await SeedGraph();

        var response = await Client(actor).DeleteAsync(
            $"api/teams/{graph.Team.Id}/playerApplications/{graph.Application.Id}", Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Single(await ReadBack(rb => rb.PlayerApplicationTeams.ToListAsync(Ct)));
    }

    // Same case as DeleteByIds_InRouteOrder_Is404AndKeepsTheRow.
    [Fact]
    public async Task DeleteByIds_WithTheIdsSwapped_RemovesTheRow()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();
        var graph = await SeedGraph();

        var response = await Client(actor).DeleteAsync(
            $"api/teams/{graph.Application.Id}/playerApplications/{graph.Team.Id}", Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Empty(await ReadBack(rb => rb.PlayerApplicationTeams.ToListAsync(Ct)));
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
