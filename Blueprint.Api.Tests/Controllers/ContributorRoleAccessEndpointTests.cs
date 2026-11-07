// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Blueprint.Api.Data.Enumerations;
using Blueprint.Api.Data.Models;
using Blueprint.Api.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Blueprint.Api.Tests.Controllers;

/// <summary>
/// What a member of a unit on a MSEL's Contributors tab may reach on the build side. Membership of the unit
/// is not enough: the member needs a role on the MSEL, Viewer or higher. Viewer reads and edits nothing,
/// removing the member's last role takes the MSEL away, the MSEL's creator always reaches it, and a member
/// without a role who is also on one of its teams is a participant, shown only the Exercise View.
/// </summary>
public class ContributorRoleAccessEndpointTests(DatabaseFixture fixture, BlueprintAppFactory factory)
    : ApiTestBase(fixture, factory), IClassFixture<BlueprintAppFactory>
{
    // ---------------------------------------------------------------------------------------------
    // a unit member holding no role
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task AUnitMemberWithNoRole_IsRefusedTheMsel()
    {
        var msel = TestData.Msel();
        await Seed(msel);
        var member = await Actor().SeedAsync();
        await Db.AddUnitMembershipAsync(member.Id, msel.Id, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, await StatusOf(member, $"/api/msels/{msel.Id}"));
    }

    [Theory]
    [InlineData("scenarioEvents")]
    [InlineData("dataFields")]
    [InlineData("moves")]
    [InlineData("teams")]
    [InlineData("mselunits")]
    public async Task AUnitMemberWithNoRole_IsRefusedTheBuildRoutes(string collection)
    {
        var msel = TestData.Msel();
        await Seed(msel);
        var member = await Actor().SeedAsync();
        await Db.AddUnitMembershipAsync(member.Id, msel.Id, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, await StatusOf(member, $"/api/msels/{msel.Id}/{collection}"));
    }

    [Fact]
    public async Task AUnitMemberWithNoRole_IsNotListed()
    {
        var msel = TestData.Msel();
        await Seed(msel);
        var member = await Actor().SeedAsync();
        await Db.AddUnitMembershipAsync(member.Id, msel.Id, Ct);

        Assert.Empty(await MyMsels(member));
    }

    // ---------------------------------------------------------------------------------------------
    // a Viewer
    // ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("")]
    [InlineData("/scenarioEvents")]
    [InlineData("/dataFields")]
    [InlineData("/moves")]
    [InlineData("/teams")]
    [InlineData("/mselunits")]
    public async Task AViewer_ReadsTheBuildRoutes(string collection)
    {
        var msel = TestData.Msel();
        await Seed(msel);
        var viewer = await Actor().OnMsel(msel, MselRole.Viewer).SeedAsync();

        Assert.Equal(HttpStatusCode.OK, await StatusOf(viewer, $"/api/msels/{msel.Id}{collection}"));
    }

    [Fact]
    public async Task AViewer_IsListed()
    {
        var msel = TestData.Msel();
        await Seed(msel, TestData.Msel());
        var viewer = await Actor().OnMsel(msel, MselRole.Viewer).SeedAsync();

        Assert.Equal(msel.Id, Assert.Single(await MyMsels(viewer)).Id);
    }

    [Fact]
    public async Task AViewer_SeesEveryEvent_HiddenOrNot()
    {
        var msel = TestData.Msel();
        await Seed(msel);
        var hidden = TestData.ScenarioEvent(msel.Id);
        hidden.IsHidden = true;
        await Seed(hidden, TestData.ScenarioEvent(msel.Id, deltaSeconds: 60));
        var viewer = await Actor().OnMsel(msel, MselRole.Viewer).SeedAsync();

        Assert.Equal(2, (await Events(viewer, msel.Id)).Count);
    }

    [Fact]
    public async Task AViewer_CannotEditTheMsel()
    {
        var msel = TestData.Msel();
        await Seed(msel);
        var viewer = await Actor().OnMsel(msel, MselRole.Viewer).SeedAsync();

        var rename = await Client(viewer).PutAsJsonAsync(
            $"/api/msels/{msel.Id}",
            new ViewModels.Msel { Id = msel.Id, Name = "Renamed", Status = msel.Status },
            JsonOptions, Ct);
        var grant = await Client(viewer).PutAsync(
            $"/api/msels/{msel.Id}/user/{viewer.Id}/role/{MselRole.Editor}/add", content: null, Ct);
        var move = await Client(viewer).PostAsJsonAsync(
            "/api/moves", new ViewModels.Move { MselId = msel.Id, MoveNumber = 1 }, JsonOptions, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, rename.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, grant.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, move.StatusCode);
        Assert.Equal(msel.Name, (await Db.Msels.AsNoTracking().SingleAsync(m => m.Id == msel.Id, Ct)).Name);
    }

    // ---------------------------------------------------------------------------------------------
    // removing the last role
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task RemovingAMembersLastRole_TakesTheMselAway()
    {
        var msel = TestData.Msel();
        await Seed(msel);
        var member = await Actor().OnMsel(msel, MselRole.Viewer).SeedAsync();
        var owner = await Actor().OnMsel(msel, MselRole.Owner, await UnitOf(member)).SeedAsync();

        var removed = await Client(owner).PutAsync(
            $"/api/msels/{msel.Id}/user/{member.Id}/role/{MselRole.Viewer}/remove", content: null, Ct);

        Assert.Equal(HttpStatusCode.OK, removed.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, await StatusOf(member, $"/api/msels/{msel.Id}"));
        Assert.Equal(HttpStatusCode.Forbidden, await StatusOf(member, $"/api/msels/{msel.Id}/scenarioEvents"));
        Assert.Empty(await MyMsels(member));
    }

    [Fact]
    public async Task RemovingOneOfTwoRoles_LeavesTheMselReadable()
    {
        var msel = TestData.Msel();
        await Seed(msel);
        var member = await Actor().OnMsel(msel, MselRole.Viewer).SeedAsync();
        Db.UserMselRoles.Add(new UserMselRoleEntity(member.Id, msel.Id, MselRole.Evaluator) { Id = Guid.NewGuid() });
        await Db.SaveChangesAsync(Ct);
        var owner = await Actor().OnMsel(msel, MselRole.Owner, await UnitOf(member)).SeedAsync();

        await Client(owner).PutAsync(
            $"/api/msels/{msel.Id}/user/{member.Id}/role/{MselRole.Viewer}/remove", content: null, Ct);

        Assert.Equal(HttpStatusCode.OK, await StatusOf(member, $"/api/msels/{msel.Id}"));
        Assert.Equal(msel.Id, Assert.Single(await MyMsels(member)).Id);
    }

    // ---------------------------------------------------------------------------------------------
    // the creator
    // ---------------------------------------------------------------------------------------------

    /// <summary>The creator is the implied Owner, with no role row and no unit.</summary>
    [Fact]
    public async Task TheCreator_ReachesTheMselWithNoRoleOrUnit()
    {
        var creatorId = Guid.NewGuid();
        var msel = TestData.Msel(createdBy: creatorId);
        await Seed(msel);
        var creator = await Actor().WithId(creatorId).SeedAsync();

        Assert.Equal(HttpStatusCode.OK, await StatusOf(creator, $"/api/msels/{msel.Id}"));
        Assert.Equal(HttpStatusCode.OK, await StatusOf(creator, $"/api/msels/{msel.Id}/moves"));
        Assert.Equal(msel.Id, Assert.Single(await MyMsels(creator)).Id);
    }

    /// <summary>A creator in one of the MSEL's units with no role row still reaches it.</summary>
    [Fact]
    public async Task TheCreator_InAUnitWithNoRole_ReachesTheMsel()
    {
        var creatorId = Guid.NewGuid();
        var msel = TestData.Msel(createdBy: creatorId);
        await Seed(msel);
        var creator = await Actor().WithId(creatorId).SeedAsync();
        await Db.AddUnitMembershipAsync(creator.Id, msel.Id, Ct);

        Assert.Equal(HttpStatusCode.OK, await StatusOf(creator, $"/api/msels/{msel.Id}"));
        Assert.Equal(msel.Id, Assert.Single(await MyMsels(creator)).Id);
    }

    // ---------------------------------------------------------------------------------------------
    // a unit member with no role who is on a team
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Team membership still opens the MSEL, but as a participant: the Exercise View's events only, and the
    /// MSEL is not in the build list.
    /// </summary>
    [Fact]
    public async Task AUnitMemberWithNoRoleOnATeam_GetsTheParticipantView()
    {
        var msel = TestData.Msel();
        await Seed(msel);
        var shown = TestData.ScenarioEvent(msel.Id);
        var hidden = TestData.ScenarioEvent(msel.Id, deltaSeconds: 60);
        hidden.IsHidden = true;
        await Seed(shown, hidden);
        var team = TestData.Team(msel.Id);
        await Seed(team);
        var member = await Actor().OnTeam(team).SeedAsync();
        await Db.AddUnitMembershipAsync(member.Id, msel.Id, Ct);

        Assert.Equal(HttpStatusCode.OK, await StatusOf(member, $"/api/msels/{msel.Id}"));
        Assert.Equal(HttpStatusCode.OK, await StatusOf(member, $"/api/msels/{msel.Id}/moves"));
        Assert.Equal(shown.Id, Assert.Single(await Events(member, msel.Id)).Id);
        Assert.Empty(await MyMsels(member));
    }

    // ---------------------------------------------------------------------------------------------
    // adding and removing a unit
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task AddingAUnit_LetsItsMembersReadTheMselAsViewers()
    {
        var msel = TestData.Msel();
        await Seed(msel);
        var unit = TestData.Unit();
        await Seed(unit);
        var member = await Actor().SeedAsync();
        await Seed(TestData.UnitUser(member.Id, unit.Id));
        var admin = await Actor().WithSystemPermissions(SystemPermission.ManageMsels).SeedAsync();

        Assert.Equal(HttpStatusCode.Forbidden, await StatusOf(member, $"/api/msels/{msel.Id}"));

        var added = await Client(admin).PostAsJsonAsync(
            "/api/mselunits", new { MselId = msel.Id, UnitId = unit.Id }, Ct);

        Assert.Equal(HttpStatusCode.Created, added.StatusCode);
        Assert.Equal(HttpStatusCode.OK, await StatusOf(member, $"/api/msels/{msel.Id}"));
        Assert.Equal(msel.Id, Assert.Single(await MyMsels(member)).Id);
        var role = await Db.UserMselRoles.AsNoTracking().SingleAsync(r => r.MselId == msel.Id && r.UserId == member.Id, Ct);
        Assert.Equal(MselRole.Viewer, role.Role);
    }

    /// <summary>
    /// Removing the unit takes the MSEL away even though the Viewer row it granted stays: a role reaches the
    /// MSEL only through one of its units.
    /// </summary>
    [Fact]
    public async Task RemovingAUnit_TakesTheMselAwayAndKeepsTheRole()
    {
        var msel = TestData.Msel();
        await Seed(msel);
        var member = await Actor().OnMsel(msel, MselRole.Viewer).SeedAsync();
        var unitId = member.MselRole.UnitId;
        var admin = await Actor().WithSystemPermissions(SystemPermission.ManageMsels).SeedAsync();

        var removed = await Client(admin).DeleteAsync($"/api/msels/{msel.Id}/units/{unitId}", Ct);

        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, await StatusOf(member, $"/api/msels/{msel.Id}"));
        Assert.Empty(await MyMsels(member));
        Assert.True(await Db.UserMselRoles.AnyAsync(r => r.MselId == msel.Id && r.UserId == member.Id, Ct));
    }

    // ---------------------------------------------------------------------------------------------

    private async Task<HttpStatusCode> StatusOf(TestActor actor, string route) =>
        (await Client(actor).GetAsync(route, Ct)).StatusCode;

    private async Task<UnitEntity> UnitOf(TestActor actor) =>
        await Db.Units.SingleAsync(u => u.Id == actor.MselRole.UnitId, Ct);

    private async Task<List<ViewModels.Msel>> MyMsels(TestActor actor) =>
        await Read<List<ViewModels.Msel>>(await Client(actor).GetAsync("/api/my-msels", Ct));

    private async Task<List<ViewModels.ScenarioEvent>> Events(TestActor actor, Guid mselId) =>
        await Read<List<ViewModels.ScenarioEvent>>(
            await Client(actor).GetAsync($"/api/msels/{mselId}/scenarioEvents", Ct));

    private async Task<T> Read<T>(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync(Ct);

        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {body}");

        return System.Text.Json.JsonSerializer.Deserialize<T>(body, JsonOptions);
    }
}
