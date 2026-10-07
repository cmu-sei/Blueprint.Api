// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Blueprint.Api.Data.Enumerations;
using Blueprint.Api.Data.Models;
using Blueprint.Api.Hubs;
using Blueprint.Api.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Blueprint.Api.Tests.Controllers;

/// <summary><c>MselUnitService</c> / <c>MselUnitController</c> - the six routes behind the join row that
/// assigns a unit to a MSEL. This is the other half of every <c>Msel*Requirement</c> conjunction, and the
/// row <c>MainHub.cs:200</c> walks to decide which MSEL groups a connection is put in.</summary>
public class MselUnitEndpointTests(DatabaseFixture fixture, BlueprintAppFactory factory)
    : ApiTestBase(fixture, factory), IClassFixture<BlueprintAppFactory>
{
    // ---------------------------------------------------------------------------------------------
    // GET msels/{mselId}/mselunits
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task GetByMsel_ReturnsOnlyThatMselsUnits()
    {
        var msel = await SeedMsel();
        var other = await SeedMsel();
        var mine = await SeedUnit();
        var theirs = await SeedUnit();
        await Seed(
            TestData.MselUnit(mine.Id, msel.Id),
            TestData.MselUnit(theirs.Id, other.Id));
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewMsels).SeedAsync();

        var rows = await GetRows(Client(actor), msel.Id);

        Assert.Equal(mine.Id, Assert.Single(rows).UnitId);
    }

    /// <summary>An unknown MSEL is a 404 for every caller: the MSEL is read before any permission is
    /// consulted.</summary>
    [Fact]
    public async Task GetByMsel_ForAMselThatIsNotThere_IsA404ForAStrangerToo()
    {
        var holder = await Actor().WithSystemPermissions(SystemPermission.ViewMsels).SeedAsync();
        var stranger = await Actor().SeedAsync();

        var forHolder = await Client(holder).GetAsync(MselUnitsOf(Guid.NewGuid()), Ct);
        var forStranger = await Client(stranger).GetAsync(MselUnitsOf(Guid.NewGuid()), Ct);

        Assert.Equal(HttpStatusCode.NotFound, forHolder.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, forStranger.StatusCode);
    }

    [Fact]
    public async Task GetByMsel_WithViewMselsOnly_Is200()
    {
        var msel = await SeedMsel();
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewMsels).SeedAsync();

        var response = await Client(actor).GetAsync(MselUnitsOf(msel.Id), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task GetByMsel_WithoutViewMsels_Is403()
    {
        var msel = await SeedMsel();
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageUnits).SeedAsync();

        var response = await Client(actor).GetAsync(MselUnitsOf(msel.Id), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task GetByMsel_ForAMselViewer_Is200()
    {
        var msel = await SeedMsel();
        var actor = await Actor().OnMsel(msel, MselRole.Viewer).SeedAsync();

        var response = await Client(actor).GetAsync(MselUnitsOf(msel.Id), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <remarks>
    /// <c>GetByMselAsync</c> includes <c>Unit.UnitUsers.User</c>, so this is the only place in the API
    /// that answers who is in a unit: <c>GET units/{id}</c> and <c>GET my-units</c> include nothing and
    /// answer an empty list for the same unit. Both halves are asserted here because the fact is the
    /// contrast. Adding the include to <c>UnitService</c> makes the second half red, which is the point.
    /// </remarks>
    [Fact]
    public async Task GetByMsel_AnswersTheUnitsMembersWhereTheUnitRoutesDoNot()
    {
        var msel = await SeedMsel();
        var unit = await SeedUnit();
        await Seed(TestData.MselUnit(unit.Id, msel.Id));
        var member = await Actor().WithName("Assigned Member").SeedAsync();
        await Seed(TestData.UnitUser(member.Id, unit.Id));
        var actor = await Actor().WithAllSystemPermissions().SeedAsync();

        var row = Assert.Single(await GetRows(Client(actor), msel.Id));

        Assert.Equal("Assigned Member", Assert.Single(row.Unit.Users).Name);

        var throughTheUnitRoute = await Client(actor).GetAsync($"/api/units/{unit.Id}", Ct);

        Assert.Equal(HttpStatusCode.OK, throughTheUnitRoute.StatusCode);
        Assert.Empty((await Read<ViewModels.Unit>(throughTheUnitRoute)).Users);
    }

    // ---------------------------------------------------------------------------------------------
    // GET mselunits/{id}
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Get_ReturnsTheRowWithItsUnitAndItsMembers()
    {
        var msel = await SeedMsel();
        var unit = await SeedUnit();
        var row = TestData.MselUnit(unit.Id, msel.Id);
        await Seed(row);
        var member = await Actor().WithName("Included Member").SeedAsync();
        await Seed(TestData.UnitUser(member.Id, unit.Id));
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewMsels).SeedAsync();

        var answered = await GetRow(Client(actor), row.Id);

        Assert.Equal(msel.Id, answered.MselId);
        Assert.Equal(unit.Id, answered.UnitId);
        Assert.Equal("Included Member", Assert.Single(answered.Unit.Users).Name);
    }

    /// <summary>Get for an id that is not there is answered with a 404 that names the MSEL.</summary>
    [Fact]
    public async Task Get_ForAnIdThatIsNotThere_Is404ThatNamesTheMsel()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewMsels).SeedAsync();

        var response = await Client(actor).GetAsync(MselUnitRoute(Guid.NewGuid()), Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("Msel Entity not found", await response.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task Get_WithViewMselsOnly_Is200()
    {
        var row = await SeedAssignment();
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewMsels).SeedAsync();

        var response = await Client(actor).GetAsync(MselUnitRoute(row.Id), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Get_WithoutViewMsels_Is403()
    {
        var row = await SeedAssignment();
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageUnits).SeedAsync();

        var response = await Client(actor).GetAsync(MselUnitRoute(row.Id), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Get_ForAMselViewer_Is200()
    {
        var msel = await SeedMsel();
        var unit = await SeedUnit();
        var row = TestData.MselUnit(unit.Id, msel.Id);
        await Seed(row);
        var actor = await Actor().OnMsel(msel, MselRole.Viewer).SeedAsync();

        var response = await Client(actor).GetAsync(MselUnitRoute(row.Id), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ---------------------------------------------------------------------------------------------
    // POST mselunits
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Create_StoresTheRowAndAnswers201()
    {
        var msel = await SeedMsel();
        var unit = await SeedUnit();
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageMsels).SeedAsync();

        var response = await Post(Client(actor), Body(msel.Id, unit.Id));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var created = await Read<ViewModels.MselUnit>(response);
        var stored = await Stored(created.Id);

        Assert.Equal(msel.Id, stored.MselId);
        Assert.Equal(unit.Id, stored.UnitId);
    }

    [Fact]
    public async Task Create_AnswersALocationHeaderPointingAtTheRow()
    {
        var msel = await SeedMsel();
        var unit = await SeedUnit();
        var actor = await Actor().WithAllSystemPermissions().SeedAsync();

        var response = await Post(Client(actor), Body(msel.Id, unit.Id));
        var created = await Read<ViewModels.MselUnit>(response);

        Assert.EndsWith($"/api/mselunits/{created.Id}", response.Headers.Location.ToString());
    }

    /// <remarks>
    /// <c>createMselUnit</c> requires <c>ManageMsels</c> and its <c>Location</c> header names
    /// <c>getMselUnit</c>, which requires <c>ViewMsels</c> - so the caller who just made the assignment is
    /// answered 403 by the header they were handed. Fourth instance of this shape on the branch, after
    /// <c>createTeam</c>, <c>createUnit</c> and <c>createUnitUser</c>.
    /// </remarks>
    [Fact]
    public async Task Create_WithManageMselsOnly_CannotFollowItsOwnLocationHeader()
    {
        var msel = await SeedMsel();
        var unit = await SeedUnit();
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageMsels).SeedAsync();

        var response = await Post(Client(actor), Body(msel.Id, unit.Id));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var followed = await Client(actor).GetAsync(response.Headers.Location, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, followed.StatusCode);
    }

    [Fact]
    public async Task Create_ForAnOwnerOfTheMsel_Is201()
    {
        var msel = await SeedMsel();
        var unit = await SeedUnit();
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        var response = await Post(Client(actor), Body(msel.Id, unit.Id));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    /// <remarks>
    /// <c>MselOwnerRequirement</c> accepts the creator and the <c>Owner</c> role and nothing else, so a
    /// viewer of the MSEL - who may read every assignment it has - may not add one.
    /// </remarks>
    [Fact]
    public async Task Create_ForAViewerOfTheMsel_Is403()
    {
        var msel = await SeedMsel();
        var unit = await SeedUnit();
        var actor = await Actor().OnMsel(msel, MselRole.Viewer).SeedAsync();

        var response = await Post(Client(actor), Body(msel.Id, unit.Id));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(await StoredFor(msel.Id, unit.Id));
    }

    /// <summary>Both parents are read before the permission check, so an unknown MSEL is a 404 for every
    /// caller.</summary>
    [Fact]
    public async Task Create_ForAMselThatIsNotThere_IsA404ForAStrangerToo()
    {
        var unit = await SeedUnit();
        var holder = await Actor().WithSystemPermissions(SystemPermission.ManageMsels).SeedAsync();
        var stranger = await Actor().SeedAsync();

        var forHolder = await Post(Client(holder), Body(Guid.NewGuid(), unit.Id));
        var forStranger = await Post(Client(stranger), Body(Guid.NewGuid(), unit.Id));

        Assert.Equal(HttpStatusCode.NotFound, forHolder.StatusCode);
        Assert.Contains("Msel Entity not found", await forHolder.Content.ReadAsStringAsync(Ct));
        Assert.Equal(HttpStatusCode.NotFound, forStranger.StatusCode);
    }

    [Fact]
    public async Task Create_ForAUnitThatIsNotThere_Is404()
    {
        var msel = await SeedMsel();
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageMsels).SeedAsync();

        var response = await Post(Client(actor), Body(msel.Id, Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("Unit Entity not found", await response.Content.ReadAsStringAsync(Ct));
    }

    /// <summary>Create for a pair that is already there is answered with a 500.</summary>
    [Fact]
    public async Task Create_ForAPairThatIsAlreadyThere_Is500()
    {
        var msel = await SeedMsel();
        var unit = await SeedUnit();
        await Seed(TestData.MselUnit(unit.Id, msel.Id));
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageMsels).SeedAsync();

        var response = await Post(Client(actor), Body(msel.Id, unit.Id));

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Contains("MSEL Unit already exists.", await response.Content.ReadAsStringAsync(Ct));
        Assert.Single(await StoredFor(msel.Id, unit.Id));
    }

    [Fact]
    public async Task Create_KeepsTheBodysId()
    {
        var id = Guid.NewGuid();
        var msel = await SeedMsel();
        var unit = await SeedUnit();
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageMsels).SeedAsync();

        var response = await Post(Client(actor), Body(msel.Id, unit.Id) with { Id = id });

        Assert.Equal(id, (await Read<ViewModels.MselUnit>(response)).Id);
        Assert.NotNull(await Stored(id));
    }

    /// <summary>Create gives every member of the unit the viewer role.</summary>
    [Fact]
    public async Task Create_GivesEveryMemberOfTheUnitTheViewerRole()
    {
        var msel = await SeedMsel();
        var unit = await SeedUnit();
        var first = await Actor().SeedAsync();
        var second = await Actor().SeedAsync();
        await Seed(
            TestData.UnitUser(first.Id, unit.Id),
            TestData.UnitUser(second.Id, unit.Id));
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageMsels).SeedAsync();

        var response = await Post(Client(actor), Body(msel.Id, unit.Id));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var roles = await StoredRoles(msel.Id);

        Assert.Equal(new[] { first.Id, second.Id }.Order(), roles.Select(x => x.UserId).Order());
        Assert.All(roles, x => Assert.Equal(MselRole.Viewer, x.Role));
    }

    /// <remarks>
    /// The MSEL's creator is excluded by <c>uu.UserId != msel.CreatedBy</c>, the comment saying they are
    /// "given the Creator role in the UI" - which is not a <see cref="MselRole"/> value and not a row
    /// anything writes. What carries them is <c>MselViewRequirement</c>'s creator branch, so the
    /// exclusion is harmless for reading and leaves them without the <c>Viewer</c> row every helper that
    /// ignores the creator would need.
    /// </remarks>
    [Fact]
    public async Task Create_SkipsTheMselsCreator()
    {
        var author = await Actor().WithSystemPermissions(SystemPermission.ManageMsels).SeedAsync();
        var msel = TestData.Msel(author.Id);
        await Seed(msel);
        var unit = await SeedUnit();
        var member = await Actor().SeedAsync();
        await Seed(
            TestData.UnitUser(author.Id, unit.Id),
            TestData.UnitUser(member.Id, unit.Id));

        await Post(Client(author), Body(msel.Id, unit.Id));

        Assert.Equal(member.Id, Assert.Single(await StoredRoles(msel.Id)).UserId);
    }

    /// <remarks>
    /// A member who already holds a role keeps it, which is what stops an assignment from demoting an
    /// owner to a viewer - and is also what makes <see cref="Delete_LeavesBehindTheRolesItsCreateGranted"/>
    /// permanent, since a re-assignment sees the abandoned <c>Viewer</c> row as a role already held.
    /// </remarks>
    [Fact]
    public async Task Create_LeavesAMemberWhoAlreadyHasARoleAlone()
    {
        var msel = await SeedMsel();
        var unit = await SeedUnit();
        var member = await Actor().InUnit(unit).SeedAsync();
        await Db.AddMselRoleAsync(member.Id, msel.Id, MselRole.Owner, Ct);
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageMsels).SeedAsync();

        await Post(Client(actor), Body(msel.Id, unit.Id));

        Assert.Equal(MselRole.Owner, Assert.Single(await StoredRoles(msel.Id)).Role);
    }

    /// <summary>A member who joins the unit after it is assigned gets no role on the MSEL.</summary>
    [Fact]
    public async Task Create_GrantsNothingToAMemberWhoJoinsTheUnitAfterwards()
    {
        var msel = await SeedMsel();
        var unit = await SeedUnit();
        var before = await Actor().InUnit(unit).SeedAsync();
        var actor = await Actor().WithAllSystemPermissions().SeedAsync();

        await Post(Client(actor), Body(msel.Id, unit.Id));

        var after = await Actor().SeedAsync();
        var added = await Client(actor).PostAsJsonAsync(
            "/api/unitusers", new { UserId = after.Id, UnitId = unit.Id }, Ct);

        Assert.Equal(HttpStatusCode.Created, added.StatusCode);

        Assert.Equal(before.Id, Assert.Single(await StoredRoles(msel.Id)).UserId);
        Assert.Equal(
            HttpStatusCode.OK, (await Client(before).GetAsync(MselUnitsOf(msel.Id), Ct)).StatusCode);
        Assert.Equal(
            HttpStatusCode.Forbidden, (await Client(after).GetAsync(MselUnitsOf(msel.Id), Ct)).StatusCode);
    }

    /// <remarks>
    /// There is no <c>MselUnitHandler</c> and no <c>MselUnitCreated</c> method, so the only thing a client
    /// hears is the side effect: one <c>UserMselRoleCreated</c> per granted role, to the MSEL's group and
    /// the admin group. A MSEL with an empty unit therefore assigns it in complete silence
    /// (<see cref="Create_ForAUnitWithNoMembers_BroadcastsNothing"/>).
    /// </remarks>
    [Fact]
    public async Task Create_BroadcastsTheRolesItGrantedAndNothingAboutTheUnit()
    {
        var msel = await SeedMsel();
        var unit = await SeedUnit();
        var member = await Actor().InUnit(unit).SeedAsync();
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageMsels).SeedAsync();

        await Post(Client(actor), Body(msel.Id, unit.Id));

        Assert.Equal(
            new[] { msel.Id.ToString(), MainHub.ADMIN_DATA_GROUP }.Order(),
            Hub.Recipients(MainHubMethods.UserMselRoleCreated, msel.Id).Order());
        Assert.Equal(MainHubMethods.UserMselRoleCreated, Assert.Single(Methods(msel.Id, unit.Id)));
    }

    /// <summary>A MSEL assigned a unit with no members hears nothing about it.</summary>
    [Fact]
    public async Task Create_ForAUnitWithNoMembers_BroadcastsNothing()
    {
        var msel = await SeedMsel();
        var empty = await SeedUnit();
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageMsels).SeedAsync();

        await Post(Client(actor), Body(msel.Id, empty.Id));

        Assert.Empty(Hub.Sent(msel.Id, empty.Id));
    }

    /// <remarks>
    /// No write path in this service calls <c>ServiceUtilities.SetMselModifiedAsync</c>, so changing who
    /// can reach an exercise does not change when it was last modified - the sixth service in this tier
    /// with that gap, after <c>CardService</c>, <c>CardTeamService</c>, <c>InjectService</c>,
    /// <c>TeamService</c> and <c>TeamUserService</c>.
    /// </remarks>
    [Fact]
    public async Task Create_DoesNotMarkTheMselModified()
    {
        var msel = await SeedMsel();
        var unit = await SeedUnit();
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageMsels).SeedAsync();

        await Post(Client(actor), Body(msel.Id, unit.Id));

        Assert.Null((await StoredMsel(msel.Id)).DateModified);
    }

    [Fact]
    public async Task Create_WithNoBody_Is400()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageMsels).SeedAsync();

        using var content = new StringContent(string.Empty, Encoding.UTF8, "application/json");

        var response = await Client(actor).PostAsync(MselUnits, content, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ---------------------------------------------------------------------------------------------
    // PUT mselunits/{id}
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Update_ChangesTheUnitAndAnswers200()
    {
        var msel = await SeedMsel();
        var unit = await SeedUnit();
        var replacement = await SeedUnit();
        var row = TestData.MselUnit(unit.Id, msel.Id);
        await Seed(row);
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageMsels).SeedAsync();

        var response = await Put(Client(actor), row.Id, BodyFor(row) with { UnitId = replacement.Id });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(replacement.Id, (await Stored(row.Id)).UnitId);
    }

    [Fact]
    public async Task Update_ForAnIdThatIsNotThere_Is404()
    {
        var msel = await SeedMsel();
        var unit = await SeedUnit();
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageMsels).SeedAsync();

        var response = await Put(Client(actor), Guid.NewGuid(), Body(msel.Id, unit.Id));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>Without <c>ManageMsels</c>, an update of an id that is not there is a 403; the delete answers
    /// 404.</summary>
    [Fact]
    public async Task Update_ForAnIdThatIsNotThere_WithoutManageMsels_Is403()
    {
        var msel = await SeedMsel();
        var unit = await SeedUnit();
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewMsels).SeedAsync();

        var response = await Put(Client(actor), Guid.NewGuid(), Body(msel.Id, unit.Id));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Update_WithManageMselsOnly_Is200()
    {
        var row = await SeedAssignment();
        var replacement = await SeedUnit();
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageMsels).SeedAsync();

        var response = await Put(Client(actor), row.Id, BodyFor(row) with { UnitId = replacement.Id });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Update_WithoutManageMsels_Is403()
    {
        var row = await SeedAssignment();
        var replacement = await SeedUnit();
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewMsels).SeedAsync();

        var response = await Put(Client(actor), row.Id, BodyFor(row) with { UnitId = replacement.Id });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(row.UnitId, (await Stored(row.Id)).UnitId);
    }

    /// <summary>Update for an owner of the MSEL named in the body takes another MSELs unit.</summary>
    [Fact]
    public async Task Update_ForAnOwnerOfTheMselNamedInTheBody_TakesAnotherMselsUnit()
    {
        var mine = await SeedMsel();
        var theirs = await SeedMsel();
        var unit = await SeedUnit();
        var row = TestData.MselUnit(unit.Id, theirs.Id);
        await Seed(row);
        var actor = await Actor().OnMsel(mine, MselRole.Owner).SeedAsync();

        var response = await Put(Client(actor), row.Id, BodyFor(row) with { MselId = mine.Id });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(mine.Id, (await Stored(row.Id)).MselId);
        Assert.Empty(await StoredFor(theirs.Id, unit.Id));
    }

    /// <summary>Moving an assignment to another MSEL grants its members no role there.</summary>
    [Fact]
    public async Task Update_GrantsNoRolesOnTheMselItMovesTheUnitTo()
    {
        var from = await SeedMsel();
        var to = await SeedMsel();
        var unit = await SeedUnit();
        var row = TestData.MselUnit(unit.Id, from.Id);
        await Seed(row);
        var member = await Actor().InUnit(unit).SeedAsync();
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageMsels).SeedAsync();

        await Put(Client(actor), row.Id, BodyFor(row) with { MselId = to.Id });

        Assert.Empty(await StoredRoles(to.Id));
        Assert.Equal(
            HttpStatusCode.Forbidden, (await Client(member).GetAsync(MselUnitsOf(to.Id), Ct)).StatusCode);
    }

    /// <summary>Update that omits the id is answered with a 500.</summary>
    [Fact]
    public async Task Update_ThatOmitsTheId_Is500()
    {
        var row = await SeedAssignment();
        var replacement = await SeedUnit();
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageMsels).SeedAsync();

        var response = await Put(
            Client(actor), row.Id, new MselUnitBody { MselId = row.MselId, UnitId = replacement.Id });

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    /// <remarks>
    /// No handler, no role rows, no MSEL stamp - so moving a unit from one exercise to another is
    /// invisible to every connected client on both of them.
    /// </remarks>
    [Fact]
    public async Task Update_BroadcastsNothingAndMarksNeitherMselModified()
    {
        var from = await SeedMsel();
        var to = await SeedMsel();
        var unit = await SeedUnit();
        var row = TestData.MselUnit(unit.Id, from.Id);
        await Seed(row);
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageMsels).SeedAsync();

        await Put(Client(actor), row.Id, BodyFor(row) with { MselId = to.Id });

        Assert.Empty(Hub.Sent(from.Id, to.Id, unit.Id));
        Assert.Null((await StoredMsel(from.Id)).DateModified);
        Assert.Null((await StoredMsel(to.Id)).DateModified);
    }

    // ---------------------------------------------------------------------------------------------
    // DELETE mselunits/{id}
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Delete_RemovesTheRowAndAnswers204()
    {
        var row = await SeedAssignment();
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageMsels).SeedAsync();

        var response = await Client(actor).DeleteAsync(MselUnitRoute(row.Id), Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Null(await Stored(row.Id));
    }

    [Fact]
    public async Task Delete_ForAnIdThatIsNotThere_IsA404ForAStrangerToo()
    {
        var holder = await Actor().WithSystemPermissions(SystemPermission.ManageMsels).SeedAsync();
        var stranger = await Actor().SeedAsync();

        var forHolder = await Client(holder).DeleteAsync(MselUnitRoute(Guid.NewGuid()), Ct);
        var forStranger = await Client(stranger).DeleteAsync(MselUnitRoute(Guid.NewGuid()), Ct);

        Assert.Equal(HttpStatusCode.NotFound, forHolder.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, forStranger.StatusCode);
    }

    [Fact]
    public async Task Delete_WithoutManageMsels_Is403()
    {
        var row = await SeedAssignment();
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewMsels).SeedAsync();

        var response = await Client(actor).DeleteAsync(MselUnitRoute(row.Id), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.NotNull(await Stored(row.Id));
    }

    [Fact]
    public async Task Delete_ForAnOwnerOfTheMsel_Is204()
    {
        var msel = await SeedMsel();
        var unit = await SeedUnit();
        var row = TestData.MselUnit(unit.Id, msel.Id);
        await Seed(row);
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        var response = await Client(actor).DeleteAsync(MselUnitRoute(row.Id), Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    /// <summary>Removing the assignment leaves the <c>Viewer</c> roles its create wrote.</summary>
    [Fact]
    public async Task Delete_LeavesBehindTheRolesItsCreateGranted()
    {
        var msel = await SeedMsel();
        var unit = await SeedUnit();
        var member = await Actor().InUnit(unit).SeedAsync();
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageMsels).SeedAsync();

        var created = await Read<ViewModels.MselUnit>(await Post(Client(actor), Body(msel.Id, unit.Id)));

        Assert.Equal(
            HttpStatusCode.OK, (await Client(member).GetAsync(MselUnitsOf(msel.Id), Ct)).StatusCode);

        var response = await Client(actor).DeleteAsync(MselUnitRoute(created.Id), Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(
            HttpStatusCode.Forbidden, (await Client(member).GetAsync(MselUnitsOf(msel.Id), Ct)).StatusCode);
        Assert.Equal(MselRole.Viewer, Assert.Single(await StoredRoles(msel.Id)).Role);
    }

    [Fact]
    public async Task Delete_BroadcastsNothingAndDoesNotMarkTheMselModified()
    {
        var msel = await SeedMsel();
        var unit = await SeedUnit();
        var row = TestData.MselUnit(unit.Id, msel.Id);
        await Seed(row);
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageMsels).SeedAsync();

        await Client(actor).DeleteAsync(MselUnitRoute(row.Id), Ct);

        Assert.Empty(Hub.Sent(msel.Id, unit.Id));
        Assert.Null((await StoredMsel(msel.Id)).DateModified);
    }

    // ---------------------------------------------------------------------------------------------
    // DELETE msels/{mselId}/units/{unitId}
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task DeleteByIds_RemovesTheRowAndAnswers204()
    {
        var msel = await SeedMsel();
        var unit = await SeedUnit();
        var row = TestData.MselUnit(unit.Id, msel.Id);
        await Seed(row);
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageMsels).SeedAsync();

        var response = await Client(actor).DeleteAsync(AssignmentRoute(msel.Id, unit.Id), Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Null(await Stored(row.Id));
    }

    /// <summary>The route passes its ids in the declared order, so the transposed ids name no row.</summary>
    [Fact]
    public async Task DeleteByIds_WithTheIdsTheOtherWayRound_Is404()
    {
        var msel = await SeedMsel();
        var unit = await SeedUnit();
        var row = TestData.MselUnit(unit.Id, msel.Id);
        await Seed(row);
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageMsels).SeedAsync();

        var response = await Client(actor).DeleteAsync(AssignmentRoute(unit.Id, msel.Id), Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.NotNull(await Stored(row.Id));
    }

    [Fact]
    public async Task DeleteByIds_ForAPairThatIsNotThere_Is404()
    {
        var msel = await SeedMsel();
        var unit = await SeedUnit();
        await Seed(TestData.MselUnit(unit.Id, msel.Id));
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageMsels).SeedAsync();

        var response = await Client(actor).DeleteAsync(AssignmentRoute(msel.Id, Guid.NewGuid()), Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Single(await StoredFor(msel.Id, unit.Id));
    }

    [Fact]
    public async Task DeleteByIds_WithoutManageMsels_Is403()
    {
        var msel = await SeedMsel();
        var unit = await SeedUnit();
        await Seed(TestData.MselUnit(unit.Id, msel.Id));
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewMsels).SeedAsync();

        var response = await Client(actor).DeleteAsync(AssignmentRoute(msel.Id, unit.Id), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Single(await StoredFor(msel.Id, unit.Id));
    }

    // ---------------------------------------------------------------------------------------------
    // Authentication
    // ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("GET", "msels/00000000-0000-0000-0000-000000000001/mselunits")]
    [InlineData("GET", "mselunits/00000000-0000-0000-0000-000000000001")]
    [InlineData("POST", "mselunits")]
    [InlineData("PUT", "mselunits/00000000-0000-0000-0000-000000000001")]
    [InlineData("DELETE", "mselunits/00000000-0000-0000-0000-000000000001")]
    [InlineData("DELETE", "msels/00000000-0000-0000-0000-000000000001/units/00000000-0000-0000-0000-000000000002")]
    public async Task EveryRouteRefusesAnUnauthenticatedRequest(string method, string route)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), $"/api/{route}")
        {
            Content = JsonContent.Create(new { })
        };

        var response = await Client().SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ---------------------------------------------------------------------------------------------
    // Harness
    // ---------------------------------------------------------------------------------------------

    private const string MselUnits = "/api/mselunits";

    private static string MselUnitRoute(Guid id) => $"{MselUnits}/{id}";

    private static string MselUnitsOf(Guid mselId) => $"/api/msels/{mselId}/mselunits";

    private static string AssignmentRoute(Guid mselId, Guid unitId) =>
        $"/api/msels/{mselId}/units/{unitId}";

    /// <summary>
    /// The wire shape of a unit assignment. <c>ViewModels.MselUnit</c> does not derive from
    /// <c>Base</c> - unlike <c>ViewModels.UnitUser</c>, whose entity has no audit columns either - so
    /// there are no audit fields to send or to be answered zeros for.
    /// </summary>
    private sealed record MselUnitBody
    {
        public Guid Id { get; init; }
        public Guid MselId { get; init; }
        public Guid UnitId { get; init; }
    }

    private static MselUnitBody Body(Guid mselId, Guid unitId) =>
        new() { MselId = mselId, UnitId = unitId };

    private static MselUnitBody BodyFor(MselUnitEntity row) =>
        new() { Id = row.Id, MselId = row.MselId, UnitId = row.UnitId };

    private async Task<UnitEntity> SeedUnit()
    {
        var unit = TestData.Unit();
        await Seed(unit);

        return unit;
    }

    private async Task<MselEntity> SeedMsel()
    {
        var msel = TestData.Msel();
        await Seed(msel);

        return msel;
    }

    private async Task<MselUnitEntity> SeedAssignment()
    {
        var msel = await SeedMsel();
        var unit = await SeedUnit();
        var row = TestData.MselUnit(unit.Id, msel.Id);
        await Seed(row);

        return row;
    }

    private Task<HttpResponseMessage> Post(HttpClient client, MselUnitBody body) =>
        client.PostAsJsonAsync(MselUnits, body, Ct);

    private Task<HttpResponseMessage> Put(HttpClient client, Guid id, MselUnitBody body) =>
        client.PutAsJsonAsync(MselUnitRoute(id), body, Ct);

    private async Task<List<ViewModels.MselUnit>> GetRows(HttpClient client, Guid mselId)
    {
        var response = await client.GetAsync(MselUnitsOf(mselId), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await Read<List<ViewModels.MselUnit>>(response);
    }

    private async Task<ViewModels.MselUnit> GetRow(HttpClient client, Guid id)
    {
        var response = await client.GetAsync(MselUnitRoute(id), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await Read<ViewModels.MselUnit>(response);
    }

    /// <summary>Every method name the hub sent those audiences, deduplicated: "what was a client told at all".</summary>
    private IReadOnlyList<string> Methods(params object[] groups) => [.. Hub.Sent(groups).Select(x => x.Method).Distinct()];

    private async Task<MselUnitEntity> Stored(Guid id)
    {
        await using var context = NewContext();

        return await context.MselUnits.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, Ct);
    }

    private async Task<List<MselUnitEntity>> StoredFor(Guid mselId, Guid unitId)
    {
        await using var context = NewContext();

        return await context.MselUnits.AsNoTracking()
            .Where(x => x.MselId == mselId && x.UnitId == unitId)
            .ToListAsync(Ct);
    }

    private async Task<List<UserMselRoleEntity>> StoredRoles(Guid mselId)
    {
        await using var context = NewContext();

        return await context.UserMselRoles.AsNoTracking()
            .Where(x => x.MselId == mselId)
            .ToListAsync(Ct);
    }

    private async Task<MselEntity> StoredMsel(Guid id)
    {
        await using var context = NewContext();

        return await context.Msels.AsNoTracking().SingleAsync(x => x.Id == id, Ct);
    }

    private async Task<T> Read<T>(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync(Ct);

        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {body}");

        return JsonSerializer.Deserialize<T>(body, JsonOptions);
    }
}
