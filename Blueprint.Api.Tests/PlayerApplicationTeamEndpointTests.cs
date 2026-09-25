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
/// <c>PlayerApplicationTeamService</c> / <c>PlayerApplicationTeamController</c> - the eight routes over the
/// rows deciding which of a MSEL's teams sees which of its Player applications. Reads want
/// <c>ViewMsels</c> or a view of the MSEL; every write wants <c>EditMsels</c> and nothing else.
/// </summary>
/// <remarks>
/// 401 and 403 for these routes live in <see cref="RouteAuthorizationTests"/>; this file covers the happy
/// path and the not-found path per route, per the thin protocol.
/// <para />
/// Not one of the four write methods takes a permission argument, so no MSEL role reaches them: a MSEL's
/// own owner cannot say which of their teams sees an application, and an <c>EditMsels</c> holder with no
/// role anywhere may point any team at any application. Both halves are asserted in
/// <see cref="NoWriteConsultsAMselRole_SoAnOwnerIsRefusedAndAStrangerIsNot"/>. The same shape one
/// concept over is <see cref="CardTeamEndpointTests"/>, and the comparison is the finding rather than
/// either file alone.
/// </remarks>
public class PlayerApplicationTeamEndpointTests(DatabaseFixture fixture, BlueprintAppFactory factory)
    : ApiTestBase(fixture, factory), IClassFixture<BlueprintAppFactory>
{
    // ---------------------------------------------------------------------------------------------
    // GET teamplayerApplications
    // ---------------------------------------------------------------------------------------------

    /// <remarks>
    /// BUG: <c>GetAsync(ct)</c> has no filter at all, so one <c>ViewMsels</c> holder reads every
    /// application-team row in the installation, across every MSEL. Same shape as
    /// <c>GET teamcards</c>.
    /// </remarks>
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

    /// <remarks>
    /// BUG: the template fall-through dereferences an unguarded, token-less <c>FindAsync</c>
    /// (<c>PlayerApplicationTeamService.cs:91-92</c>), so the status an unknown MSEL gets depends on the
    /// caller's permission - the <c>&amp;&amp;</c> short-circuits for a <c>ViewMsels</c> holder and does
    /// not for anybody else. Three of this service's four reads share the shape.
    /// </remarks>
    [Fact]
    public async Task GetByMsel_ForAMselThatIsNotThere_Is500_Or200ForAViewMselsHolder()
    {
        var stranger = await Actor().SeedAsync();
        var privileged = await Actor().WithSystemPermissions(SystemPermission.ViewMsels).SeedAsync();
        var id = Guid.NewGuid();

        var refused = await Client(stranger).GetAsync($"api/msels/{id}/teamplayerApplications", Ct);
        Assert.Equal(HttpStatusCode.InternalServerError, refused.StatusCode);

        var answered = await Client(privileged).GetAsync($"api/msels/{id}/teamplayerApplications", Ct);
        Assert.Equal(HttpStatusCode.OK, answered.StatusCode);
        Assert.Empty(await answered.Content
            .ReadFromJsonAsync<List<ViewModels.PlayerApplicationTeam>>(JsonOptions, Ct));
    }

    // ---------------------------------------------------------------------------------------------
    // GET playerApplications/{playerApplicationId}/teamplayerApplications
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task GetByPlayerApplication_ForAViewerOfTheMsel_ReturnsOnlyThatApplicationsRows()
    {
        var graph = await SeedGraph();
        var actor = await Actor().OnMsel(graph.Msel, MselRole.Viewer).SeedAsync();
        var other = BlueprintAppFactory.PlayerApplication(graph.Msel.Id, "other");
        await Seed(other);
        await Seed(BlueprintAppFactory.PlayerApplicationTeam(other.Id, graph.Team.Id, 2));

        var response = await Client(actor).GetAsync(
            $"api/playerApplications/{graph.Application.Id}/teamplayerApplications", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var list = await response.Content
            .ReadFromJsonAsync<List<ViewModels.PlayerApplicationTeam>>(JsonOptions, Ct);
        Assert.Single(list);
        Assert.Equal(graph.Row.Id, list[0].Id);
    }

    /// <remarks>
    /// BUG: the application is fetched with <c>FirstOrDefaultAsync</c> and dereferenced on the next line
    /// (<c>PlayerApplicationTeamService.cs:73-74</c>), so an unknown application is a 500 for an ordinary
    /// caller and an empty 200 for a <c>ViewMsels</c> holder.
    /// </remarks>
    [Fact]
    public async Task GetByPlayerApplication_ForAnIdThatIsNotThere_Is500_Or200ForAViewMselsHolder()
    {
        var stranger = await Actor().SeedAsync();
        var privileged = await Actor().WithSystemPermissions(SystemPermission.ViewMsels).SeedAsync();
        var url = $"api/playerApplications/{Guid.NewGuid()}/teamplayerApplications";

        var refused = await Client(stranger).GetAsync(url, Ct);
        Assert.Equal(HttpStatusCode.InternalServerError, refused.StatusCode);

        var answered = await Client(privileged).GetAsync(url, Ct);
        Assert.Equal(HttpStatusCode.OK, answered.StatusCode);
        Assert.Empty(await answered.Content
            .ReadFromJsonAsync<List<ViewModels.PlayerApplicationTeam>>(JsonOptions, Ct));
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

    /// <remarks>
    /// BUG: <c>GetAsync</c> dereferences <c>item.PlayerApplication.MselId</c> on the line after its own
    /// <c>SingleOrDefaultAsync</c> (<c>PlayerApplicationTeamService.cs:58-61</c>), to the right of
    /// <c>!hasSystemPermission &amp;&amp;</c> - so the controller's null check at <c>:106</c> is reachable
    /// only once the service's dereference has been skipped, and an unknown id is a 404 for a
    /// <c>ViewMsels</c> holder and a 500 for everybody else.
    /// </remarks>
    [Fact]
    public async Task Get_ForAnIdThatIsNotThere_Is404WithViewMselsAnd500ForEverybodyElse()
    {
        var stranger = await Actor().SeedAsync();
        var privileged = await Actor().WithSystemPermissions(SystemPermission.ViewMsels).SeedAsync();
        var url = $"api/teamplayerApplications/{Guid.NewGuid()}";

        var refused = await Client(stranger).GetAsync(url, Ct);
        Assert.Equal(HttpStatusCode.InternalServerError, refused.StatusCode);

        var answered = await Client(privileged).GetAsync(url, Ct);
        Assert.Equal(HttpStatusCode.NotFound, answered.StatusCode);
    }

    // ---------------------------------------------------------------------------------------------
    // POST teamplayerApplications
    // ---------------------------------------------------------------------------------------------

    /// <remarks>
    /// BUG: the created row's <c>Location</c> header points at <c>teamplayerapplications/{id}</c> while
    /// the update route is spelled <c>playerApplicationteams/{id}</c> - the odd one out among eight
    /// siblings - so a client following the header to edit what it just created is answered 405.
    /// </remarks>
    [Fact]
    public async Task Create_WithEditMsels_Is201_AndClampsTheDisplayOrderIntoRange()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();
        var msel = BlueprintAppFactory.Msel();
        await Seed(msel);
        var team = BlueprintAppFactory.Team(msel.Id);
        var application = BlueprintAppFactory.PlayerApplication(msel.Id);
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

        var stored = await NewContext().PlayerApplicationTeams
            .SingleAsync(x => x.Id == created.Id, Ct);
        Assert.Equal(team.Id, stored.TeamId);
        Assert.Equal(1, stored.DisplayOrder);
    }

    /// <remarks>
    /// BUG: no write method takes a permission argument, so no MSEL role reaches this service. The owner
    /// of the MSEL the application and the team both belong to is refused, and an <c>EditMsels</c> holder
    /// with no role anywhere is not - including against a MSEL they have never been near.
    /// </remarks>
    [Fact]
    public async Task NoWriteConsultsAMselRole_SoAnOwnerIsRefusedAndAStrangerIsNot()
    {
        var msel = BlueprintAppFactory.Msel();
        await Seed(msel);
        var team = BlueprintAppFactory.Team(msel.Id);
        var application = BlueprintAppFactory.PlayerApplication(msel.Id);
        await Seed(team, application);
        var owner = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();
        var stranger = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();

        var refused = await Client(owner).PostAsJsonAsync(
            "api/teamplayerApplications", Body(application.Id, team.Id), Ct);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);

        var allowed = await Client(stranger).PostAsJsonAsync(
            "api/teamplayerApplications", Body(application.Id, team.Id), Ct);
        Assert.Equal(HttpStatusCode.Created, allowed.StatusCode);
    }

    // ---------------------------------------------------------------------------------------------
    // PUT playerApplicationteams/{id}
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Update_WithEditMsels_Is200()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();
        var graph = await SeedGraph();
        var other = BlueprintAppFactory.PlayerApplication(graph.Msel.Id, "other");
        await Seed(other);

        var response = await Client(actor).PutAsJsonAsync(
            $"api/playerApplicationteams/{graph.Row.Id}",
            Body(other.Id, graph.Team.Id) with { id = graph.Row.Id, displayOrder = 1 },
            Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var stored = await NewContext().PlayerApplicationTeams
            .SingleAsync(x => x.Id == graph.Row.Id, Ct);
        Assert.Equal(other.Id, stored.PlayerApplicationId);
    }

    /// <remarks>
    /// BUG: <c>UpdateAsync</c> refuses a display order outside <c>1..count+1</c> with an
    /// <c>InvalidDataException</c>, which is not an <c>IApiException</c>, so the answer is a 500 rather
    /// than a 400. The default an omitted <c>displayOrder</c> binds to is <b>0</b>, so every PUT that does
    /// not name one trips it - and <c>CreateAsync</c>, twenty lines above, silently clamps the same value
    /// into range instead.
    /// </remarks>
    [Fact]
    public async Task Update_WithoutADisplayOrder_Is500()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();
        var graph = await SeedGraph();

        var response = await Client(actor).PutAsJsonAsync(
            $"api/playerApplicationteams/{graph.Row.Id}",
            Body(graph.Application.Id, graph.Team.Id) with { id = graph.Row.Id },
            Ct);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
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
        Assert.Empty(await NewContext().PlayerApplicationTeams.ToListAsync(Ct));
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

    /// <remarks>
    /// BUG: <c>PlayerApplicationTeamController.cs:192</c> calls
    /// <c>DeleteByIdsAsync(teamId, playerApplicationId, ct)</c> against a signature of
    /// <c>(Guid playerApplicationId, Guid teamId, …)</c>, so the ids arrive transposed and the
    /// <c>Where</c> matches nothing: the route is a 404 for every well-formed request and answers 204
    /// only when the caller passes the ids the wrong way round, which is what the second half of this
    /// test does. Both parameters are <c>Guid</c>, so nothing about it is a compile error. Second
    /// instance of the shape after <c>CardTeamController.cs:192</c>; the working copy is
    /// <c>TeamUserController.cs:152</c>.
    /// </remarks>
    [Fact]
    public async Task DeleteByIds_Is404ForAWellFormedRequest_AndDeletesTheRowWhenTheIdsAreSwapped()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();
        var graph = await SeedGraph();

        var wellFormed = await Client(actor).DeleteAsync(
            $"api/teams/{graph.Team.Id}/playerApplications/{graph.Application.Id}", Ct);
        Assert.Equal(HttpStatusCode.NotFound, wellFormed.StatusCode);
        Assert.Single(await NewContext().PlayerApplicationTeams.ToListAsync(Ct));

        var swapped = await Client(actor).DeleteAsync(
            $"api/teams/{graph.Application.Id}/playerApplications/{graph.Team.Id}", Ct);
        Assert.Equal(HttpStatusCode.NoContent, swapped.StatusCode);
        Assert.Empty(await NewContext().PlayerApplicationTeams.ToListAsync(Ct));
    }

    private async Task<(MselEntity Msel, TeamEntity Team, PlayerApplicationEntity Application,
        PlayerApplicationTeamEntity Row)> SeedGraph()
    {
        var msel = BlueprintAppFactory.Msel();
        await Seed(msel);
        var team = BlueprintAppFactory.Team(msel.Id);
        var application = BlueprintAppFactory.PlayerApplication(msel.Id);
        await Seed(team, application);
        var row = BlueprintAppFactory.PlayerApplicationTeam(application.Id, team.Id, 1);
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
