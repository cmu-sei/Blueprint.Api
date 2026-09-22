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
using Blueprint.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Blueprint.Api.Tests;

/// <summary>
/// <c>TeamCompetencyService</c> / <c>TeamCompetencyController</c> - the six routes over the competencies a
/// MSEL's individual teams are assessed against, one level down from
/// <see cref="MselCompetencyEndpointTests"/>. Reads want <c>ViewMsels</c> or a view of the MSEL; writes
/// want <c>ManageMsels</c> or the MSEL's <c>Owner</c> role.
/// </summary>
/// <remarks>
/// 401 and 403 for these routes live in <see cref="RouteAuthorizationTests"/>; this file covers the happy
/// path and the not-found path per route, per the thin protocol.
/// <para />
/// The positive control of this commit, and the reason it is worth a file of its own: all six methods read
/// the stored parent before they decide anything, so not one of the three shapes that opened every other
/// file in this tier is present - no permission decided from a request body (there is no update method to
/// decide one), no <c>SingleAsync</c> plus a dead null check, and both deletes check existence first, so an
/// unknown id is a clean 404 for every caller rather than a status that depends on their permission. What
/// is left to characterize is small: a duplicate pair, the level a write is audited at, and which name a
/// 404 carries.
/// </remarks>
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
        var otherTeam = BlueprintAppFactory.Team(graph.Msel.Id);
        await Seed(otherTeam);
        await Seed(BlueprintAppFactory.TeamCompetency(otherTeam.Id, graph.Competency.Id));

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
        var secondTeam = BlueprintAppFactory.Team(graph.Msel.Id);
        await Seed(secondTeam);
        await Seed(BlueprintAppFactory.TeamCompetency(secondTeam.Id, graph.Competency.Id));
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

    /// <remarks>
    /// The MSEL-level read resolves a competency's <c>RelatedIdNumbers</c> against the MSEL's own pool and
    /// this one resolves nothing at all, so the same competency answers a relationship list on
    /// <c>GET mselcompetencies/{id}</c> and an empty one here. Not a defect either way - but a client
    /// showing a team's competencies cannot draw the relationships it draws one level up, and nothing in
    /// the surface says so.
    /// </remarks>
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

    /// <remarks>
    /// BUG: a missing row is reported as <c>EntityNotFoundException&lt;TeamCompetency&gt;</c> - the
    /// <b>view model</b>, where the same service's two list routes name <c>TeamEntity</c> and
    /// <c>MselEntity</c> and its create names <c>CompetencyEntity</c>. So one service spells one condition
    /// two ways, and a client matching on the message cannot. Harmless in itself; it is the marker for the
    /// wider inconsistency, which <c>MselCompetencyService</c> and <c>MselPageService</c> share.
    /// </remarks>
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
        var competency = BlueprintAppFactory.Competency(graph.Framework.Id);
        await Seed(competency);

        var response = await Client(actor).PostAsJsonAsync(
            "api/teamcompetencies", Body(graph.Team.Id, competency.Id), Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content
            .ReadFromJsonAsync<ViewModels.TeamCompetency>(JsonOptions, Ct);
        Assert.Equal(competency.Id, created.CompetencyId);
        Assert.EndsWith($"/api/teamcompetencies/{created.Id}", response.Headers.Location.ToString());

        Assert.Equal(2, await NewContext().TeamCompetencies
            .CountAsync(x => x.TeamId == graph.Team.Id, Ct));
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

    /// <remarks>
    /// BUG: a pair that is already there is refused by a bare <c>ArgumentException</c>, which is not an
    /// <c>IApiException</c>, so the answer is a 500 where the case deserves a 409. <c>(TeamId,
    /// CompetencyId)</c> is uniquely indexed as well, so the guard is a courtesy rather than the
    /// protection - remove it and the answer is still a 500, from the index. Same shape as
    /// <c>MselCompetencyService</c> and <c>CatalogUnitService</c>.
    /// </remarks>
    [Fact]
    public async Task Create_ForAPairThatIsAlreadyThere_Is500()
    {
        var graph = await SeedGraph();
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageMsels).SeedAsync();

        var response = await Client(actor).PostAsJsonAsync(
            "api/teamcompetencies", Body(graph.Team.Id, graph.Competency.Id), Ct);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Single(await NewContext().TeamCompetencies.ToListAsync(Ct));
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
        Assert.Empty(await NewContext().TeamCompetencies.ToListAsync(Ct));
        Assert.Single(await NewContext().Competencies.ToListAsync(Ct));
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

    /// <remarks>
    /// The second positive control for the transposed-ids defect, after
    /// <see cref="MselCompetencyEndpointTests.DeleteByIds_ForAnOwnerOfTheMsel_Is204"/>:
    /// <c>TeamCompetencyController.cs:124</c> calls <c>DeleteByIdsAsync(teamId, competencyId, …)</c>
    /// against a matching signature, where <c>CardTeamController.cs:192</c> and
    /// <c>PlayerApplicationTeamController.cs:192</c> transpose the same two <c>Guid</c>s and delete
    /// nothing.
    /// </remarks>
    [Fact]
    public async Task DeleteByIds_ForAnOwnerOfTheMsel_Is204()
    {
        var graph = await SeedGraph();
        var actor = await Actor().OnMsel(graph.Msel, MselRole.Owner).SeedAsync();

        var response = await Client(actor).DeleteAsync(
            $"api/teams/{graph.Team.Id}/competencies/{graph.Competency.Id}", Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Empty(await NewContext().TeamCompetencies.ToListAsync(Ct));
    }

    [Fact]
    public async Task DeleteByIds_ForAPairThatIsNotThere_Is404()
    {
        var graph = await SeedGraph();
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageMsels).SeedAsync();

        var response = await Client(actor).DeleteAsync(
            $"api/teams/{graph.Team.Id}/competencies/{Guid.NewGuid()}", Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Single(await NewContext().TeamCompetencies.ToListAsync(Ct));
    }

    private async Task<(MselEntity Msel, TeamEntity Team, CompetencyFrameworkEntity Framework,
        CompetencyEntity Competency, TeamCompetencyEntity Row)> SeedGraph()
    {
        var msel = BlueprintAppFactory.Msel();
        var framework = BlueprintAppFactory.CompetencyFramework();
        await Seed(msel, framework);
        var team = BlueprintAppFactory.Team(msel.Id);
        var competency = BlueprintAppFactory.Competency(framework.Id);
        await Seed(team, competency);
        var row = BlueprintAppFactory.TeamCompetency(team.Id, competency.Id);
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
