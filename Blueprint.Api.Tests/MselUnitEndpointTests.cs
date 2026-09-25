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
using Blueprint.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Blueprint.Api.Tests;

/// <summary>
/// <c>MselUnitService</c> / <c>MselUnitController</c> - the six routes behind the join row that assigns a
/// unit to a MSEL. This is the other half of every <c>Msel*Requirement</c> conjunction, and the row
/// <c>MainHub.cs:200</c> walks to decide which MSEL groups a connection is put in.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Assigning a unit to a MSEL writes a <c>Viewer</c> role for every member of it</strong>
/// (<c>MselUnitService.cs:110-128</c>), which is what makes the assignment mean anything: a unit
/// membership alone satisfies no requirement but <c>MselUserRequirement</c>. The grant happens once, at
/// assignment time, over the members the unit has then - so <strong>a person added to the unit afterwards
/// gets nothing</strong> and cannot see the exercise their unit contributes to
/// (<see cref="Create_GrantsNothingToAMemberWhoJoinsTheUnitAfterwards"/>). The two requests are
/// order-dependent and nothing says so.
/// </para>
/// <para>
/// <strong>Removing the unit again leaves those roles behind, where they grant nothing.</strong> The
/// Phase 2 finding that a <c>UserMselRoleEntity</c> without a <c>UnitUserEntity</c> is a no-op, reached
/// from the assignment side: this service creates both halves and drops only one, so a MSEL accumulates
/// role rows naming people with no path to it, and re-assigning the unit then skips them as "already
/// having a role" - so a unit removed and restored comes back with whatever roles it had, which is either
/// the fix or the second bug depending on which was intended. See
/// <see cref="Delete_LeavesBehindTheRolesItsCreateGranted"/>.
/// </para>
/// <para>
/// <strong><c>UpdateAsync</c> takes its permission decision from the request body's <c>MselId</c> - the
/// seventh instance on the branch</strong>, after <c>OrganizationService</c>,
/// <c>ScenarioEventService</c>, <c>DataOptionService</c>, <c>MoveService</c>, <c>CardService</c> and
/// <c>TeamService</c>, and the only one where the row being moved <em>is</em> an authorization decision:
/// the mapper writes the body's <c>MselId</c> onto the row, so a caller who owns any MSEL may take any
/// other MSEL's unit onto their own - gaining its members - and the MSEL it left is told nothing.
/// <see cref="Update_ForAnOwnerOfTheMselNamedInTheBody_TakesAnotherMselsUnit"/>. Note the update grants no
/// roles on the MSEL it moves the unit to, so the stolen unit's members arrive with no role and the
/// assignment is inert until somebody notices.
/// </para>
/// <para>
/// <strong>The two deletes check existence before permission and the update checks permission first</strong>,
/// so within one file an unknown id is a clean 404 for everybody on one route and a 403 for a stranger on
/// the next. <c>DeleteAsync</c> is the model to copy, exactly as it was in <c>DataOptionService</c>.
/// </para>
/// <para>
/// <strong><c>CreateAsync</c> is the well-behaved one, and saying so is the point:</strong> it reads both
/// parents before deciding anything and answers a clean 404 for each, which is what
/// <c>UnitUserService.CreateAsync</c> should have done with the two locals it fetches and never reads.
/// The reads are also the only routes in the unit that answer a populated member list - the
/// <c>Include</c> chain reaches <c>Unit.UnitUsers.User</c>, where neither <c>UnitController</c> read
/// includes anything, so the same unit has members here and none there
/// (<see cref="GetByMsel_AnswersTheUnitsMembersWhereTheUnitRoutesDoNot"/>).
/// </para>
/// <para>
/// <strong>There is no <c>MselUnitHandler</c> among the 25</strong> and no <c>MselUnitCreated</c> in
/// <c>MainHubMethods</c>, so nothing a client receives says a unit was assigned to a MSEL or taken off
/// one. What a client does receive on a create is one <c>UserMselRoleCreated</c> per granted role, so
/// "these people became viewers" arrives and the reason never does; an update and both deletes are
/// silent. Nothing marks the MSEL modified either, on any of the four write paths - the sixth service in
/// this tier with that gap.
/// </para>
/// <para>
/// Both single-row reads report a missing row as <c>EntityNotFoundException&lt;MselEntity&gt;</c>, so an
/// unknown <em>assignment</em> id is answered "Msel Entity not found" - the same wrong-entity slip as the
/// <c>DataValueEntity</c> one in <c>MoveService</c> and <c>CardService</c>, and here it is indistinguishable
/// from the MSEL itself being gone. The two writes name <c>ViewModels.MselUnit</c> instead, so one service
/// spells the same condition three ways.
/// </para>
/// </remarks>
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
            BlueprintAppFactory.MselUnit(mine.Id, msel.Id),
            BlueprintAppFactory.MselUnit(theirs.Id, other.Id));
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewMsels).SeedAsync();

        var rows = await GetRows(Client(actor), msel.Id);

        Assert.Equal(mine.Id, Assert.Single(rows).UnitId);
    }

    /// <remarks>
    /// The MSEL is read first and its absence is a 404 before any permission is consulted, so a stranger
    /// learns whether a MSEL exists. That is the right shape for the route and the wrong shape for the
    /// leak; both halves are here because the contrast with
    /// <see cref="Update_ForAnIdThatIsNotThere_WithoutManageMsels_Is403"/> is the finding.
    /// </remarks>
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
        await Seed(BlueprintAppFactory.MselUnit(unit.Id, msel.Id));
        var member = await Actor().WithName("Assigned Member").SeedAsync();
        await Seed(BlueprintAppFactory.UnitUser(member.Id, unit.Id));
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
        var row = BlueprintAppFactory.MselUnit(unit.Id, msel.Id);
        await Seed(row);
        var member = await Actor().WithName("Included Member").SeedAsync();
        await Seed(BlueprintAppFactory.UnitUser(member.Id, unit.Id));
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewMsels).SeedAsync();

        var answered = await GetRow(Client(actor), row.Id);

        Assert.Equal(msel.Id, answered.MselId);
        Assert.Equal(unit.Id, answered.UnitId);
        Assert.Equal("Included Member", Assert.Single(answered.Unit.Users).Name);
    }

    /// <remarks>
    /// The exception names <c>MselEntity</c> where the row that is missing is a <c>MselUnitEntity</c>, so
    /// the API says "Msel Entity not found" for an assignment id nobody recognises - indistinguishable
    /// from the MSEL having been deleted, and the wrong thing to show a client that has just been handed
    /// this id by a list route. Naming the right type turns the message assertion red and leaves the
    /// status alone.
    /// </remarks>
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
        var row = BlueprintAppFactory.MselUnit(unit.Id, msel.Id);
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

    /// <remarks>
    /// Both parents are read before the permission check, so this is a 404 for a caller with no
    /// permission at all - the well-behaved shape <c>UnitUserService.CreateAsync</c> fetches the same two
    /// rows for and then answers 500 instead. Deciding permission first turns these into 403s.
    /// </remarks>
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

    /// <remarks>
    /// <c>ArgumentException</c> is not an <c>IApiException</c>, so the duplicate check's own message is
    /// answered as a 500 rather than the 409 the check exists to produce - the row is protected by a
    /// unique index anyway, which would have been the same 500. Throwing a <c>ConflictException</c> turns
    /// the status assertion red.
    /// </remarks>
    [Fact]
    public async Task Create_ForAPairThatIsAlreadyThere_Is500()
    {
        var msel = await SeedMsel();
        var unit = await SeedUnit();
        await Seed(BlueprintAppFactory.MselUnit(unit.Id, msel.Id));
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

    /// <remarks>
    /// The headline: the assignment is what makes a unit membership mean anything, and the grant that
    /// makes it mean anything is written here, once, over the members the unit has at this moment. Every
    /// member gets <c>Viewer</c> and nothing in the request says which role - so a unit of evaluators is
    /// assigned as viewers and somebody has to fix it row by row afterwards.
    /// </remarks>
    [Fact]
    public async Task Create_GivesEveryMemberOfTheUnitTheViewerRole()
    {
        var msel = await SeedMsel();
        var unit = await SeedUnit();
        var first = await Actor().SeedAsync();
        var second = await Actor().SeedAsync();
        await Seed(
            BlueprintAppFactory.UnitUser(first.Id, unit.Id),
            BlueprintAppFactory.UnitUser(second.Id, unit.Id));
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
        var msel = BlueprintAppFactory.Msel(author.Id);
        await Seed(msel);
        var unit = await SeedUnit();
        var member = await Actor().SeedAsync();
        await Seed(
            BlueprintAppFactory.UnitUser(author.Id, unit.Id),
            BlueprintAppFactory.UnitUser(member.Id, unit.Id));

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
        var member = await Actor().SeedAsync();
        await Seed(BlueprintAppFactory.UnitUser(member.Id, unit.Id));
        await Db.AddMselRoleAsync(member.Id, msel.Id, MselRole.Owner, Ct);
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageMsels).SeedAsync();

        await Post(Client(actor), Body(msel.Id, unit.Id));

        Assert.Equal(MselRole.Owner, Assert.Single(await StoredRoles(msel.Id)).Role);
    }

    /// <remarks>
    /// The grant happens once, so the two requests that put a person on an exercise through a unit are
    /// order-dependent: assign the unit and then add the member, and the member is in a unit the MSEL
    /// knows about with no role on it, which every helper but <c>MselUserRequirement</c> refuses. The
    /// earlier member's 200 is asserted alongside so the difference cannot be blamed on the route.
    /// Granting the role from <c>UnitUserService.CreateAsync</c> as well - the other side of the join -
    /// turns this red.
    /// </remarks>
    [Fact]
    public async Task Create_GrantsNothingToAMemberWhoJoinsTheUnitAfterwards()
    {
        var msel = await SeedMsel();
        var unit = await SeedUnit();
        var before = await Actor().SeedAsync();
        await Seed(BlueprintAppFactory.UnitUser(before.Id, unit.Id));
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
    /// the admin group. A MSEL with an empty unit therefore assigns it in complete silence, which is what
    /// the second half asserts.
    /// </remarks>
    [Fact]
    public async Task Create_BroadcastsTheRolesItGrantedAndNothingAboutTheUnit()
    {
        var msel = await SeedMsel();
        var unit = await SeedUnit();
        var member = await Actor().SeedAsync();
        await Seed(BlueprintAppFactory.UnitUser(member.Id, unit.Id));
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageMsels).SeedAsync();

        await Post(Client(actor), Body(msel.Id, unit.Id));

        Assert.Equal(
            new[] { msel.Id.ToString(), MainHub.ADMIN_DATA_GROUP }.Order(),
            Hub.Recipients(MainHubMethods.UserMselRoleCreated).Order());
        Assert.Equal(MainHubMethods.UserMselRoleCreated, Assert.Single(Methods()));

        Hub.Clear();

        var empty = await SeedUnit();

        await Post(Client(actor), Body(msel.Id, empty.Id));

        Assert.Empty(Hub.Sends);
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
        var row = BlueprintAppFactory.MselUnit(unit.Id, msel.Id);
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

    /// <remarks>
    /// The permission check runs before the row is looked up, so a caller without <c>ManageMsels</c> is
    /// answered 403 for an id that does not exist - where <c>DeleteAsync</c> eleven lines below reads the
    /// row first and answers 404 to everybody (<see cref="Delete_ForAnIdThatIsNotThere_IsA404ForAStrangerToo"/>).
    /// One service, one question, two answers. Reordering the update to match the delete turns this red.
    /// </remarks>
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

    /// <remarks>
    /// The escalation. The permission check asks whether the caller owns the MSEL <em>the body names</em>,
    /// then the mapper writes that <c>MselId</c> onto the row - so an owner of any MSEL may take any other
    /// MSEL's unit onto their own, gaining a list of its members, and the MSEL the unit left keeps no
    /// record and is sent no notification. Deciding from the stored row's <c>MselId</c>, the way
    /// <c>DeleteAsync</c> does, turns this into the 403 it should be.
    /// </remarks>
    [Fact]
    public async Task Update_ForAnOwnerOfTheMselNamedInTheBody_TakesAnotherMselsUnit()
    {
        var mine = await SeedMsel();
        var theirs = await SeedMsel();
        var unit = await SeedUnit();
        var row = BlueprintAppFactory.MselUnit(unit.Id, theirs.Id);
        await Seed(row);
        var actor = await Actor().OnMsel(mine, MselRole.Owner).SeedAsync();

        var response = await Put(Client(actor), row.Id, BodyFor(row) with { MselId = mine.Id });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(mine.Id, (await Stored(row.Id)).MselId);
        Assert.Empty(await StoredFor(theirs.Id, unit.Id));
    }

    /// <remarks>
    /// Moving a unit onto a MSEL grants nobody a role there, where creating the same assignment grants
    /// every member <c>Viewer</c> - so the unit arrives attached and inert, and its members cannot see
    /// the exercise they have been moved to. Copying the create's grant loop into the update turns this
    /// red.
    /// </remarks>
    [Fact]
    public async Task Update_GrantsNoRolesOnTheMselItMovesTheUnitTo()
    {
        var from = await SeedMsel();
        var to = await SeedMsel();
        var unit = await SeedUnit();
        var row = BlueprintAppFactory.MselUnit(unit.Id, from.Id);
        await Seed(row);
        var member = await Actor().SeedAsync();
        await Seed(BlueprintAppFactory.UnitUser(member.Id, unit.Id));
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageMsels).SeedAsync();

        await Put(Client(actor), row.Id, BodyFor(row) with { MselId = to.Id });

        Assert.Empty(await StoredRoles(to.Id));
        Assert.Equal(
            HttpStatusCode.Forbidden, (await Client(member).GetAsync(MselUnitsOf(to.Id), Ct)).StatusCode);
    }

    /// <remarks>
    /// The profile maps <c>Id</c> in both directions, so a body that does not echo the route's id writes
    /// a different key onto a tracked entity and EF refuses to modify it. A PUT is therefore unusable by
    /// any client that does not send the whole row back, and the failure is a 500 rather than the 400 a
    /// mismatched id deserves. Same shape as <c>InjectService</c>'s. Ignoring <c>Id</c> on the inbound map
    /// turns this red.
    /// </remarks>
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
        var row = BlueprintAppFactory.MselUnit(unit.Id, from.Id);
        await Seed(row);
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageMsels).SeedAsync();

        await Put(Client(actor), row.Id, BodyFor(row) with { MselId = to.Id });

        Assert.Empty(Hub.Sends);
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
        var row = BlueprintAppFactory.MselUnit(unit.Id, msel.Id);
        await Seed(row);
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        var response = await Client(actor).DeleteAsync(MselUnitRoute(row.Id), Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    /// <remarks>
    /// The Phase 2 finding reached from the side that created both halves. <c>CreateAsync</c> writes a
    /// <c>UnitUserEntity</c>'s worth of meaning into a <c>UserMselRoleEntity</c>; the delete removes only
    /// the assignment, so the role survives as a row that satisfies no requirement - the MSEL's role list
    /// still names the person, and every route refuses them. Worse, because
    /// <see cref="Create_LeavesAMemberWhoAlreadyHasARoleAlone"/> skips anybody who already holds one, the
    /// abandoned row is what a later re-assignment sees, so the mistake is self-preserving. Deleting the
    /// roles this assignment granted turns the second half red.
    /// </remarks>
    [Fact]
    public async Task Delete_LeavesBehindTheRolesItsCreateGranted()
    {
        var msel = await SeedMsel();
        var unit = await SeedUnit();
        var member = await Actor().SeedAsync();
        await Seed(BlueprintAppFactory.UnitUser(member.Id, unit.Id));
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
        var row = BlueprintAppFactory.MselUnit(unit.Id, msel.Id);
        await Seed(row);
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageMsels).SeedAsync();

        await Client(actor).DeleteAsync(MselUnitRoute(row.Id), Ct);

        Assert.Empty(Hub.Sends);
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
        var row = BlueprintAppFactory.MselUnit(unit.Id, msel.Id);
        await Seed(row);
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageMsels).SeedAsync();

        var response = await Client(actor).DeleteAsync(AssignmentRoute(msel.Id, unit.Id), Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Null(await Stored(row.Id));
    }

    /// <remarks>
    /// The third positive control for <c>b5d2d86</c>'s transposed-ids defect: <c>MselUnitController.cs:150</c>
    /// passes <c>(mselId, unitId)</c> to a method declared <c>(Guid mselId, Guid unitId, …)</c>, the right
    /// way round, where <c>CardTeamController.cs:192</c> transposes the same shape and its route deletes
    /// nothing for every well-formed request. This test fails if anybody ever "tidies" the argument order
    /// here.
    /// </remarks>
    [Fact]
    public async Task DeleteByIds_WithTheIdsTheOtherWayRound_Is404()
    {
        var msel = await SeedMsel();
        var unit = await SeedUnit();
        var row = BlueprintAppFactory.MselUnit(unit.Id, msel.Id);
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
        await Seed(BlueprintAppFactory.MselUnit(unit.Id, msel.Id));
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
        await Seed(BlueprintAppFactory.MselUnit(unit.Id, msel.Id));
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

        var response = await AnonymousClient.SendAsync(request, Ct);

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
        var unit = BlueprintAppFactory.Unit();
        await Seed(unit);

        return unit;
    }

    private async Task<MselEntity> SeedMsel()
    {
        var msel = BlueprintAppFactory.Msel();
        await Seed(msel);

        return msel;
    }

    private async Task<MselUnitEntity> SeedAssignment()
    {
        var msel = await SeedMsel();
        var unit = await SeedUnit();
        var row = BlueprintAppFactory.MselUnit(unit.Id, msel.Id);
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

    /// <summary>Every method name the hub recorded, deduplicated - "what was a client told at all".</summary>
    private IReadOnlyList<string> Methods() => [.. Hub.Sends.Select(x => x.Method).Distinct()];

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
