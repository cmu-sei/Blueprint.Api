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
/// <c>UnitUserService</c> / <c>UnitUserController</c> - the five routes behind the join row that puts a
/// person in a unit, which is the half of every <c>Msel*Requirement</c> conjunction that has to match
/// before the role query is ever reached.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Nothing records who put a user in a unit, or when.</strong> <c>UnitUserEntity</c> is not a
/// <c>BaseEntity</c> - it has four columns and no audit ones - while <c>ViewModels.UnitUser</c> derives
/// from <c>Base</c>, so every route here answers an all-zeros <c>createdBy</c> and a <c>dateCreated</c> of
/// <c>0001-01-01</c> (<see cref="Get_AnswersAuditFieldsTheEntityDoesNotHave"/>). Both places that assign
/// the creator - <c>UnitUserController.cs:97</c> and <c>UnitUserService.cs:70</c> - are therefore dead
/// code. Exactly the defect <c>4dd5201</c> recorded for <c>TeamUserEntity</c>, one level up: a unit is
/// how a person reaches a MSEL, so this is the audit trail for granting access to an exercise.
/// </para>
/// <para>
/// <strong><c>CreateAsync</c> validates nothing, under a comment saying it does.</strong>
/// "make sure this would not add a duplicate user on any pending or active msels" is followed by two
/// <c>FindAsync</c> locals that are never read (and take no <c>CancellationToken</c>), so an unknown user
/// id or an unknown unit id reaches the database as a foreign-key violation and is a 500 - see
/// <see cref="Create_ForAUserThatIsNotThere_Is500"/> and
/// <see cref="Create_ForAUnitThatIsNotThere_Is500"/>. <c>MselUnitService.CreateAsync</c> checks both of
/// its parents and answers two clean 404s; it is the model to copy, and it is in the same tier.
/// </para>
/// <para>
/// <strong>The list route has no filter at all</strong> - <c>GetAsync()</c> is
/// <c>_context.UnitUsers.ToListAsync(ct)</c>, so one <c>ViewUnits</c> holder reads every unit membership
/// in the installation (<see cref="GetAll_ReturnsEveryRowInTheInstallation"/>). It also does not
/// <c>Include</c> the user, where the single read does, so the same row answers a populated <c>user</c>
/// by id and a null one in the list (<see cref="GetAll_AnswersANullUserOnEveryRow"/>). Neither read
/// loads <c>Unit</c>, so no route here names the unit a membership is in except by id.
/// </para>
/// <para>
/// <strong><c>DELETE units/{unitId}/users/{userId}</c> is a second positive control for
/// <c>b5d2d86</c>'s transposed-ids defect.</strong> <c>UnitUserController.cs:143</c> calls
/// <c>DeleteByIdsAsync(unitId, userId, ct)</c> against a signature of <c>(Guid unitId, Guid userId, …)</c>
/// - the right way round, where <c>CardTeamController.cs:192</c> transposes the same shape and its route
/// deletes nothing for every well-formed request. Two same-typed ids either side of a call site is a
/// defect no compiler can find, so the working copy earns
/// <see cref="DeleteByIds_WithTheIdsTheOtherWayRound_Is404"/>.
/// </para>
/// <para>
/// Both deletes read the row before they decide anything, so an unknown id is a clean 404 on both -
/// which is the shape <c>UnitService</c>, <c>OrganizationService</c>, <c>MoveService</c>,
/// <c>CardService</c> and <c>InjectService</c> should have copied. Neither withdraws the
/// <c>UserMselRole</c> rows the membership made meaningful, so removing a person from a unit silently
/// turns every role they hold into a no-op while the exercise's member list still names them -
/// <see cref="Delete_LeavesTheUsersMselRolesBehindWhereTheyGrantNothing"/>, the Phase 2 finding reached
/// through the API for the first time.
/// </para>
/// </remarks>
public class UnitUserEndpointTests(DatabaseFixture fixture, BlueprintAppFactory factory)
    : ApiTestBase(fixture, factory), IClassFixture<BlueprintAppFactory>
{
    // ---------------------------------------------------------------------------------------------
    // GET unitusers
    // ---------------------------------------------------------------------------------------------

    /// <remarks>
    /// <c>GetAsync()</c> takes no argument and applies no <c>Where</c>, so this is every unit membership
    /// in the installation - including units the caller is not in and MSELs they cannot see. Adding any
    /// scope at all turns this red.
    /// </remarks>
    [Fact]
    public async Task GetAll_ReturnsEveryRowInTheInstallation()
    {
        var mine = await SeedUnit();
        var theirs = await SeedUnit();
        var first = await Actor().SeedAsync();
        var second = await Actor().SeedAsync();
        await Seed(
            BlueprintAppFactory.UnitUser(first.Id, mine.Id),
            BlueprintAppFactory.UnitUser(second.Id, theirs.Id));
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewUnits).SeedAsync();

        var rows = await GetRows(Client(actor));

        Assert.Equal(
            new[] { first.Id, second.Id }.Order(), rows.Select(x => x.UserId).Order());
    }

    [Fact]
    public async Task GetAll_WithViewUnitsOnly_Is200()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewUnits).SeedAsync();

        var response = await Client(actor).GetAsync(UnitUsers, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task GetAll_WithoutViewUnits_Is403()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageUsers).SeedAsync();

        var response = await Client(actor).GetAsync(UnitUsers, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <remarks>
    /// The list route does not <c>Include(tu => tu.User)</c> and the single read does, so the answer to
    /// "who is in this unit" depends on which route asked - a client listing memberships has nothing but
    /// ids and must fetch each row again to render a name. Adding the <c>Include</c> turns this red and
    /// makes <see cref="Get_ReturnsTheRowWithItsUser"/> the whole story.
    /// </remarks>
    [Fact]
    public async Task GetAll_AnswersANullUserOnEveryRow()
    {
        var unit = await SeedUnit();
        var member = await Actor().WithName("Listed Member").SeedAsync();
        await Seed(BlueprintAppFactory.UnitUser(member.Id, unit.Id));
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewUnits).SeedAsync();

        var row = Assert.Single(await GetRows(Client(actor)));

        Assert.Equal(member.Id, row.UserId);
        Assert.Null(row.User);
        Assert.Null(row.Unit);
    }

    // ---------------------------------------------------------------------------------------------
    // GET unitusers/{id}
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Get_ReturnsTheRowWithItsUser()
    {
        var unit = await SeedUnit();
        var member = await Actor().WithName("Named Member").SeedAsync();
        var row = BlueprintAppFactory.UnitUser(member.Id, unit.Id);
        await Seed(row);
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewUnits).SeedAsync();

        var answered = await GetRow(Client(actor), row.Id);

        Assert.Equal(member.Id, answered.UserId);
        Assert.Equal(unit.Id, answered.UnitId);
        Assert.Equal("Named Member", answered.User.Name);
    }

    /// <remarks>
    /// The <c>Include</c> reaches <c>User</c> and stops, so the row knows which unit it is in only as a
    /// bare id - and no route in <c>UnitController</c> will name it for a caller without
    /// <c>ViewUnits</c>, which this route already required. Adding <c>.Include(tu => tu.Unit)</c> turns
    /// this red.
    /// </remarks>
    [Fact]
    public async Task Get_AnswersANullUnit()
    {
        var unit = await SeedUnit();
        var member = await Actor().SeedAsync();
        var row = BlueprintAppFactory.UnitUser(member.Id, unit.Id);
        await Seed(row);
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewUnits).SeedAsync();

        Assert.Null((await GetRow(Client(actor), row.Id)).Unit);
    }

    /// <remarks>
    /// <c>UnitUserEntity</c> has no audit columns, so AutoMapper leaves the four properties
    /// <c>ViewModels.UnitUser</c> inherits from <c>Base</c> at their defaults and the API answers them
    /// anyway. The controller's <c>unit.CreatedBy = User.GetId()</c> and the service's
    /// <c>unitUser.CreatedBy = _user.GetId()</c> are both written and both dropped by the map. Giving the
    /// entity the columns - or dropping <c>Base</c> from the view model, as <c>MselUnit</c> does - turns
    /// this red.
    /// </remarks>
    [Fact]
    public async Task Get_AnswersAuditFieldsTheEntityDoesNotHave()
    {
        var unit = await SeedUnit();
        var member = await Actor().SeedAsync();
        var row = BlueprintAppFactory.UnitUser(member.Id, unit.Id);
        await Seed(row);
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewUnits).SeedAsync();

        var answered = await GetRow(Client(actor), row.Id);

        Assert.Equal(Guid.Empty, answered.CreatedBy);
        Assert.Equal(default, answered.DateCreated);
        Assert.Null(answered.ModifiedBy);
        Assert.Null(answered.DateModified);
    }

    [Fact]
    public async Task Get_ForAnIdThatIsNotThere_Is404()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewUnits).SeedAsync();

        var response = await Client(actor).GetAsync(UnitUserRoute(Guid.NewGuid()), Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Get_WithViewUnitsOnly_Is200()
    {
        var row = await SeedMembership();
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewUnits).SeedAsync();

        var response = await Client(actor).GetAsync(UnitUserRoute(row.Id), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Get_WithoutViewUnits_Is403()
    {
        var row = await SeedMembership();
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageUsers).SeedAsync();

        var response = await Client(actor).GetAsync(UnitUserRoute(row.Id), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <remarks>
    /// The route requires <c>ViewUnits</c> outright and the service checks nothing else, so the person
    /// the row is about cannot read it. Same shape as <c>GET units/{id}</c> refusing a member of the
    /// unit.
    /// </remarks>
    [Fact]
    public async Task Get_ForTheUserTheRowIsAbout_Is403()
    {
        var unit = await SeedUnit();
        var member = await Actor().SeedAsync();
        var row = BlueprintAppFactory.UnitUser(member.Id, unit.Id);
        await Seed(row);

        var response = await Client(member).GetAsync(UnitUserRoute(row.Id), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ---------------------------------------------------------------------------------------------
    // POST unitusers
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Create_StoresTheRowAndAnswers201()
    {
        var unit = await SeedUnit();
        var member = await Actor().SeedAsync();
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageUnits).SeedAsync();

        var response = await Post(Client(actor), Body(member.Id, unit.Id));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var created = await Read<ViewModels.UnitUser>(response);
        var stored = await Stored(created.Id);

        Assert.Equal(member.Id, stored.UserId);
        Assert.Equal(unit.Id, stored.UnitId);
    }

    [Fact]
    public async Task Create_AnswersALocationHeaderPointingAtTheRow()
    {
        var unit = await SeedUnit();
        var member = await Actor().SeedAsync();
        var actor = await Actor().WithAllSystemPermissions().SeedAsync();

        var response = await Post(Client(actor), Body(member.Id, unit.Id));
        var created = await Read<ViewModels.UnitUser>(response);

        Assert.EndsWith($"/api/unitusers/{created.Id}", response.Headers.Location.ToString());
    }

    /// <remarks>
    /// <c>createUnitUser</c> requires <c>ManageUnits</c> and its <c>Location</c> header names
    /// <c>getUnitUser</c>, which requires <c>ViewUnits</c> - so the caller who just added the user is
    /// answered 403 by the header they were handed. The third instance of this shape on the branch, after
    /// <c>createTeam</c> in <c>4dd5201</c> and <c>createUnit</c> in this unit.
    /// </remarks>
    [Fact]
    public async Task Create_WithManageUnitsOnly_CannotFollowItsOwnLocationHeader()
    {
        var unit = await SeedUnit();
        var member = await Actor().SeedAsync();
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageUnits).SeedAsync();

        var response = await Post(Client(actor), Body(member.Id, unit.Id));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var followed = await Client(actor).GetAsync(response.Headers.Location, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, followed.StatusCode);
    }

    [Fact]
    public async Task Create_WithoutManageUnits_Is403()
    {
        var unit = await SeedUnit();
        var member = await Actor().SeedAsync();
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewUnits).SeedAsync();

        var response = await Post(Client(actor), Body(member.Id, unit.Id));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(await StoredRows());
    }

    /// <remarks>
    /// The headline finding, first half. <c>requestedUser</c> is fetched and never read, so an unknown
    /// user id reaches Postgres as a foreign-key violation - a 500, with the response naming the
    /// constraint. Using the local the method already has, as
    /// <c>MselUnitService.CreateAsync</c> does with both of its parents, turns this into the 404 it
    /// should be and turns this test red.
    /// </remarks>
    [Fact]
    public async Task Create_ForAUserThatIsNotThere_Is500()
    {
        var unit = await SeedUnit();
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageUnits).SeedAsync();

        var response = await Post(Client(actor), Body(Guid.NewGuid(), unit.Id));

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Empty(await StoredRows());
    }

    /// <remarks>
    /// The same finding from the other side: <c>requestedUnit</c> is fetched and never read. Note both
    /// <c>FindAsync</c> calls also omit the <c>CancellationToken</c> the method was given.
    /// </remarks>
    [Fact]
    public async Task Create_ForAUnitThatIsNotThere_Is500()
    {
        var member = await Actor().SeedAsync();
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageUnits).SeedAsync();

        var response = await Post(Client(actor), Body(member.Id, Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Empty(await StoredRows());
    }

    /// <remarks>
    /// <c>(UserId, UnitId)</c> is uniquely indexed, so adding a person to a unit they are already in is a
    /// 500 from the database rather than the 409 - or the no-op - it should be. The comment above the two
    /// dead locals says this method exists to prevent duplicates.
    /// </remarks>
    [Fact]
    public async Task Create_ForAPairThatIsAlreadyThere_Is500()
    {
        var unit = await SeedUnit();
        var member = await Actor().SeedAsync();
        await Seed(BlueprintAppFactory.UnitUser(member.Id, unit.Id));
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageUnits).SeedAsync();

        var response = await Post(Client(actor), Body(member.Id, unit.Id));

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Single(await StoredRows());
    }

    [Fact]
    public async Task Create_KeepsTheBodysId()
    {
        var id = Guid.NewGuid();
        var unit = await SeedUnit();
        var member = await Actor().SeedAsync();
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageUnits).SeedAsync();

        var response = await Post(Client(actor), Body(member.Id, unit.Id) with { Id = id });

        Assert.Equal(id, (await Read<ViewModels.UnitUser>(response)).Id);
        Assert.NotNull(await Stored(id));
    }

    /// <remarks>
    /// The other half of <see cref="Get_AnswersAuditFieldsTheEntityDoesNotHave"/>: a hostile
    /// <c>createdBy</c> is neither stored nor echoed, because there is nowhere to store it. So the answer
    /// is the same zeros whether the client lies or not, and the two server-side assignments are
    /// unobservable. Giving the entity the columns would make the body's value matter, which is why the
    /// fix needs the assignments to stay.
    /// </remarks>
    [Fact]
    public async Task Create_RecordsNobodyAsHavingAddedTheUser()
    {
        var unit = await SeedUnit();
        var member = await Actor().SeedAsync();
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageUnits).SeedAsync();

        var response = await Post(Client(actor), Body(member.Id, unit.Id) with
        {
            CreatedBy = Guid.NewGuid(),
            DateCreated = new DateTime(1999, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        });

        var created = await Read<ViewModels.UnitUser>(response);

        Assert.Equal(Guid.Empty, created.CreatedBy);
        Assert.Equal(default, created.DateCreated);
    }

    /// <remarks>
    /// <c>UnitUserHandler.GetGroupsAsync</c> names the unit, the user, the admin data group and every
    /// MSEL the unit is assigned to - so a client watching an exercise is told when somebody joins a unit
    /// that contributes to it, which is the notification <c>MselUnitService</c> itself never sends
    /// (there is no <c>MselUnitHandler</c>).
    /// </remarks>
    [Fact]
    public async Task Create_BroadcastsToTheUnitTheUserAndEveryMselTheUnitIsOn()
    {
        var msel = await SeedMsel();
        var unit = await SeedUnit();
        await Seed(BlueprintAppFactory.MselUnit(unit.Id, msel.Id));
        var member = await Actor().SeedAsync();
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageUnits).SeedAsync();

        await Post(Client(actor), Body(member.Id, unit.Id));

        Assert.Equal(
            new[]
            {
                unit.Id.ToString(),
                member.Id.ToString(),
                MainHub.ADMIN_DATA_GROUP,
                msel.Id.ToString()
            }.Order(),
            Hub.Recipients(MainHubMethods.UnitUserCreated).Order());
    }

    [Fact]
    public async Task Create_WithNoBody_Is400()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageUnits).SeedAsync();

        using var content = new StringContent(string.Empty, Encoding.UTF8, "application/json");

        var response = await Client(actor).PostAsync(UnitUsers, content, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ---------------------------------------------------------------------------------------------
    // DELETE unitusers/{id}
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Delete_RemovesTheRowAndAnswers204()
    {
        var row = await SeedMembership();
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageUnits).SeedAsync();

        var response = await Client(actor).DeleteAsync(UnitUserRoute(row.Id), Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Null(await Stored(row.Id));
    }

    [Fact]
    public async Task Delete_ForAnIdThatIsNotThere_Is404()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageUnits).SeedAsync();

        var response = await Client(actor).DeleteAsync(UnitUserRoute(Guid.NewGuid()), Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Delete_WithoutManageUnits_Is403()
    {
        var row = await SeedMembership();
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewUnits).SeedAsync();

        var response = await Client(actor).DeleteAsync(UnitUserRoute(row.Id), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.NotNull(await Stored(row.Id));
    }

    [Fact]
    public async Task Delete_BroadcastsToTheUnitTheUserAndEveryMselTheUnitIsOn()
    {
        var msel = await SeedMsel();
        var unit = await SeedUnit();
        await Seed(BlueprintAppFactory.MselUnit(unit.Id, msel.Id));
        var member = await Actor().SeedAsync();
        var row = BlueprintAppFactory.UnitUser(member.Id, unit.Id);
        await Seed(row);
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageUnits).SeedAsync();

        await Client(actor).DeleteAsync(UnitUserRoute(row.Id), Ct);

        Assert.Equal(
            new[]
            {
                unit.Id.ToString(),
                member.Id.ToString(),
                MainHub.ADMIN_DATA_GROUP,
                msel.Id.ToString()
            }.Order(),
            Hub.Recipients(MainHubMethods.UnitUserDeleted).Order());
    }

    /// <remarks>
    /// Phase 2 established that a <c>UserMselRoleEntity</c> without a <c>UnitUserEntity</c> is a no-op in
    /// every requirement helper, because the role query is never reached unless the unit query already
    /// found the user. This is that finding reached through the API: removing a person from a unit is a
    /// 204 that silently revokes every role they hold on every MSEL the unit contributes to, leaving the
    /// rows behind so the exercise's member list still names them. Nothing warns, nothing is logged
    /// beyond the service's own "removed from unit" line, and re-adding them restores the roles as
    /// silently. Withdrawing the roles - or refusing while any exist - turns the second half of this
    /// test red.
    /// </remarks>
    [Fact]
    public async Task Delete_LeavesTheUsersMselRolesBehindWhereTheyGrantNothing()
    {
        var msel = await SeedMsel();
        var unit = await SeedUnit();
        await Seed(BlueprintAppFactory.MselUnit(unit.Id, msel.Id));
        var member = await Actor().SeedAsync();
        var row = BlueprintAppFactory.UnitUser(member.Id, unit.Id);
        await Seed(row);
        await Db.AddMselRoleAsync(member.Id, msel.Id, MselRole.Viewer, Ct);
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageUnits).SeedAsync();

        var before = await Client(member).GetAsync(MselUnitsOf(msel.Id), Ct);

        Assert.Equal(HttpStatusCode.OK, before.StatusCode);

        var response = await Client(actor).DeleteAsync(UnitUserRoute(row.Id), Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var after = await Client(member).GetAsync(MselUnitsOf(msel.Id), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, after.StatusCode);

        await using var context = NewContext();

        Assert.Single(await context.UserMselRoles.Where(x => x.MselId == msel.Id).ToListAsync(Ct));
    }

    // ---------------------------------------------------------------------------------------------
    // DELETE units/{unitId}/users/{userId}
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task DeleteByIds_RemovesTheRowAndAnswers204()
    {
        var unit = await SeedUnit();
        var member = await Actor().SeedAsync();
        var row = BlueprintAppFactory.UnitUser(member.Id, unit.Id);
        await Seed(row);
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageUnits).SeedAsync();

        var response = await Client(actor).DeleteAsync(MembershipRoute(unit.Id, member.Id), Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Null(await Stored(row.Id));
    }

    /// <remarks>
    /// The positive control. <c>UnitUserController.cs:143</c> passes <c>(unitId, userId)</c> to a method
    /// declared <c>(Guid unitId, Guid userId, …)</c>, so a caller who swaps them matches nothing and is
    /// answered 404 - which is what <c>DELETE teams/{teamId}/cards/{cardId}</c> answers for the
    /// <em>right</em> ids, because its call site transposes them. This test fails if anybody ever
    /// "tidies" the argument order here.
    /// </remarks>
    [Fact]
    public async Task DeleteByIds_WithTheIdsTheOtherWayRound_Is404()
    {
        var unit = await SeedUnit();
        var member = await Actor().SeedAsync();
        var row = BlueprintAppFactory.UnitUser(member.Id, unit.Id);
        await Seed(row);
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageUnits).SeedAsync();

        var response = await Client(actor).DeleteAsync(MembershipRoute(member.Id, unit.Id), Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.NotNull(await Stored(row.Id));
    }

    [Fact]
    public async Task DeleteByIds_ForAPairThatIsNotThere_Is404()
    {
        var unit = await SeedUnit();
        var member = await Actor().SeedAsync();
        await Seed(BlueprintAppFactory.UnitUser(member.Id, unit.Id));
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageUnits).SeedAsync();

        var response = await Client(actor).DeleteAsync(MembershipRoute(unit.Id, Guid.NewGuid()), Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Single(await StoredRows());
    }

    [Fact]
    public async Task DeleteByIds_WithoutManageUnits_Is403()
    {
        var unit = await SeedUnit();
        var member = await Actor().SeedAsync();
        await Seed(BlueprintAppFactory.UnitUser(member.Id, unit.Id));
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewUnits).SeedAsync();

        var response = await Client(actor).DeleteAsync(MembershipRoute(unit.Id, member.Id), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Single(await StoredRows());
    }

    /// <remarks>
    /// Neither delete has a self-service guard, so a caller holding <c>ManageUnits</c> may remove their
    /// own membership and with it their own access to every MSEL the unit is assigned to - the role rows
    /// stay behind and grant nothing (<see cref="Delete_LeavesTheUsersMselRolesBehindWhereTheyGrantNothing"/>),
    /// so there is no way back but somebody else re-adding them. The contrast worth reading is
    /// <c>UnitService.DeleteAsync</c>, which refuses to delete a <em>unit</em> whose id equals the
    /// caller's own user id "because it is your account" - a guard on the wrong id that can never fire,
    /// pinned by <c>UnitEndpointTests.Delete_ForTheCallersOwnUserId_Is403AboutAnAccount</c>. One route
    /// guards a case that cannot happen and its sibling does not guard the case that can.
    /// </remarks>
    [Fact]
    public async Task DeleteByIds_ForTheCallersOwnMembership_Is204()
    {
        var unit = await SeedUnit();
        var member = await Actor().WithSystemPermissions(SystemPermission.ManageUnits).SeedAsync();
        await Seed(BlueprintAppFactory.UnitUser(member.Id, unit.Id));

        var response = await Client(member).DeleteAsync(MembershipRoute(unit.Id, member.Id), Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Empty(await StoredRows());
    }

    // ---------------------------------------------------------------------------------------------
    // Authentication
    // ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("GET", "unitusers")]
    [InlineData("GET", "unitusers/00000000-0000-0000-0000-000000000001")]
    [InlineData("POST", "unitusers")]
    [InlineData("DELETE", "unitusers/00000000-0000-0000-0000-000000000001")]
    [InlineData("DELETE", "units/00000000-0000-0000-0000-000000000001/users/00000000-0000-0000-0000-000000000002")]
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

    private const string UnitUsers = "/api/unitusers";

    private static string UnitUserRoute(Guid id) => $"{UnitUsers}/{id}";

    private static string MembershipRoute(Guid unitId, Guid userId) =>
        $"/api/units/{unitId}/users/{userId}";

    /// <summary>
    /// The MSEL-scoped read of a unit assignment, used to show what a membership was carrying - it is the
    /// nearest route gated by <c>MselViewRequirement</c> alone, so it answers 403 the moment the unit
    /// membership behind a MSEL role disappears.
    /// </summary>
    private static string MselUnitsOf(Guid mselId) => $"/api/msels/{mselId}/mselunits";

    /// <summary>
    /// The wire shape of a unit user. The four audit properties are non-nullable where
    /// <c>ViewModels.Base</c> declares them so, because a body sending null for <c>createdBy</c> or
    /// <c>dateCreated</c> is a 400 that never reaches the controller.
    /// </summary>
    private sealed record UnitUserBody
    {
        public Guid Id { get; init; }
        public Guid UserId { get; init; }
        public Guid UnitId { get; init; }

        public Guid CreatedBy { get; init; }
        public DateTime DateCreated { get; init; }
        public Guid? ModifiedBy { get; init; }
        public DateTime? DateModified { get; init; }
    }

    private static UnitUserBody Body(Guid userId, Guid unitId) =>
        new() { UserId = userId, UnitId = unitId };

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

    private async Task<UnitUserEntity> SeedMembership()
    {
        var unit = await SeedUnit();
        var member = await Actor().SeedAsync();
        var row = BlueprintAppFactory.UnitUser(member.Id, unit.Id);
        await Seed(row);

        return row;
    }

    private Task<HttpResponseMessage> Post(HttpClient client, UnitUserBody body) =>
        client.PostAsJsonAsync(UnitUsers, body, Ct);

    private async Task<List<ViewModels.UnitUser>> GetRows(HttpClient client)
    {
        var response = await client.GetAsync(UnitUsers, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await Read<List<ViewModels.UnitUser>>(response);
    }

    private async Task<ViewModels.UnitUser> GetRow(HttpClient client, Guid id)
    {
        var response = await client.GetAsync(UnitUserRoute(id), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await Read<ViewModels.UnitUser>(response);
    }

    private async Task<UnitUserEntity> Stored(Guid id)
    {
        await using var context = NewContext();

        return await context.UnitUsers.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, Ct);
    }

    private async Task<List<UnitUserEntity>> StoredRows()
    {
        await using var context = NewContext();

        return await context.UnitUsers.AsNoTracking().ToListAsync(Ct);
    }

    private async Task<T> Read<T>(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync(Ct);

        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {body}");

        return JsonSerializer.Deserialize<T>(body, JsonOptions);
    }
}
