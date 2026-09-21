// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
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
/// <c>TeamUserService</c> / <c>TeamUserController</c> - the six routes behind the join row that puts a
/// user on a team, and the membership every <c>MselViewRequirement</c> and <c>MselUserRequirement</c>
/// check falls back on.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This is the well-written service in the tier, and saying so is the point of the file.</strong>
/// Every one of its six methods reads the stored parent before it decides anything: the two list routes
/// load the team (or scope the query through it), the single read loads the row then looks up its team's
/// MSEL, the create loads the team the body names and takes its permission decision from <em>that row's</em>
/// <c>MselId</c> rather than from the body, and both deletes <c>Include</c> the team. So none of the three
/// defect shapes that opened every other file in this tier is here - no request-body permission decision
/// (six instances elsewhere, most recently <c>TeamService.UpdateAsync</c>), no <c>SingleAsync</c> plus a
/// dead null check (four copies elsewhere), and an unknown id is a 404 on all four routes that take one.
/// <see cref="GetByTeam_ForATeamThatIsNotThere_IsA404ForAStrangerToo"/> is the contrast worth reading
/// beside <c>TeamService</c>'s <c>UpdateAsync</c>, where the same question is a 500.
/// </para>
/// <para>
/// <strong><c>DELETE teams/{teamId}/users/{userId}</c> passes its two ids in the right order</strong> -
/// <c>TeamUserController.cs:152</c> calls <c>DeleteByIdsAsync(teamId, userId, …)</c> against a signature
/// of <c>(Guid teamId, Guid userId, …)</c>. This is the positive control for the defect recorded in
/// <c>b5d2d86</c>, where <c>CardTeamController.cs:192</c> transposes the same shape and the route deletes
/// nothing for every well-formed request. Two same-typed ids either side of a call site is a defect no
/// compiler can find, so the working copy is worth a test of its own:
/// <see cref="DeleteByIds_PassesItsTwoIdsInTheOrderTheServiceExpects"/>.
/// </para>
/// <para>
/// <strong>The unit's two halves disagree about who may add a member.</strong> Creating a team wants
/// <c>EditMsels</c>-or-MSEL-owner (<c>TeamEndpointTests</c>); putting a user on it wants
/// <c>ManageUsers</c>-or-MSEL-owner; and granting that user a role on the team wants <c>EditMsels</c>
/// again (<c>UserTeamRoleEndpointTests</c>). One conceptual operation, three permissions, and a
/// <c>ManageUsers</c> holder with no role on any MSEL may put any user on any team in the installation.
/// </para>
/// <para>
/// <strong>The two reads also disagree with each other.</strong> Listing a MSEL's or a team's members
/// asks <c>MselUserRequirement</c>, which accepts membership of a unit the MSEL is assigned to with no
/// role at all; reading one of those same rows by id asks <c>MselViewRequirement</c>, which does not. So
/// a unit member may enumerate every member of every team on the MSEL and cannot read any one of them.
/// Both halves are asserted in <see cref="Get_ForAUnitMemberWithNoRole_Is403_ThoughTheyMayListTheSameRow"/>
/// so the contradiction is on one screen.
/// </para>
/// <para>
/// <strong><c>TeamUserEntity</c> is not a <c>BaseEntity</c>, but <c>ViewModels.TeamUser</c> derives from
/// <c>Base</c></strong> - so every one of these routes answers an all-zeros <c>createdBy</c> and a
/// <c>dateCreated</c> of <c>0001-01-01</c>, and nothing anywhere records who put a user on a team or
/// when. The controller and the service both assign <c>CreatedBy</c> on the way in
/// (<c>TeamUserController.cs:110</c>, <c>TeamUserService.cs:103</c>) and the map drops it, so both
/// assignments are dead code. See <see cref="Get_AnswersAuditFieldsTheEntityDoesNotHave"/>.
/// </para>
/// <para>
/// Per this branch's rule, every test characterizes rather than fixes, and says what fixing it will do to
/// the test.
/// </para>
/// </remarks>
public class TeamUserEndpointTests(DatabaseFixture fixture, BlueprintAppFactory factory)
    : ApiTestBase(fixture, factory), IClassFixture<BlueprintAppFactory>
{
    // ---------------------------------------------------------------------------------------------
    // GET msels/{mselId}/teamusers
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task GetByMsel_ReturnsEveryTeamUserOnTheMsel()
    {
        var msel = await SeedMsel();
        var first = await SeedTeam(msel);
        var second = await SeedTeam(msel);
        var one = await Actor().OnTeam(first).SeedAsync();
        var two = await Actor().OnTeam(second).SeedAsync();
        var actor = await Actor().OnMsel(msel, MselRole.Viewer).SeedAsync();

        var rows = await GetRows(Client(actor), TeamUsersOf(msel.Id));

        Assert.Equal(new[] { one.Id, two.Id }.Order(), rows.Select(x => x.UserId).Order());
    }

    [Fact]
    public async Task GetByMsel_DoesNotReturnAnotherMselsTeamUsers()
    {
        var msel = await SeedMsel();
        var mine = await SeedTeam(msel);
        var member = await Actor().OnTeam(mine).SeedAsync();
        await Actor().OnTeam(await SeedTeam(await SeedMsel())).SeedAsync();
        var actor = await Actor().OnMsel(msel, MselRole.Viewer).SeedAsync();

        var rows = await GetRows(Client(actor), TeamUsersOf(msel.Id));

        Assert.Equal(member.Id, Assert.Single(rows).UserId);
    }

    [Fact]
    public async Task GetByMsel_WithViewMselsAndNoRoleOnTheMsel_Is200()
    {
        var msel = await SeedMsel();
        await Actor().OnTeam(await SeedTeam(msel)).SeedAsync();
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewMsels).SeedAsync();

        Assert.Single(await GetRows(Client(actor), TeamUsersOf(msel.Id)));
    }

    [Fact]
    public async Task GetByMsel_ForACallerWithNoRoleOnTheMsel_Is403()
    {
        var msel = await SeedMsel();
        var actor = await Actor().SeedAsync();

        var response = await Client(actor).GetAsync(TeamUsersOf(msel.Id), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <remarks>
    /// <c>MselUserRequirement</c> is the only one of the eight helpers that accepts membership of a unit
    /// the MSEL is assigned to on its own, with no <c>UserMselRoleEntity</c> - the minimum this route
    /// admits. Note what the caller may then enumerate: every member of every team on the MSEL, by user
    /// id. Swapping the helper for <c>MselViewRequirement</c>, as the single read uses, turns this red.
    /// </remarks>
    [Fact]
    public async Task GetByMsel_ForAUnitMemberWithNoRole_Is200()
    {
        var msel = await SeedMsel();
        await Actor().OnTeam(await SeedTeam(msel)).SeedAsync();
        var actor = await Actor().SeedAsync();
        await Db.AddUnitMembershipAsync(actor.Id, msel.Id, Ct);

        Assert.Single(await GetRows(Client(actor), TeamUsersOf(msel.Id)));
    }

    /// <remarks>
    /// <c>MselUserRequirement</c> dereferences its <c>FirstOrDefaultAsync</c> with no null guard, so an
    /// unknown MSEL id is a 500 for an ordinary caller and an empty list for a <c>ViewMsels</c> holder
    /// whose permission short-circuits the check. "Is this MSEL there" therefore has two answers
    /// depending on who asks - the shape recorded for <c>MoveService</c> in <c>997a9c4</c> and for
    /// <c>TeamService.GetByMselAsync</c> in this unit's first file. A null guard turns the first half into
    /// a 403 and leaves the second alone.
    /// </remarks>
    [Fact]
    public async Task GetByMsel_ForAMselThatIsNotThere_Is500()
    {
        var stranger = await Actor().SeedAsync();
        var privileged = await Actor().WithSystemPermissions(SystemPermission.ViewMsels).SeedAsync();

        var response = await Client(stranger).GetAsync(TeamUsersOf(Guid.NewGuid()), Ct);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Empty(await GetRows(Client(privileged), TeamUsersOf(Guid.NewGuid())));
    }

    // ---------------------------------------------------------------------------------------------
    // GET teams/{teamId}/teamusers
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task GetByTeam_ReturnsTheTeamsMembers()
    {
        var msel = await SeedMsel();
        var team = await SeedTeam(msel);
        var one = await Actor().OnTeam(team).SeedAsync();
        var two = await Actor().OnTeam(team).SeedAsync();
        var actor = await Actor().OnMsel(msel, MselRole.Viewer).SeedAsync();

        var rows = await GetRows(Client(actor), TeamUsersOfTeam(team.Id));

        Assert.Equal(new[] { one.Id, two.Id }.Order(), rows.Select(x => x.UserId).Order());
        Assert.All(rows, row => Assert.Equal(team.Id, row.TeamId));
    }

    [Fact]
    public async Task GetByTeam_DoesNotReturnAnotherTeamsMembers()
    {
        var msel = await SeedMsel();
        var mine = await SeedTeam(msel);
        var member = await Actor().OnTeam(mine).SeedAsync();
        await Actor().OnTeam(await SeedTeam(msel)).SeedAsync();
        var actor = await Actor().OnMsel(msel, MselRole.Viewer).SeedAsync();

        var rows = await GetRows(Client(actor), TeamUsersOfTeam(mine.Id));

        Assert.Equal(member.Id, Assert.Single(rows).UserId);
    }

    [Fact]
    public async Task GetByTeam_ForATeamWithNoMembers_IsAnEmptyList()
    {
        var msel = await SeedMsel();
        var team = await SeedTeam(msel);
        var actor = await Actor().OnMsel(msel, MselRole.Viewer).SeedAsync();

        Assert.Empty(await GetRows(Client(actor), TeamUsersOfTeam(team.Id)));
    }

    [Fact]
    public async Task GetByTeam_ForATeamThatIsNotThere_Is404()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewMsels).SeedAsync();

        var response = await Client(actor).GetAsync(TeamUsersOfTeam(Guid.NewGuid()), Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <remarks>
    /// The existence check runs <em>before</em> the permission check, so an unknown team is a 404 whoever
    /// asks - which leaks that a team id is unused, and is also the shape every other service in this
    /// tier should have copied. Compare <c>TeamService.UpdateAsync</c>, where the permission check runs
    /// first and a stranger naming an unknown MSEL gets <c>MselOwnerRequirement</c>'s 500. Moving the
    /// permission check above the existence check turns this into a 403 and reddens this test.
    /// </remarks>
    [Fact]
    public async Task GetByTeam_ForATeamThatIsNotThere_IsA404ForAStrangerToo()
    {
        var actor = await Actor().SeedAsync();

        var response = await Client(actor).GetAsync(TeamUsersOfTeam(Guid.NewGuid()), Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetByTeam_WithViewMselsAndNoRoleOnTheMsel_Is200()
    {
        var msel = await SeedMsel();
        var team = await SeedTeam(msel);
        await Actor().OnTeam(team).SeedAsync();
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewMsels).SeedAsync();

        Assert.Single(await GetRows(Client(actor), TeamUsersOfTeam(team.Id)));
    }

    [Fact]
    public async Task GetByTeam_ForACallerWithNoRoleOnTheMsel_Is403()
    {
        var msel = await SeedMsel();
        var team = await SeedTeam(msel);
        var actor = await Actor().SeedAsync();

        var response = await Client(actor).GetAsync(TeamUsersOfTeam(team.Id), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <remarks>
    /// <c>MselUserRequirement</c> accepts team membership, so a participant may list their own team's
    /// members - and, because the requirement is asked about the <em>MSEL</em> rather than the team, they
    /// may list every other team on the MSEL too. Membership of one team is read access to all of them.
    /// Scoping the check to the team in the route turns the second assertion red.
    /// </remarks>
    [Fact]
    public async Task GetByTeam_ForAMemberOfOneTeam_ListsEveryTeamOnTheMsel()
    {
        var msel = await SeedMsel();
        var mine = await SeedTeam(msel);
        var theirs = await SeedTeam(msel);
        var other = await Actor().OnTeam(theirs).SeedAsync();
        var actor = await Actor().OnTeam(mine).SeedAsync();

        Assert.Equal(actor.Id, Assert.Single(await GetRows(Client(actor), TeamUsersOfTeam(mine.Id))).UserId);
        Assert.Equal(other.Id, Assert.Single(await GetRows(Client(actor), TeamUsersOfTeam(theirs.Id))).UserId);
    }

    // ---------------------------------------------------------------------------------------------
    // GET teamusers/{id}
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Get_ReturnsTheTeamUser()
    {
        var msel = await SeedMsel();
        var team = await SeedTeam(msel);
        var member = await Actor().OnTeam(team).SeedAsync();
        var row = await StoredFor(member.Id, team.Id);
        var actor = await Actor().OnMsel(msel, MselRole.Viewer).SeedAsync();

        var answered = await GetRow(Client(actor), row.Id);

        Assert.Equal(row.Id, answered.Id);
        Assert.Equal(member.Id, answered.UserId);
        Assert.Equal(team.Id, answered.TeamId);
    }

    [Fact]
    public async Task Get_ForAnIdThatIsNotThere_Is404()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewMsels).SeedAsync();

        var response = await Client(actor).GetAsync(TeamUserRoute(Guid.NewGuid()), Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Get_WithViewMselsAndNoRoleOnTheMsel_Is200()
    {
        var msel = await SeedMsel();
        var team = await SeedTeam(msel);
        var member = await Actor().OnTeam(team).SeedAsync();
        var row = await StoredFor(member.Id, team.Id);
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewMsels).SeedAsync();

        Assert.Equal(row.Id, (await GetRow(Client(actor), row.Id)).Id);
    }

    [Fact]
    public async Task Get_ForACallerWithNoRoleOnTheMsel_Is403()
    {
        var msel = await SeedMsel();
        var team = await SeedTeam(msel);
        var member = await Actor().OnTeam(team).SeedAsync();
        var row = await StoredFor(member.Id, team.Id);
        var actor = await Actor().SeedAsync();

        var response = await Client(actor).GetAsync(TeamUserRoute(row.Id), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <remarks>
    /// The disagreement between the two reads, on one screen: the same caller lists the MSEL's team-user
    /// rows and is refused the one row the list just answered. <c>GetByMselAsync</c> asks
    /// <c>MselUserRequirement</c> (unit membership alone is enough) and <c>GetAsync</c> asks
    /// <c>MselViewRequirement</c> (a role is needed), and nothing in either controller action hints at the
    /// difference - both resolve exactly <c>ViewMsels</c>. Making the two agree, either way, turns one
    /// half of this test red.
    /// </remarks>
    [Fact]
    public async Task Get_ForAUnitMemberWithNoRole_Is403_ThoughTheyMayListTheSameRow()
    {
        var msel = await SeedMsel();
        var team = await SeedTeam(msel);
        var member = await Actor().OnTeam(team).SeedAsync();
        var row = await StoredFor(member.Id, team.Id);
        var actor = await Actor().SeedAsync();
        await Db.AddUnitMembershipAsync(actor.Id, msel.Id, Ct);

        var listed = await GetRows(Client(actor), TeamUsersOf(msel.Id));

        Assert.Equal(row.Id, Assert.Single(listed).Id);
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await Client(actor).GetAsync(TeamUserRoute(row.Id), Ct)).StatusCode);
    }

    /// <remarks>
    /// <c>MselViewRequirement</c> accepts team membership, so a participant may read their own row - the
    /// one thing a team user may do with this family of routes.
    /// </remarks>
    [Fact]
    public async Task Get_ForTheMemberTheRowNames_Is200()
    {
        var msel = await SeedMsel();
        var team = await SeedTeam(msel);
        var actor = await Actor().OnTeam(team).SeedAsync();
        var row = await StoredFor(actor.Id, team.Id);

        Assert.Equal(row.Id, (await GetRow(Client(actor), row.Id)).Id);
    }

    /// <remarks>
    /// <c>TeamUserEntity</c> has no <c>CreatedBy</c>, <c>DateCreated</c>, <c>ModifiedBy</c> or
    /// <c>DateModified</c> - it is not a <c>BaseEntity</c> - while <c>ViewModels.TeamUser</c> derives from
    /// <c>Base</c> and declares all four. So the wire carries an all-zeros creator and a year-one date on
    /// every team-user route, and the two assignments on the way in
    /// (<c>TeamUserController.cs:110</c> and <c>TeamUserService.cs:103</c>) are dead code. Nothing records
    /// who put a user on a team: adding the four columns turns this red, and dropping the four properties
    /// from the view model turns it into a compile error, which is the honest fix.
    /// </remarks>
    [Fact]
    public async Task Get_AnswersAuditFieldsTheEntityDoesNotHave()
    {
        var msel = await SeedMsel();
        var team = await SeedTeam(msel);
        var member = await Actor().OnTeam(team).SeedAsync();
        var row = await StoredFor(member.Id, team.Id);
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewMsels).SeedAsync();

        var answered = await GetRow(Client(actor), row.Id);

        Assert.Equal(Guid.Empty, answered.CreatedBy);
        Assert.Equal(default(DateTime), answered.DateCreated);
        Assert.Null(answered.ModifiedBy);
        Assert.Null(answered.DateModified);
    }

    // ---------------------------------------------------------------------------------------------
    // POST teamusers
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Create_StoresTheRowAndAnswers201()
    {
        var msel = await SeedMsel();
        var team = await SeedTeam(msel);
        var member = await Actor().SeedAsync();
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        var response = await Post(Client(actor), Body(member.Id, team.Id));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var created = await Read<ViewModels.TeamUser>(response);

        Assert.EndsWith($"/api/teamusers/{created.Id}", response.Headers.Location?.ToString());
        Assert.Equal(member.Id, created.UserId);
        Assert.NotNull(await StoredFor(member.Id, team.Id));
    }

    [Fact]
    public async Task Create_ForATeamThatIsNotThere_Is404()
    {
        var member = await Actor().SeedAsync();
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageUsers).SeedAsync();

        var response = await Post(Client(actor), Body(member.Id, Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <remarks>
    /// The minimum: <c>ManageUsers</c> and nothing else. The service never looks at the caller's relation
    /// to the MSEL once the permission is there, so this holder puts any user on any team in the
    /// installation - including a user who is in no unit the MSEL is assigned to, which
    /// <see cref="Create_ForAUserInNoUnitOfTheMsel_Is201"/> pins separately.
    /// </remarks>
    [Fact]
    public async Task Create_WithManageUsersAndNoRoleOnTheMsel_Is201()
    {
        var team = await SeedTeam(await SeedMsel());
        var member = await Actor().SeedAsync();
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageUsers).SeedAsync();

        var response = await Post(Client(actor), Body(member.Id, team.Id));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    /// <remarks>
    /// The other branch: <c>MselOwnerRequirement</c> against the <em>stored</em> team's <c>MselId</c>.
    /// Note that the MSEL's <c>Editor</c> is refused, as in <c>TeamEndpointTests</c> - the helper accepts
    /// the creator and the <c>Owner</c> role only.
    /// </remarks>
    [Theory]
    [InlineData(MselRole.Owner, HttpStatusCode.Created)]
    [InlineData(MselRole.Editor, HttpStatusCode.Forbidden)]
    [InlineData(MselRole.Viewer, HttpStatusCode.Forbidden)]
    public async Task Create_ForEachMselRole(MselRole role, HttpStatusCode expected)
    {
        var msel = await SeedMsel();
        var team = await SeedTeam(msel);
        var member = await Actor().SeedAsync();
        var actor = await Actor().OnMsel(msel, role).SeedAsync();

        var response = await Post(Client(actor), Body(member.Id, team.Id));

        Assert.Equal(expected, response.StatusCode);
    }

    [Fact]
    public async Task Create_ForACallerWithNoRoleOnTheMsel_Is403()
    {
        var msel = await SeedMsel();
        var team = await SeedTeam(msel);
        var member = await Actor().SeedAsync();
        var actor = await Actor().SeedAsync();

        var response = await Post(Client(actor), Body(member.Id, team.Id));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Null(await StoredFor(member.Id, team.Id));
    }

    /// <remarks>
    /// <c>(UserId, TeamId)</c> is uniquely indexed, so a second row for one pair is a 500 from the
    /// database rather than the 409 a conflict deserves - or the 200 an idempotent membership route could
    /// reasonably answer. The UI cannot tell this apart from the server being broken.
    /// </remarks>
    [Fact]
    public async Task Create_ForAUserAlreadyOnTheTeam_Is500()
    {
        var msel = await SeedMsel();
        var team = await SeedTeam(msel);
        var member = await Actor().OnTeam(team).SeedAsync();
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        var response = await Post(Client(actor), Body(member.Id, team.Id));

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    /// <remarks>
    /// Nothing checks that the user exists, so an unknown id is a foreign-key violation and a 500 where
    /// the team's own 404 two lines above is the obvious model. Adding the same
    /// <c>EntityNotFoundException</c> for the user turns this red.
    /// </remarks>
    [Fact]
    public async Task Create_ForAUserThatIsNotThere_Is500()
    {
        var msel = await SeedMsel();
        var team = await SeedTeam(msel);
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        var response = await Post(Client(actor), Body(Guid.NewGuid(), team.Id));

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    /// <remarks>
    /// A team belongs to a MSEL, a MSEL is assigned to units, and a unit holds users - and none of that
    /// is enforced here. So a user with no relation to the MSEL becomes a participant in its exercise,
    /// and the MSEL's own membership queries then answer them alongside everybody else.
    /// </remarks>
    [Fact]
    public async Task Create_ForAUserInNoUnitOfTheMsel_Is201()
    {
        var msel = await SeedMsel();
        var team = await SeedTeam(msel);
        var member = await Actor().OnMsel(await SeedMsel(), MselRole.Viewer).SeedAsync();
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        var response = await Post(Client(actor), Body(member.Id, team.Id));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    /// <remarks>
    /// <c>TeamUserProfile</c> is a bare bidirectional map and the service keeps a non-empty body
    /// <c>Id</c>, so a client chooses the primary key. Combined with the unique index on
    /// <c>(UserId, TeamId)</c> there are two ways for one create to be a 500.
    /// </remarks>
    [Fact]
    public async Task Create_KeepsTheBodysId()
    {
        var msel = await SeedMsel();
        var team = await SeedTeam(msel);
        var member = await Actor().SeedAsync();
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();
        var id = Guid.NewGuid();

        var created = await Read<ViewModels.TeamUser>(
            await Post(Client(actor), Body(member.Id, team.Id) with { Id = id }));

        Assert.Equal(id, created.Id);
    }

    /// <remarks>
    /// <c>TeamUserService</c> has no <c>ServiceUtilities.SetMselModifiedAsync</c> call on either of its
    /// writes, matching <c>TeamService</c> and unlike <c>UserTeamRoleService</c> - so adding a
    /// participant to an exercise leaves the MSEL reporting that it has never been modified.
    /// </remarks>
    [Fact]
    public async Task Create_LeavesTheMselUnmodified()
    {
        var msel = await SeedMsel();
        var team = await SeedTeam(msel);
        var member = await Actor().SeedAsync();
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        await Post(Client(actor), Body(member.Id, team.Id));

        Assert.Null((await StoredMsel(msel.Id)).DateModified);
    }

    /// <remarks>
    /// <c>TeamUserHandler.GetGroups</c> names the team, <em>the user</em> and the admin data group - the
    /// only handler in the tier that tells the subject of a row about it, which is what lets a client
    /// learn it has been added to a team it could not previously see. Note the handler re-reads the row
    /// with <c>.Include(tu =&gt; tu.User)</c> and then dereferences <c>teamUser.Team.Msel</c>, relying on
    /// change-tracker fix-up to have supplied <c>Team</c> from the <c>FindAsync</c> the create did two
    /// steps earlier; it would be a <c>NullReferenceException</c> on a context that had not touched the
    /// team. Fifth appearance of that mechanism on this branch.
    /// </remarks>
    [Fact]
    public async Task Create_BroadcastsTeamUserCreatedToTheTeamTheUserAndTheAdmins()
    {
        var msel = await SeedMsel();
        var team = await SeedTeam(msel);
        var member = await Actor().SeedAsync();
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        await Post(Client(actor), Body(member.Id, team.Id));

        Assert.Equal(
            new[] { MainHub.ADMIN_DATA_GROUP, member.Id.ToString(), team.Id.ToString() }.Order(),
            Hub.Recipients(MainHubMethods.TeamUserCreated).Order());
    }

    // ---------------------------------------------------------------------------------------------
    // DELETE teamusers/{id}
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Delete_RemovesTheRowAndAnswers204()
    {
        var msel = await SeedMsel();
        var team = await SeedTeam(msel);
        var member = await Actor().OnTeam(team).SeedAsync();
        var row = await StoredFor(member.Id, team.Id);
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        var response = await Client(actor).DeleteAsync(TeamUserRoute(row.Id), Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Null(await StoredFor(member.Id, team.Id));
        Assert.Equal(
            new[] { MainHub.ADMIN_DATA_GROUP, member.Id.ToString(), team.Id.ToString() }.Order(),
            Hub.Recipients(MainHubMethods.TeamUserDeleted).Order());
    }

    [Fact]
    public async Task Delete_ForAnIdThatIsNotThere_Is404()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageUsers).SeedAsync();

        var response = await Client(actor).DeleteAsync(TeamUserRoute(Guid.NewGuid()), Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Delete_WithManageUsersAndNoRoleOnTheMsel_Is204()
    {
        var team = await SeedTeam(await SeedMsel());
        var member = await Actor().OnTeam(team).SeedAsync();
        var row = await StoredFor(member.Id, team.Id);
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageUsers).SeedAsync();

        var response = await Client(actor).DeleteAsync(TeamUserRoute(row.Id), Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    /// <remarks>
    /// A participant cannot take themselves off a team, and the MSEL's <c>Editor</c> cannot take them
    /// off either - the delete asks <c>MselOwnerRequirement</c>, from the stored row's team. Both cases
    /// are 403 here.
    /// </remarks>
    [Fact]
    public async Task Delete_ForACallerWithNoRoleOnTheMsel_Is403()
    {
        var msel = await SeedMsel();
        var team = await SeedTeam(msel);
        var member = await Actor().OnTeam(team).SeedAsync();
        var row = await StoredFor(member.Id, team.Id);
        var editor = await Actor().OnMsel(msel, MselRole.Editor).SeedAsync();

        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await Client(member).DeleteAsync(TeamUserRoute(row.Id), Ct)).StatusCode);
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await Client(editor).DeleteAsync(TeamUserRoute(row.Id), Ct)).StatusCode);
        Assert.NotNull(await StoredFor(member.Id, team.Id));
    }

    /// <remarks>
    /// The permission decision comes from the stored row's team, reached through an <c>Include</c>, so
    /// owning another MSEL grants nothing - the shape <c>TeamService.UpdateAsync</c> should have copied.
    /// </remarks>
    [Fact]
    public async Task Delete_TakesItsPermissionDecisionFromTheStoredRow()
    {
        var team = await SeedTeam(await SeedMsel());
        var member = await Actor().OnTeam(team).SeedAsync();
        var row = await StoredFor(member.Id, team.Id);
        var actor = await Actor().OnMsel(await SeedMsel(), MselRole.Owner).SeedAsync();

        var response = await Client(actor).DeleteAsync(TeamUserRoute(row.Id), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.NotNull(await StoredFor(member.Id, team.Id));
    }

    // ---------------------------------------------------------------------------------------------
    // DELETE teams/{teamId}/users/{userId}
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task DeleteByIds_RemovesTheRowAndAnswers204()
    {
        var msel = await SeedMsel();
        var team = await SeedTeam(msel);
        var member = await Actor().OnTeam(team).SeedAsync();
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        var response = await Client(actor).DeleteAsync(MembershipRoute(team.Id, member.Id), Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Null(await StoredFor(member.Id, team.Id));
    }

    /// <remarks>
    /// <para>
    /// The positive control for <c>b5d2d86</c>'s worst finding. <c>CardTeamController.cs:192</c> calls
    /// <c>DeleteByIdsAsync(teamId, cardId, ct)</c> against a signature of <c>(Guid cardId, Guid teamId,
    /// …)</c>, so that route deletes nothing for every well-formed request and answers 204 only when the
    /// caller transposes its ids. <c>TeamUserController.cs:152</c> gets the same shape right, and this
    /// test is what would notice if somebody "tidied" it: the correct pair is a 204, and the transposed
    /// pair is a 404.
    /// </para>
    /// <para>
    /// Both parameters are <c>Guid</c>, so nothing about the mistake is a compile error and no type test
    /// can find it. The second assertion here is the only kind that can.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task DeleteByIds_PassesItsTwoIdsInTheOrderTheServiceExpects()
    {
        var msel = await SeedMsel();
        var team = await SeedTeam(msel);
        var member = await Actor().OnTeam(team).SeedAsync();
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        Assert.Equal(
            HttpStatusCode.NotFound,
            (await Client(actor).DeleteAsync(MembershipRoute(member.Id, team.Id), Ct)).StatusCode);
        Assert.NotNull(await StoredFor(member.Id, team.Id));
        Assert.Equal(
            HttpStatusCode.NoContent,
            (await Client(actor).DeleteAsync(MembershipRoute(team.Id, member.Id), Ct)).StatusCode);
        Assert.Null(await StoredFor(member.Id, team.Id));
    }

    [Fact]
    public async Task DeleteByIds_ForAPairThatIsNotThere_Is404()
    {
        var team = await SeedTeam(await SeedMsel());
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageUsers).SeedAsync();

        var response = await Client(actor).DeleteAsync(MembershipRoute(team.Id, Guid.NewGuid()), Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task DeleteByIds_WithManageUsersAndNoRoleOnTheMsel_Is204()
    {
        var team = await SeedTeam(await SeedMsel());
        var member = await Actor().OnTeam(team).SeedAsync();
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageUsers).SeedAsync();

        var response = await Client(actor).DeleteAsync(MembershipRoute(team.Id, member.Id), Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task DeleteByIds_ForACallerWithNoRoleOnTheMsel_Is403()
    {
        var msel = await SeedMsel();
        var team = await SeedTeam(msel);
        var member = await Actor().OnTeam(team).SeedAsync();

        var response = await Client(member).DeleteAsync(MembershipRoute(team.Id, member.Id), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.NotNull(await StoredFor(member.Id, team.Id));
    }

    // ---------------------------------------------------------------------------------------------
    // Authentication
    // ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("GET", "msels/00000000-0000-0000-0000-000000000001/teamusers")]
    [InlineData("GET", "teams/00000000-0000-0000-0000-000000000001/teamusers")]
    [InlineData("GET", "teamusers/00000000-0000-0000-0000-000000000001")]
    [InlineData("POST", "teamusers")]
    [InlineData("DELETE", "teamusers/00000000-0000-0000-0000-000000000001")]
    [InlineData("DELETE", "teams/00000000-0000-0000-0000-000000000001/users/00000000-0000-0000-0000-000000000002")]
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

    private const string TeamUsers = "/api/teamusers";

    private static string TeamUserRoute(Guid id) => $"{TeamUsers}/{id}";

    private static string TeamUsersOf(Guid mselId) => $"/api/msels/{mselId}/teamusers";

    private static string TeamUsersOfTeam(Guid teamId) => $"/api/teams/{teamId}/teamusers";

    /// <summary>
    /// <c>DELETE teams/{teamId}/users/{userId}</c>, spelled in the route's own order. Taking the two ids
    /// in that order is deliberate: <see cref="DeleteByIds_PassesItsTwoIdsInTheOrderTheServiceExpects"/>
    /// calls this helper both ways round, and a helper named for the ids rather than for the route would
    /// have hidden which way round it was.
    /// </summary>
    private static string MembershipRoute(Guid teamId, Guid userId) =>
        $"/api/teams/{teamId}/users/{userId}";

    /// <summary>
    /// The wire shape of a team user. All three ids are writable through <c>TeamUserProfile</c>'s bare
    /// bidirectional map, and the four audit fields exist on the view model and nowhere else.
    /// </summary>
    private sealed record TeamUserBody
    {
        public Guid Id { get; init; }
        public Guid UserId { get; init; }
        public Guid TeamId { get; init; }

        public Guid CreatedBy { get; init; }
        public DateTime DateCreated { get; init; }
        public Guid? ModifiedBy { get; init; }
        public DateTime? DateModified { get; init; }
    }

    private static TeamUserBody Body(Guid userId, Guid teamId) => new()
    {
        UserId = userId,
        TeamId = teamId
    };

    private async Task<MselEntity> SeedMsel()
    {
        var msel = BlueprintAppFactory.Msel();
        await Seed(msel);

        return msel;
    }

    private async Task<TeamEntity> SeedTeam(MselEntity msel)
    {
        var team = BlueprintAppFactory.Team(msel.Id);
        await Seed(team);

        return team;
    }

    private Task<HttpResponseMessage> Post(HttpClient client, TeamUserBody body) =>
        client.PostAsJsonAsync(TeamUsers, body, Ct);

    private async Task<List<ViewModels.TeamUser>> GetRows(HttpClient client, string url)
    {
        var response = await client.GetAsync(url, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await Read<List<ViewModels.TeamUser>>(response);
    }

    private async Task<ViewModels.TeamUser> GetRow(HttpClient client, Guid id)
    {
        var response = await client.GetAsync(TeamUserRoute(id), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await Read<ViewModels.TeamUser>(response);
    }

    /// <summary>
    /// The stored join row for one pair, or null. <c>TestActorBuilder.OnTeam</c> writes the row with an
    /// id of its own choosing and does not hand it back, so every test that needs the id reads it here.
    /// </summary>
    private async Task<TeamUserEntity> StoredFor(Guid userId, Guid teamId)
    {
        await using var context = NewContext();

        return await context.TeamUsers
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.UserId == userId && x.TeamId == teamId, Ct);
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
