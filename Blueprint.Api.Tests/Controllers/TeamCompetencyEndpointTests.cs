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

/// <summary><c>TeamCompetencyService</c> / <c>TeamCompetencyController</c> - the six routes over the
/// competencies a MSEL's individual teams are assessed against, one level down from <see
/// cref="MselCompetencyEndpointTests"/>. Reads want <c>ViewMsels</c> or a view of the MSEL; writes want
/// <c>ManageMsels</c> or the MSEL's <c>Owner</c> role.</summary>
public class TeamCompetencyEndpointTests(DatabaseFixture fixture, BlueprintAppFactory factory)
    : ApiTestBase(fixture, factory), IClassFixture<BlueprintAppFactory>
{
    // ---------------------------------------------------------------------------------------------
    // GET teams/{teamId}/teamcompetencies
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task GetByTeam_ForAViewerOfTheMsel_ReturnsOnlyThatTeamsCompetencies()
    {
        var graph = await SeedGraph();
        var actor = await Actor().OnMsel(graph.Msel, MselRole.Viewer).SeedAsync();
        var otherTeam = TestData.Team(graph.Msel.Id);
        await Seed(otherTeam);
        await Seed(TestData.TeamCompetency(otherTeam.Id, graph.Competency.Id));

        var response = await Client(actor)
            .GetAsync($"api/teams/{graph.Team.Id}/teamcompetencies", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var list = await response.Content
            .ReadFromJsonAsync<List<ViewModels.TeamCompetency>>(JsonOptions, Ct);
        Assert.Single(list);
        Assert.Equal(graph.Row.Id, list[0].Id);
        Assert.Equal(graph.Team.Id, list[0].TeamId);
    }

    [Fact]
    public async Task GetByTeam_ForATeamThatIsNotThere_Is404()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewMsels).SeedAsync();

        var response = await Client(actor)
            .GetAsync($"api/teams/{Guid.NewGuid()}/teamcompetencies", Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ApiErrorBody>(JsonOptions, Ct);
        Assert.Equal("Team Entity not found", error.title);
    }

    // ---------------------------------------------------------------------------------------------
    // GET msels/{mselId}/teamcompetencies
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task GetByMsel_ForAViewerOfTheMsel_ReturnsEveryTeamsRowsAndNoOtherMsels()
    {
        var graph = await SeedGraph();
        var actor = await Actor().OnMsel(graph.Msel, MselRole.Viewer).SeedAsync();
        var secondTeam = TestData.Team(graph.Msel.Id);
        await Seed(secondTeam);
        await Seed(TestData.TeamCompetency(secondTeam.Id, graph.Competency.Id));
        var otherGraph = await SeedGraph();

        var response = await Client(actor)
            .GetAsync($"api/msels/{graph.Msel.Id}/teamcompetencies", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var list = await response.Content
            .ReadFromJsonAsync<List<ViewModels.TeamCompetency>>(JsonOptions, Ct);
        Assert.Equal(2, list.Count);
        Assert.Contains(graph.Team.Id, list.Select(x => x.TeamId));
        Assert.Contains(secondTeam.Id, list.Select(x => x.TeamId));
        Assert.DoesNotContain(otherGraph.Row.Id, list.Select(x => x.Id));
    }

    [Fact]
    public async Task GetByMsel_ForAMselThatIsNotThere_Is404()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewMsels).SeedAsync();

        var response = await Client(actor)
            .GetAsync($"api/msels/{Guid.NewGuid()}/teamcompetencies", Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ApiErrorBody>(JsonOptions, Ct);
        Assert.Equal("Msel Entity not found", error.title);
    }

    // ---------------------------------------------------------------------------------------------
    // GET teamcompetencies/{id}
    // ---------------------------------------------------------------------------------------------

    /// <summary>A viewer of the MSEL reads a team competency, with no related ID numbers resolved.</summary>
    [Fact]
    public async Task Get_ForAViewerOfTheMsel_Is200()
    {
        var graph = await SeedGraph();
        var actor = await Actor().OnMsel(graph.Msel, MselRole.Viewer).SeedAsync();

        var response = await Client(actor).GetAsync($"api/teamcompetencies/{graph.Row.Id}", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var answered = await response.Content
            .ReadFromJsonAsync<ViewModels.TeamCompetency>(JsonOptions, Ct);
        Assert.Equal(graph.Row.Id, answered.Id);
        Assert.Equal(graph.Team.Id, answered.TeamId);
        Assert.Equal(graph.Competency.IdNumber, answered.Competency.IdNumber);
        Assert.Empty(answered.Competency.RelatedIdNumbers);
    }

    /// <summary>An unknown team competency is a 404 naming <c>TeamCompetency</c>.</summary>
    [Fact]
    public async Task Get_ForAnIdThatIsNotThere_Is404_ThatNamesTheViewModel()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewMsels).SeedAsync();

        var response = await Client(actor).GetAsync($"api/teamcompetencies/{Guid.NewGuid()}", Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ApiErrorBody>(JsonOptions, Ct);
        Assert.Equal("Team Competency not found", error.title);
    }

    // ---------------------------------------------------------------------------------------------
    // POST teamcompetencies
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Create_ForAnOwnerOfTheMsel_Is201()
    {
        var graph = await SeedGraph();
        var actor = await Actor().OnMsel(graph.Msel, MselRole.Owner).SeedAsync();
        var competency = TestData.Competency(graph.Framework.Id);
        await Seed(competency);

        var response = await Client(actor).PostAsJsonAsync(
            "api/teamcompetencies", Body(graph.Team.Id, competency.Id), Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content
            .ReadFromJsonAsync<ViewModels.TeamCompetency>(JsonOptions, Ct);
        Assert.Equal(competency.Id, created.CompetencyId);
        Assert.EndsWith($"/api/teamcompetencies/{created.Id}", response.Headers.Location.ToString());

        Assert.Equal(2, await ReadBack(rb => rb.TeamCompetencies
            .CountAsync(x => x.TeamId == graph.Team.Id, Ct)));
    }

    [Theory]
    [InlineData("team")]
    [InlineData("competency")]
    public async Task Create_ForAParentThatIsNotThere_Is404(string missing)
    {
        var graph = await SeedGraph();
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageMsels).SeedAsync();
        var body = missing == "team"
            ? Body(Guid.NewGuid(), graph.Competency.Id)
            : Body(graph.Team.Id, Guid.NewGuid());

        var response = await Client(actor).PostAsJsonAsync("api/teamcompetencies", body, Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ApiErrorBody>(JsonOptions, Ct);
        Assert.Equal(
            missing == "team" ? "Team Entity not found" : "Competency Entity not found", error.title);
    }

    /// <summary>Create for a pair that is already there is answered with a 500.</summary>
    [Fact]
    public async Task Create_ForAPairThatIsAlreadyThere_Is500()
    {
        var graph = await SeedGraph();
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageMsels).SeedAsync();

        var response = await Client(actor).PostAsJsonAsync(
            "api/teamcompetencies", Body(graph.Team.Id, graph.Competency.Id), Ct);

        Assert.Equal("Team Competency already exists.", (await AssertJsonError<Blueprint.Api.ViewModels.ApiError>(HttpStatusCode.InternalServerError, response)).Title);
        Assert.Single(await ReadBack(rb => rb.TeamCompetencies.ToListAsync(Ct)));
    }

    // ---------------------------------------------------------------------------------------------
    // DELETE teamcompetencies/{id}
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Delete_ForAnOwnerOfTheMsel_Is204()
    {
        var graph = await SeedGraph();
        var actor = await Actor().OnMsel(graph.Msel, MselRole.Owner).SeedAsync();

        var response = await Client(actor).DeleteAsync($"api/teamcompetencies/{graph.Row.Id}", Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Empty(await ReadBack(rb => rb.TeamCompetencies.ToListAsync(Ct)));
        Assert.Single(await ReadBack(rb => rb.Competencies.ToListAsync(Ct)));
    }

    [Fact]
    public async Task Delete_ForAnIdThatIsNotThere_Is404()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageMsels).SeedAsync();

        var response = await Client(actor).DeleteAsync($"api/teamcompetencies/{Guid.NewGuid()}", Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ---------------------------------------------------------------------------------------------
    // DELETE teams/{teamId}/competencies/{competencyId}
    // ---------------------------------------------------------------------------------------------

    /// <summary>The route passes its ids in the declared order and deletes the row they name.</summary>
    [Fact]
    public async Task DeleteByIds_ForAnOwnerOfTheMsel_Is204()
    {
        var graph = await SeedGraph();
        var actor = await Actor().OnMsel(graph.Msel, MselRole.Owner).SeedAsync();

        var response = await Client(actor).DeleteAsync(
            $"api/teams/{graph.Team.Id}/competencies/{graph.Competency.Id}", Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Empty(await ReadBack(rb => rb.TeamCompetencies.ToListAsync(Ct)));
    }

    [Fact]
    public async Task DeleteByIds_ForAPairThatIsNotThere_Is404()
    {
        var graph = await SeedGraph();
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageMsels).SeedAsync();

        var response = await Client(actor).DeleteAsync(
            $"api/teams/{graph.Team.Id}/competencies/{Guid.NewGuid()}", Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Single(await ReadBack(rb => rb.TeamCompetencies.ToListAsync(Ct)));
    }

    // ---------------------------------------------------------------------------------------------
    // Near-miss denials
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task GetByTeam_is_forbidden_for_a_caller_holding_Owner_only_in_another_msel()
    {
        var graph = await SeedGraph();
        var actor = await Actor().OnNewMsel(MselRole.Owner).SeedAsync();

        var response = await Client(actor).GetAsync($"api/teams/{graph.Team.Id}/teamcompetencies", Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Get_is_forbidden_for_a_caller_holding_Owner_only_in_another_msel()
    {
        var graph = await SeedGraph();
        var actor = await Actor().OnNewMsel(MselRole.Owner).SeedAsync();

        var response = await Client(actor).GetAsync($"api/teamcompetencies/{graph.Row.Id}", Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Create_is_forbidden_for_an_editor_of_the_msel()
    {
        var graph = await SeedGraph();
        var actor = await Actor().OnMsel(graph.Msel, MselRole.Editor).SeedAsync();
        var competency = TestData.Competency(graph.Framework.Id);
        await Seed(competency);

        var response = await Client(actor).PostAsJsonAsync(
            "api/teamcompetencies", Body(graph.Team.Id, competency.Id), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(1, await ReadBack(rb => rb.TeamCompetencies.CountAsync(x => x.TeamId == graph.Team.Id, Ct)));
    }

    [Fact]
    public async Task Delete_is_forbidden_for_an_editor_of_the_msel()
    {
        var graph = await SeedGraph();
        var actor = await Actor().OnMsel(graph.Msel, MselRole.Editor).SeedAsync();

        var response = await Client(actor).DeleteAsync($"api/teamcompetencies/{graph.Row.Id}", Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(1, await ReadBack(rb => rb.TeamCompetencies.CountAsync(x => x.Id == graph.Row.Id, Ct)));
    }

    [Fact]
    public async Task DeleteByIds_is_forbidden_for_a_caller_holding_Owner_only_in_another_msel()
    {
        var graph = await SeedGraph();
        var actor = await Actor().OnNewMsel(MselRole.Owner).SeedAsync();

        var response = await Client(actor).DeleteAsync(
            $"api/teams/{graph.Team.Id}/competencies/{graph.Competency.Id}", Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(1, await ReadBack(rb => rb.TeamCompetencies.CountAsync(x => x.Id == graph.Row.Id, Ct)));
    }

    private async Task<(MselEntity Msel, TeamEntity Team, CompetencyFrameworkEntity Framework,
        CompetencyEntity Competency, TeamCompetencyEntity Row)> SeedGraph()
    {
        var msel = TestData.Msel();
        var framework = TestData.CompetencyFramework();
        await Seed(msel, framework);
        var team = TestData.Team(msel.Id);
        var competency = TestData.Competency(framework.Id);
        await Seed(team, competency);
        var row = TestData.TeamCompetency(team.Id, competency.Id);
        await Seed(row);

        return (msel, team, framework, competency, row);
    }

    /// <remarks>
    /// Neither <c>ViewModels.TeamCompetency</c> nor <c>TeamCompetencyEntity</c> carries an audit field, so
    /// nothing records who decided what a team would be assessed against - the service writes one
    /// <c>LogWarning</c> per write instead, which is the audit trail and is filed a level above where it
    /// belongs.
    /// </remarks>
    private static TeamCompetencyBody Body(Guid teamId, Guid competencyId) =>
        new() { teamId = teamId, competencyId = competencyId };

    private record TeamCompetencyBody
    {
        public Guid id { get; init; }
        public Guid teamId { get; init; }
        public Guid competencyId { get; init; }
    }

    /// <remarks>
    /// The shape <c>JsonExceptionFilter</c> answers with - <c>ViewModels.ApiError</c>, read here as a
    /// record so a test can assert which entity a 404 names.
    /// </remarks>
    private record ApiErrorBody
    {
        public int status { get; init; }
        public string title { get; init; }
        public string detail { get; init; }
    }
}
