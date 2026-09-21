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
/// <c>TeamService</c> / <c>TeamController</c> - the eight routes behind a MSEL's teams, the unit of
/// participation every other MSEL-scoped table is eventually filed under.
/// </summary>
/// <remarks>
/// <para>
/// A team belongs to exactly one MSEL - <c>TeamEntity.MselId</c> is a non-nullable foreign key with a
/// cascade delete, unlike the nullable parent every other row in this tier carries - so this file has no
/// "template" half and no detach escalation to characterize. What it has instead is eight routes that
/// disagree about who may use them: <c>GET my-teams</c> asks the caller for nothing at all,
/// <c>GET teams/{id}</c> asks for <c>ViewMsels</c> and nothing else, <c>GET users/{userId}/teams</c> asks
/// for <c>ManageUsers</c>, and the four writes ask for <c>EditMsels</c>-or-MSEL-owner. Per the thinner
/// half of Phase 3 item 6 this is a quartet per endpoint - happy path, not-found,
/// allowed-with-the-minimum, forbidden-without - plus the characterizations below.
/// </para>
/// <para>
/// <strong><c>GET teams/{id}</c> is not MSEL-scoped at all.</strong> The controller requires
/// <c>ViewMsels</c> outright and the service checks nothing, so one <c>ViewMsels</c> holder reads every
/// team in the installation together with its member list, while the MSEL's own owner - who may create,
/// rename and delete the team - cannot read it by id without that permission. The route's own remarks
/// promise "a User that is a member of a Team within the specified Team", which is not implemented. See
/// <see cref="Get_WithViewMselsAndNoRoleOnAnyMsel_ReadsAnyTeam"/> and
/// <see cref="Get_ForTheMselsOwnerWithoutViewMsels_Is403"/>.
/// </para>
/// <para>
/// <strong>Whether a MSEL exists depends on who asks.</strong> <c>GetByMselAsync</c>'s template
/// fall-through dereferences a <c>FindAsync</c> with no null guard (and no <c>CancellationToken</c>), so
/// an unknown MSEL id is a 500 for an ordinary caller and an empty list for a <c>ViewMsels</c> holder -
/// the shape recorded for <c>MoveService</c> in <c>997a9c4</c>, reached here through a different helper.
/// See <see cref="GetByMsel_ForAMselThatIsNotThere_Is500"/>.
/// </para>
/// <para>
/// <strong><c>UpdateAsync</c> takes its permission decision from the request body's <c>MselId</c></strong>
/// - the sixth instance of that shape in the branch, after <c>OrganizationService</c>,
/// <c>ScenarioEventService</c>, <c>DataOptionService</c>, <c>MoveService</c> and <c>CardService</c> - but it
/// is the first one with a guard against the steal that follows, at <c>TeamService.cs:173</c>. The order is
/// what is left to characterize: the permission check runs <em>before</em> the guard, so a caller who owns
/// any MSEL at all reaches the guard for every team of every other one and is answered 500 by an
/// <c>ArgumentException</c> rather than the 403 the check should have produced or the 400 a bad request
/// deserves. A body that simply omits the id is the same 500. See
/// <see cref="Update_ChoosesItsPermissionBranchFromTheRequestBodyAndThenRefusesTheSteal"/>.
/// </para>
/// <para>
/// <strong>Nothing here marks the MSEL modified.</strong> None of the four write paths calls
/// <c>ServiceUtilities.SetMselModifiedAsync</c>, so adding, renaming or deleting a team leaves the MSEL's
/// <c>DateModified</c> exactly as it was - the fourth service in this tier with that gap, after
/// <c>CardService</c>, <c>CardTeamService</c> and <c>InjectService</c>, and the contrast is
/// <c>UserTeamRoleService</c>, which does call it on both of its writes
/// (<c>UserTeamRoleEndpointTests</c>).
/// </para>
/// <para>
/// <strong>The same view model crosses the same API with and without its members.</strong> Only the two
/// routes that <c>Include</c> the membership - <c>GET msels/{mselId}/teams</c> and <c>GET teams/{id}</c> -
/// answer a populated <c>users</c>; <c>GET my-teams</c> and <c>GET users/{userId}/teams</c> project the
/// team out of <c>TeamUsers</c> and answer an empty one. Fifth appearance of this shape on the branch,
/// after <c>InjectTypeService</c>'s reads.
/// </para>
/// <para>
/// Per this branch's rule, every test characterizes rather than fixes, and says what fixing it will do to
/// the test.
/// </para>
/// </remarks>
public class TeamEndpointTests(DatabaseFixture fixture, BlueprintAppFactory factory)
    : ApiTestBase(fixture, factory), IClassFixture<BlueprintAppFactory>
{
    // ---------------------------------------------------------------------------------------------
    // GET my-teams
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task GetMine_ReturnsTheTeamsTheCallerIsOn()
    {
        var msel = await SeedMsel();
        var mine = await SeedTeam(msel);
        var other = await SeedTeam(msel);
        var actor = await Actor().OnTeam(mine).OnTeam(other).SeedAsync();

        var teams = await GetTeams(Client(actor), MyTeams);

        Assert.Equal(new[] { mine.Id, other.Id }.Order(), teams.Select(x => x.Id).Order());
    }

    [Fact]
    public async Task GetMine_DoesNotReturnATeamTheCallerIsNotOn()
    {
        var msel = await SeedMsel();
        var mine = await SeedTeam(msel);
        await SeedTeam(msel);
        var actor = await Actor().OnTeam(mine).SeedAsync();

        var teams = await GetTeams(Client(actor), MyTeams);

        Assert.Equal(mine.Id, Assert.Single(teams).Id);
    }

    /// <remarks>
    /// The team and its member exist so that the empty answer is the filter's doing rather than the
    /// database's: a caller on no team must be told about no team even when there is one to be told about.
    /// </remarks>
    [Fact]
    public async Task GetMine_ForACallerOnNoTeam_IsAnEmptyList()
    {
        var msel = await SeedMsel();
        var team = await SeedTeam(msel);
        await Actor().OnTeam(team).SeedAsync();
        var actor = await Actor().SeedAsync();

        Assert.Empty(await GetTeams(Client(actor), MyTeams));
    }

    /// <remarks>
    /// The only route in any of the three files of this unit with no authorization of any kind: the
    /// controller resolves no <c>SystemPermission</c> and the service filters on the caller's own id, which
    /// is the argument that it needs none. The second assertion is the contrast that makes the route worth
    /// pinning - the same caller may list their own team here and may not read it by id, because
    /// <c>GET teams/{id}</c> requires <c>ViewMsels</c> outright and consults no membership at all. So the
    /// id this route hands out is an id the caller cannot use. Requiring a permission here turns the first
    /// assertion red.
    /// </remarks>
    [Fact]
    public async Task GetMine_AsksTheCallerForNoPermissionAtAll()
    {
        var msel = await SeedMsel();
        var team = await SeedTeam(msel);
        var actor = await Actor().OnTeam(team).SeedAsync();

        var mine = Assert.Single(await GetTeams(Client(actor), MyTeams));

        Assert.Equal(msel.Id, mine.MselId);
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await Client(actor).GetAsync(TeamRoute(team.Id), Ct)).StatusCode);
    }

    /// <remarks>
    /// <c>GetMineAsync</c> reads <c>TeamUsers</c> and projects the team out of it, and a projection
    /// discards the <c>Include</c> above it, so the membership is never materialized and <c>users</c> is
    /// empty however many members the team has. Contrast
    /// <see cref="GetByMsel_AnswersEachTeamWithItsMembers"/>, which answers the same view model with the
    /// list populated. Loading the membership here turns this red.
    /// </remarks>
    [Fact]
    public async Task GetMine_AnswersEachTeamWithoutItsMembers()
    {
        var msel = await SeedMsel();
        var team = await SeedTeam(msel);
        var other = await Actor().OnTeam(team).SeedAsync();
        var actor = await Actor().OnTeam(team).SeedAsync();

        var mine = Assert.Single(await GetTeams(Client(actor), MyTeams));

        Assert.Empty(mine.Users);
        Assert.Equal(2, await MembersOf(team.Id));
        Assert.NotEqual(actor.Id, other.Id);
    }

    // ---------------------------------------------------------------------------------------------
    // GET msels/{mselId}/teams
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task GetByMsel_ReturnsEveryTeamOnTheMsel()
    {
        var msel = await SeedMsel();
        var first = await SeedTeam(msel);
        var second = await SeedTeam(msel);
        var actor = await Actor().OnMsel(msel, MselRole.Viewer).SeedAsync();

        var teams = await GetTeams(Client(actor), TeamsOf(msel.Id));

        Assert.Equal(new[] { first.Id, second.Id }.Order(), teams.Select(x => x.Id).Order());
    }

    [Fact]
    public async Task GetByMsel_DoesNotReturnAnotherMselsTeams()
    {
        var msel = await SeedMsel();
        var mine = await SeedTeam(msel);
        await SeedTeam(await SeedMsel());
        var actor = await Actor().OnMsel(msel, MselRole.Viewer).SeedAsync();

        var teams = await GetTeams(Client(actor), TeamsOf(msel.Id));

        Assert.Equal(mine.Id, Assert.Single(teams).Id);
    }

    [Fact]
    public async Task GetByMsel_ForAMselWithNoTeams_IsAnEmptyList()
    {
        var msel = await SeedMsel();
        var actor = await Actor().OnMsel(msel, MselRole.Viewer).SeedAsync();

        Assert.Empty(await GetTeams(Client(actor), TeamsOf(msel.Id)));
    }

    [Fact]
    public async Task GetByMsel_WithViewMselsAndNoRoleOnTheMsel_Is200()
    {
        var msel = await SeedMsel();
        await SeedTeam(msel);
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewMsels).SeedAsync();

        Assert.Single(await GetTeams(Client(actor), TeamsOf(msel.Id)));
    }

    [Fact]
    public async Task GetByMsel_ForACallerWithNoRoleOnTheMsel_Is403()
    {
        var msel = await SeedMsel();
        var actor = await Actor().SeedAsync();

        var response = await Client(actor).GetAsync(TeamsOf(msel.Id), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <remarks>
    /// <c>MselViewRequirement</c> accepts a unit member holding any of five roles, and separately accepts a
    /// member of any team on the MSEL - so a participant reads the team list. <c>Evaluator</c> is missing
    /// from that role list, which is the defect pinned in <c>MselViewRequirementTests</c>; adding it turns
    /// the last case here from 403 to 200 and so turns this test red.
    /// </remarks>
    [Theory]
    [InlineData(MselRole.Owner, HttpStatusCode.OK)]
    [InlineData(MselRole.Editor, HttpStatusCode.OK)]
    [InlineData(MselRole.Approver, HttpStatusCode.OK)]
    [InlineData(MselRole.MoveEditor, HttpStatusCode.OK)]
    [InlineData(MselRole.Viewer, HttpStatusCode.OK)]
    [InlineData(MselRole.Evaluator, HttpStatusCode.Forbidden)]
    public async Task GetByMsel_ForEachMselRole(MselRole role, HttpStatusCode expected)
    {
        var msel = await SeedMsel();
        var actor = await Actor().OnMsel(msel, role).SeedAsync();

        var response = await Client(actor).GetAsync(TeamsOf(msel.Id), Ct);

        Assert.Equal(expected, response.StatusCode);
    }

    [Fact]
    public async Task GetByMsel_ForAMemberOfOneOfItsTeams_Is200()
    {
        var msel = await SeedMsel();
        var team = await SeedTeam(msel);
        var actor = await Actor().OnTeam(team).SeedAsync();

        Assert.Single(await GetTeams(Client(actor), TeamsOf(msel.Id)));
    }

    /// <remarks>
    /// The fall-through at <c>TeamService.cs:78-80</c>: a caller the requirement refused is let through
    /// anyway when the MSEL is a template, so every template's team list is world-readable to any signed-in
    /// caller. Deliberate for the template library; note that nothing distinguishes a template from a
    /// live MSEL that happens to carry the flag.
    /// </remarks>
    [Fact]
    public async Task GetByMsel_ForATemplateMsel_IsReadableWithNoRoleAtAll()
    {
        var msel = await SeedMsel(isTemplate: true);
        await SeedTeam(msel);
        var actor = await Actor().SeedAsync();

        Assert.Single(await GetTeams(Client(actor), TeamsOf(msel.Id)));
    }

    /// <remarks>
    /// The same fall-through with nothing to fall through to: <c>FindAsync(mselId)</c> answers null and
    /// <c>msel.IsTemplate</c> is a <c>NullReferenceException</c>, so an unknown MSEL id is a 500 for an
    /// ordinary caller and - because the requirement short-circuits on the permission - an empty list for a
    /// <c>ViewMsels</c> holder. Both halves are asserted here so the contradiction is on one screen. A null
    /// guard turns the first half into the 403 the branch was reaching for, and the second half is
    /// unaffected.
    /// </remarks>
    [Fact]
    public async Task GetByMsel_ForAMselThatIsNotThere_Is500()
    {
        var stranger = await Actor().SeedAsync();
        var privileged = await Actor().WithSystemPermissions(SystemPermission.ViewMsels).SeedAsync();

        var response = await Client(stranger).GetAsync(TeamsOf(Guid.NewGuid()), Ct);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Empty(await GetTeams(Client(privileged), TeamsOf(Guid.NewGuid())));
    }

    /// <remarks>
    /// The route that does load the membership. <c>TeamProfile</c> maps <c>Users</c> from
    /// <c>TeamUsers.Select(y => y.User)</c> and marks it <c>ExplicitExpansion</c>, which governs
    /// <c>ProjectTo</c> and not <c>Map</c>, so the two <c>Include</c>s reach the wire.
    /// </remarks>
    [Fact]
    public async Task GetByMsel_AnswersEachTeamWithItsMembers()
    {
        var msel = await SeedMsel();
        var team = await SeedTeam(msel);
        var member = await Actor().OnTeam(team).SeedAsync();
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewMsels).SeedAsync();

        var teams = await GetTeams(Client(actor), TeamsOf(msel.Id));

        Assert.Equal(member.Id, Assert.Single(Assert.Single(teams).Users).Id);
    }

    // ---------------------------------------------------------------------------------------------
    // GET users/{userId}/teams
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task GetByUser_ReturnsTheUsersTeams()
    {
        var msel = await SeedMsel();
        var team = await SeedTeam(msel);
        await SeedTeam(msel);
        var member = await Actor().OnTeam(team).SeedAsync();
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageUsers).SeedAsync();

        var teams = await GetTeams(Client(actor), TeamsOfUser(member.Id));

        Assert.Equal(team.Id, Assert.Single(teams).Id);
    }

    /// <remarks>
    /// There is no existence check of any kind, so "that user is on no team" and "there is no such user"
    /// are one answer. A 404 for the second would turn this red. Somebody else's membership is seeded so
    /// that the empty answer is the filter's doing and not the database's.
    /// </remarks>
    [Fact]
    public async Task GetByUser_ForAUserThatIsNotThere_IsAnEmptyList()
    {
        var msel = await SeedMsel();
        var team = await SeedTeam(msel);
        await Actor().OnTeam(team).SeedAsync();
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageUsers).SeedAsync();

        Assert.Empty(await GetTeams(Client(actor), TeamsOfUser(Guid.NewGuid())));
    }

    /// <remarks>
    /// <c>ManageUsers</c> is the whole of the check and the service is scoped by nothing, so this answers
    /// teams on MSELs the caller has no role on - which is the point of the route, and is also why it is
    /// the only read in the file that a MSEL owner cannot use about their own team's members.
    /// </remarks>
    [Fact]
    public async Task GetByUser_WithManageUsersAndNoRoleOnTheMsel_Is200()
    {
        var msel = await SeedMsel();
        var team = await SeedTeam(msel);
        var member = await Actor().OnTeam(team).SeedAsync();
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageUsers).SeedAsync();

        Assert.Single(await GetTeams(Client(actor), TeamsOfUser(member.Id)));
    }

    [Fact]
    public async Task GetByUser_WithoutManageUsers_Is403()
    {
        var msel = await SeedMsel();
        var team = await SeedTeam(msel);
        var actor = await Actor().OnMsel(msel, MselRole.Owner).OnTeam(team).SeedAsync();

        var response = await Client(actor).GetAsync(TeamsOfUser(actor.Id), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ---------------------------------------------------------------------------------------------
    // GET teams/{id}
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Get_ReturnsTheTeamWithItsMembers()
    {
        var msel = await SeedMsel();
        var team = await SeedTeam(msel);
        var member = await Actor().OnTeam(team).SeedAsync();
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewMsels).SeedAsync();

        var answered = await GetTeam(Client(actor), team.Id);

        Assert.Equal(team.Name, answered.Name);
        Assert.Equal(msel.Id, answered.MselId);
        Assert.Equal(member.Id, Assert.Single(answered.Users).Id);
    }

    /// <remarks>
    /// The one <c>GetAsync</c> in this neighbourhood written as <c>SingleOrDefaultAsync</c>, so the
    /// controller's own null check at <c>TeamController.cs:107</c> is live and an unknown id is the 404 it
    /// should be - the positive contrast with the four <c>SingleAsync</c>-plus-dead-null-check copies in
    /// <c>OrganizationService</c>, <c>MoveService</c>, <c>CardService</c> and <c>InjectService</c>.
    /// </remarks>
    [Fact]
    public async Task Get_ForAnIdThatIsNotThere_Is404()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewMsels).SeedAsync();

        var response = await Client(actor).GetAsync(TeamRoute(Guid.NewGuid()), Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <remarks>
    /// The service checks nothing and the controller asks for <c>ViewMsels</c> outright, so one permission
    /// grant reads every team in the installation by id, on any MSEL, with its member list. Scoping the
    /// route to the team's MSEL - which is what its own remarks describe - turns this red.
    /// </remarks>
    [Fact]
    public async Task Get_WithViewMselsAndNoRoleOnAnyMsel_ReadsAnyTeam()
    {
        var team = await SeedTeam(await SeedMsel());
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewMsels).SeedAsync();

        Assert.Equal(team.Id, (await GetTeam(Client(actor), team.Id)).Id);
    }

    /// <remarks>
    /// The other side of the same finding: the MSEL's owner may rename and delete this team and may not
    /// read it by id, because <c>ViewMsels</c> is the only thing the route accepts. Every MSEL-scoped read
    /// elsewhere in the API passes the permission down as a <c>bool</c> and lets the requirement helpers
    /// answer; doing that here turns this red.
    /// </remarks>
    [Fact]
    public async Task Get_ForTheMselsOwnerWithoutViewMsels_Is403()
    {
        var msel = await SeedMsel();
        var team = await SeedTeam(msel);
        var actor = await Actor().OnMsel(msel, MselRole.Owner).OnTeam(team).SeedAsync();

        var response = await Client(actor).GetAsync(TeamRoute(team.Id), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ---------------------------------------------------------------------------------------------
    // POST teams
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Create_StoresTheTeamAndAnswers201()
    {
        var msel = await SeedMsel();
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        var response = await Post(Client(actor), Body(msel.Id) with { Name = "Blue Cell" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var created = await Read<ViewModels.Team>(response);

        Assert.EndsWith($"/api/teams/{created.Id}", response.Headers.Location?.ToString());
        Assert.Equal("Blue Cell", (await Stored(created.Id)).Name);
        Assert.Equal(msel.Id, (await Stored(created.Id)).MselId);
    }

    [Fact]
    public async Task Create_WithEditMselsAndNoRoleOnTheMsel_Is201()
    {
        var msel = await SeedMsel();
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();

        var response = await Post(Client(actor), Body(msel.Id));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    /// <remarks>
    /// <c>MselOwnerRequirement</c> accepts the MSEL's creator and a unit member holding <c>Owner</c>, and
    /// nothing else - so an <c>Editor</c> may edit every row of the MSEL's content and may not add a team
    /// to it. Accepting <c>Editor</c> here turns the second case red.
    /// </remarks>
    [Theory]
    [InlineData(MselRole.Owner, HttpStatusCode.Created)]
    [InlineData(MselRole.Editor, HttpStatusCode.Forbidden)]
    [InlineData(MselRole.Viewer, HttpStatusCode.Forbidden)]
    public async Task Create_ForEachMselRole(MselRole role, HttpStatusCode expected)
    {
        var msel = await SeedMsel();
        var actor = await Actor().OnMsel(msel, role).SeedAsync();

        var response = await Post(Client(actor), Body(msel.Id));

        Assert.Equal(expected, response.StatusCode);
    }

    [Fact]
    public async Task Create_ForACallerWithNoRoleOnTheMsel_Is403()
    {
        var msel = await SeedMsel();
        var actor = await Actor().SeedAsync();

        var response = await Post(Client(actor), Body(msel.Id));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, await CountOn(msel.Id));
    }

    /// <remarks>
    /// <c>MselOwnerRequirement.IsMet</c> dereferences a <c>FirstOrDefaultAsync</c>, so naming a MSEL that
    /// is not there is a 500 rather than the 404 or 403 either answer would justify - the Phase 2 finding,
    /// reached from a create. A null guard in the requirement turns this red, which is what
    /// <c>MselOwnerRequirementTests</c>' two characterizations already promise.
    /// </remarks>
    [Fact]
    public async Task Create_ForAMselThatIsNotThere_Is500()
    {
        var actor = await Actor().SeedAsync();

        var response = await Post(Client(actor), Body(Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    /// <remarks>
    /// <c>ViewModels.Team.MselId</c> is <c>Guid?</c> where the entity's is <c>Guid</c>, so AutoMapper's
    /// null-source rule writes <see cref="Guid.Empty"/> and the insert fails the foreign key - a 500 with
    /// nothing said about which field was wrong, where a <c>[Required]</c> on the view model would be a 400
    /// from <c>ValidateModelStateFilter</c>. Adding one turns this red.
    /// </remarks>
    [Fact]
    public async Task Create_ThatOmitsTheMselId_Is500()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();

        var response = await Post(Client(actor), Body(Guid.NewGuid()) with { MselId = null });

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    [Fact]
    public async Task Create_StampsTheAuditFieldsOnTheServer()
    {
        var msel = await SeedMsel();
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();
        var before = DateTime.UtcNow;

        var response = await Post(Client(actor), Body(msel.Id) with
        {
            CreatedBy = Guid.NewGuid(),
            DateCreated = new DateTime(1999, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            ModifiedBy = Guid.NewGuid(),
            DateModified = new DateTime(1999, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        });

        var created = await Read<ViewModels.Team>(response);
        var stored = await Stored(created.Id);

        Assert.Equal(actor.Id, stored.CreatedBy);
        AssertStampedBetween(stored.DateCreated, before, DateTime.UtcNow);
        Assert.Null(stored.ModifiedBy);
        Assert.Null(stored.DateModified);
    }

    /// <remarks>
    /// The body's <c>Id</c> is kept when it is not empty, so a client chooses the primary key and a second
    /// create naming it is a 500 from the unique index rather than the 409 a conflict deserves. Ignoring
    /// the body's id - which is what <c>ScenarioEventProfile</c> does - turns this red.
    /// </remarks>
    [Fact]
    public async Task Create_KeepsTheBodysIdAndRefusesASecondOneWithA500()
    {
        var msel = await SeedMsel();
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();
        var id = Guid.NewGuid();

        var first = await Post(Client(actor), Body(msel.Id) with { Id = id });
        var second = await Post(Client(actor), Body(msel.Id) with { Id = id });

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(id, (await Read<ViewModels.Team>(first)).Id);
        Assert.Equal(HttpStatusCode.InternalServerError, second.StatusCode);
    }

    /// <remarks>
    /// Nothing in <c>TeamConfiguration</c> makes a team's name or short name unique, so a MSEL may hold
    /// two teams called the same thing and no route can tell a participant which one they are on.
    /// </remarks>
    [Fact]
    public async Task Create_WithANameTheMselAlreadyUses_Is201()
    {
        var msel = await SeedMsel();
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        await Post(Client(actor), Body(msel.Id) with { Name = "Blue Cell" });
        var second = await Post(Client(actor), Body(msel.Id) with { Name = "Blue Cell" });

        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        Assert.Equal(2, await CountOn(msel.Id));
    }

    /// <remarks>
    /// No write path in <c>TeamService</c> calls <c>ServiceUtilities.SetMselModifiedAsync</c>, so the MSEL
    /// a team was just added to still reports never having been modified. Adding the call - which
    /// <c>UserTeamRoleService</c> makes on both of its writes - turns this red.
    /// </remarks>
    [Fact]
    public async Task Create_LeavesTheMselUnmodified()
    {
        var msel = await SeedMsel();
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        await Post(Client(actor), Body(msel.Id));

        Assert.Null((await StoredMsel(msel.Id)).DateModified);
        Assert.Null((await StoredMsel(msel.Id)).ModifiedBy);
    }

    /// <remarks>
    /// <c>TeamHandler.GetGroups</c> names the team, its MSEL and the admin data group. Unlike
    /// <c>OrganizationHandler</c> and <c>CardHandler</c> it cannot produce a group named by the empty
    /// string, because <c>TeamEntity.MselId</c> is not nullable.
    /// </remarks>
    [Fact]
    public async Task Create_BroadcastsTeamCreatedToTheTeamTheMselAndTheAdmins()
    {
        var msel = await SeedMsel();
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        var created = await Read<ViewModels.Team>(await Post(Client(actor), Body(msel.Id)));

        Assert.Equal(
            new[] { MainHub.ADMIN_DATA_GROUP, created.Id.ToString(), msel.Id.ToString() }.Order(),
            Hub.Recipients(MainHubMethods.TeamCreated).Order());
    }

    // ---------------------------------------------------------------------------------------------
    // POST teams/msel/{mselId}/unit/{unitId}
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task CreateFromUnit_CopiesTheUnitsNameAndEveryMember()
    {
        var msel = await SeedMsel();
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();
        var first = await Actor().SeedAsync();
        var second = await Actor().SeedAsync();
        var unit = await SeedUnit(first.Id, second.Id);

        var response = await PostFromUnit(Client(actor), msel.Id, unit.Id);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var created = await Read<ViewModels.Team>(response);
        var stored = await Stored(created.Id);

        Assert.Equal(unit.Name, stored.Name);
        Assert.Equal(unit.ShortName, stored.ShortName);
        Assert.Equal(msel.Id, stored.MselId);
        var members = await MemberIdsOf(created.Id);

        Assert.Equal(new[] { first.Id, second.Id }.Order(), members.Order());
        Assert.EndsWith($"/api/teams/{created.Id}", response.Headers.Location?.ToString());
    }

    [Fact]
    public async Task CreateFromUnit_ForAUnitThatIsNotThere_Is404()
    {
        var msel = await SeedMsel();
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        var response = await PostFromUnit(Client(actor), msel.Id, Guid.NewGuid());

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(0, await CountOn(msel.Id));
    }

    [Fact]
    public async Task CreateFromUnit_WithEditMselsAndNoRoleOnTheMsel_Is201()
    {
        var msel = await SeedMsel();
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();
        var unit = await SeedUnit(actor.Id);

        var response = await PostFromUnit(Client(actor), msel.Id, unit.Id);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task CreateFromUnit_ForACallerWithNoRoleOnTheMsel_Is403()
    {
        var msel = await SeedMsel();
        var actor = await Actor().SeedAsync();
        var unit = await SeedUnit(actor.Id);

        var response = await PostFromUnit(Client(actor), msel.Id, unit.Id);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, await CountOn(msel.Id));
    }

    /// <remarks>
    /// The permission check runs before the unit is loaded, so an unknown MSEL is
    /// <c>MselOwnerRequirement</c>'s unguarded dereference and a 500 - and it is a 500 for a caller holding
    /// <c>EditMsels</c> too, because the create then fails the MSEL foreign key. Two causes, one status;
    /// guarding the requirement leaves the second half of this test green.
    /// </remarks>
    [Fact]
    public async Task CreateFromUnit_ForAMselThatIsNotThere_Is500()
    {
        var stranger = await Actor().SeedAsync();
        var privileged = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();
        var unit = await SeedUnit(stranger.Id);

        Assert.Equal(
            HttpStatusCode.InternalServerError,
            (await PostFromUnit(Client(stranger), Guid.NewGuid(), unit.Id)).StatusCode);
        Assert.Equal(
            HttpStatusCode.InternalServerError,
            (await PostFromUnit(Client(privileged), Guid.NewGuid(), unit.Id)).StatusCode);
    }

    /// <remarks>
    /// Nothing records that a team came from a unit and nothing checks whether one already has, so calling
    /// the route twice leaves the MSEL with two identically-named teams holding the same members. Every
    /// participant is then on two teams of one MSEL, which is the state <c>XApiService</c> and
    /// <c>IntegrationCiteExtensions</c> both resolve by taking whichever row the database returns first.
    /// </remarks>
    [Fact]
    public async Task CreateFromUnit_CalledTwice_CreatesASecondIdenticalTeam()
    {
        var msel = await SeedMsel();
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();
        var member = await Actor().SeedAsync();
        var unit = await SeedUnit(member.Id);

        await PostFromUnit(Client(actor), msel.Id, unit.Id);
        var second = await PostFromUnit(Client(actor), msel.Id, unit.Id);

        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        Assert.Equal(2, await CountOn(msel.Id));
    }

    /// <remarks>
    /// A unit with no members makes an empty team, which is the same answer as a unit whose members have
    /// all left - there is nothing to distinguish them and no warning either way.
    /// </remarks>
    [Fact]
    public async Task CreateFromUnit_ForAUnitWithNoMembers_CreatesAnEmptyTeam()
    {
        var msel = await SeedMsel();
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();
        var unit = await SeedUnit();

        var created = await Read<ViewModels.Team>(await PostFromUnit(Client(actor), msel.Id, unit.Id));

        Assert.Equal(0, await MembersOf(created.Id));
    }

    // ---------------------------------------------------------------------------------------------
    // PUT teams/{id}
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Update_ChangesTheTeamAndAnswers200()
    {
        var msel = await SeedMsel();
        var team = await SeedTeam(msel);
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        var response = await Put(Client(actor), team.Id, BodyFor(team) with { Name = "Renamed" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Renamed", (await Read<ViewModels.Team>(response)).Name);
        Assert.Equal("Renamed", (await Stored(team.Id)).Name);
    }

    [Fact]
    public async Task Update_ForAnIdThatIsNotThere_Is404()
    {
        var msel = await SeedMsel();
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();

        var response = await Put(Client(actor), Guid.NewGuid(), Body(msel.Id));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Update_WithEditMselsAndNoRoleOnTheMsel_Is200()
    {
        var team = await SeedTeam(await SeedMsel());
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();

        var response = await Put(Client(actor), team.Id, BodyFor(team) with { Name = "Renamed" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Update_ForACallerWithNoRoleOnTheMsel_Is403()
    {
        var msel = await SeedMsel();
        var team = await SeedTeam(msel);
        var actor = await Actor().OnTeam(team).SeedAsync();

        var response = await Put(Client(actor), team.Id, BodyFor(team) with { Name = "Renamed" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(team.Name, (await Stored(team.Id)).Name);
    }

    /// <remarks>
    /// <para>
    /// The sixth instance of the request-body permission decision in the branch, and the first with a guard
    /// against what usually follows: a caller who owns MSEL B names B in the body of a PUT to a team on
    /// MSEL A, passes <c>MselOwnerRequirement</c> on the strength of it, and is then refused by
    /// <c>TeamService.cs:173</c> - as an <c>ArgumentException</c>, which <c>JsonExceptionFilter</c> maps to
    /// 500. So the steal is blocked and the caller is told the server broke.
    /// </para>
    /// <para>
    /// Deciding from the stored row - which <c>DeleteAsync</c> fourteen lines below already does - turns
    /// this into the 403 it should be and reddens this test. Turning the <c>ArgumentException</c> into a
    /// <c>BadRequestException</c> reddens it too, and would be an improvement on its own.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Update_ChoosesItsPermissionBranchFromTheRequestBodyAndThenRefusesTheSteal()
    {
        var target = await SeedMsel();
        var team = await SeedTeam(target);
        var mine = await SeedMsel();
        var actor = await Actor().OnMsel(mine, MselRole.Owner).SeedAsync();

        var response = await Put(Client(actor), team.Id, BodyFor(team) with { MselId = mine.Id });

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal(target.Id, (await Stored(team.Id)).MselId);
    }

    /// <remarks>
    /// The guard compares a <c>Guid</c> against a <c>Guid?</c>, so a body that does not mention the MSEL is
    /// a mismatch and a 500 - and because the permission check runs first, a caller with no permission at
    /// all gets <c>MselOwnerRequirement</c>'s null dereference instead, which is the same 500 for a
    /// different reason. A PUT is therefore unusable by any client that does not echo the whole row back.
    /// </remarks>
    [Fact]
    public async Task Update_ThatOmitsTheMselId_Is500()
    {
        var msel = await SeedMsel();
        var team = await SeedTeam(msel);
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();

        var response = await Put(Client(actor), team.Id, BodyFor(team) with { MselId = null });

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal(msel.Id, (await Stored(team.Id)).MselId);
    }

    /// <remarks>
    /// <para>
    /// <strong>A PUT lets the client say who made the change.</strong> <c>SaveEntries</c> restores
    /// <c>CreatedBy</c> and <c>DateCreated</c> from <c>OriginalValues</c> and stamps <c>DateModified</c>
    /// with <c>UtcNow</c>, so three of the four audit fields are immune to a hostile body exactly as Phase
    /// 1 found - but it never touches <c>ModifiedBy</c>, and neither
    /// <c>TeamController.Update</c> nor <c>TeamService.UpdateAsync</c> assigns it either, while
    /// <c>TeamProfile</c> maps it by convention. So the row records whoever the request said, and the one
    /// audit field that answers "who did this" is the one a client controls.
    /// </para>
    /// <para>
    /// This is the second exception to Phase 1's immunity finding, after the nested data values in
    /// <c>3437aed</c> - and the simpler of the two, needing no attach-versus-load trick. Compare
    /// <c>MoveController.cs:112</c>, which assigns <c>ModifiedBy</c> redundantly because
    /// <c>MoveService</c> has already done it (<c>997a9c4</c>): here neither layer does. Assigning it in
    /// either layer turns the third assertion red, which is the fix.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Update_StampsTheDateButLetsTheClientClaimTheModifier()
    {
        var msel = await SeedMsel();
        var team = await SeedTeam(msel);
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();
        var created = await Stored(team.Id);
        var somebodyElse = Guid.NewGuid();
        var before = DateTime.UtcNow;

        await Put(Client(actor), team.Id, BodyFor(team) with
        {
            Name = "Renamed",
            CreatedBy = Guid.NewGuid(),
            DateCreated = new DateTime(1999, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            ModifiedBy = somebodyElse
        });

        var stored = await Stored(team.Id);

        Assert.Equal(created.CreatedBy, stored.CreatedBy);
        Assert.Equal(created.DateCreated, stored.DateCreated);
        Assert.Equal(somebodyElse, stored.ModifiedBy);
        AssertStampedBetween(stored.DateModified, before, DateTime.UtcNow);
    }

    /// <remarks>
    /// The other half of the finding above: a body that mentions no <c>modifiedBy</c> leaves the column
    /// null, so an edited team says nobody edited it. The UI's own client always echoes the row back, so
    /// in practice the value stored is whatever the row last held - which is null for a team that has
    /// never been edited, and the previous editor's id for one that has.
    /// </remarks>
    [Fact]
    public async Task Update_ThatMentionsNoModifier_RecordsNone()
    {
        var msel = await SeedMsel();
        var team = await SeedTeam(msel);
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        await Put(Client(actor), team.Id, BodyFor(team) with { Name = "Renamed" });

        var stored = await Stored(team.Id);

        Assert.Null(stored.ModifiedBy);
        Assert.NotNull(stored.DateModified);
    }

    /// <remarks>
    /// <c>TeamProfile</c> maps <c>Id</c>, and a key cannot be modified on a tracked entity, so a PUT whose
    /// body carries a different id is a 500 from EF rather than the 400 a mismatch deserves. Nothing
    /// validates that the route and the body agree - the same finding as <c>MoveService</c>'s.
    /// </remarks>
    [Fact]
    public async Task Update_WithABodyIdThatIsNotTheRoutes_Is500()
    {
        var msel = await SeedMsel();
        var team = await SeedTeam(msel);
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        var response = await Put(Client(actor), team.Id, BodyFor(team) with { Id = Guid.NewGuid() });

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    [Fact]
    public async Task Update_LeavesTheMselUnmodified()
    {
        var msel = await SeedMsel();
        var team = await SeedTeam(msel);
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        await Put(Client(actor), team.Id, BodyFor(team) with { Name = "Renamed" });

        Assert.Null((await StoredMsel(msel.Id)).DateModified);
    }

    // ---------------------------------------------------------------------------------------------
    // DELETE teams/{id}
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Delete_RemovesTheTeamAndAnswers204()
    {
        var msel = await SeedMsel();
        var team = await SeedTeam(msel);
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        var response = await Client(actor).DeleteAsync(TeamRoute(team.Id), Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Null(await Stored(team.Id));
        Assert.Equal(
            new[] { MainHub.ADMIN_DATA_GROUP, msel.Id.ToString(), team.Id.ToString() }.Order(),
            Hub.Recipients(MainHubMethods.TeamDeleted).Order());
        Assert.All(Hub.Of(MainHubMethods.TeamDeleted), send => Assert.Equal(team.Id, send.Payload));
    }

    [Fact]
    public async Task Delete_ForAnIdThatIsNotThere_Is404()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();

        var response = await Client(actor).DeleteAsync(TeamRoute(Guid.NewGuid()), Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Delete_WithEditMselsAndNoRoleOnTheMsel_Is204()
    {
        var team = await SeedTeam(await SeedMsel());
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();

        var response = await Client(actor).DeleteAsync(TeamRoute(team.Id), Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task Delete_ForACallerWithNoRoleOnTheMsel_Is403()
    {
        var msel = await SeedMsel();
        var team = await SeedTeam(msel);
        var actor = await Actor().OnTeam(team).SeedAsync();

        var response = await Client(actor).DeleteAsync(TeamRoute(team.Id), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.NotNull(await Stored(team.Id));
    }

    /// <remarks>
    /// The method this file's <c>UpdateAsync</c> should have been copied from: the row is read first, the
    /// existence check comes before the permission check, and the MSEL the decision is taken on is the
    /// stored one - so owning another MSEL grants nothing, and an unknown id is a 404 rather than a 403.
    /// </remarks>
    [Fact]
    public async Task Delete_TakesItsPermissionDecisionFromTheStoredRow()
    {
        var team = await SeedTeam(await SeedMsel());
        var mine = await SeedMsel();
        var actor = await Actor().OnMsel(mine, MselRole.Owner).SeedAsync();

        var response = await Client(actor).DeleteAsync(TeamRoute(team.Id), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.NotNull(await Stored(team.Id));
    }

    /// <remarks>
    /// Every row that names the team goes with it, by database cascade rather than by anything the service
    /// does - so nothing counts the participants about to lose their membership and nothing offers a 409.
    /// The route answers 204 either way.
    /// </remarks>
    [Fact]
    public async Task Delete_CascadesToItsMembershipsAndRoles()
    {
        var msel = await SeedMsel();
        var team = await SeedTeam(msel);
        var member = await Actor().OnTeam(team).SeedAsync();
        await Seed(BlueprintAppFactory.UserTeamRole(member.Id, team.Id));
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        var response = await Client(actor).DeleteAsync(TeamRoute(team.Id), Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(0, await MembersOf(team.Id));
        Assert.Equal(0, await RolesOn(team.Id));
    }

    /// <remarks>
    /// The cascade the other way: deleting the MSEL takes its teams. <c>TeamEntity.MselId</c> is
    /// non-nullable, so there is no orphan state to reach.
    /// </remarks>
    [Fact]
    public async Task ADeletedMselTakesItsTeamsWithIt()
    {
        var msel = await SeedMsel();
        var team = await SeedTeam(msel);

        await using (var context = NewContext())
        {
            context.Msels.Remove(await context.Msels.SingleAsync(x => x.Id == msel.Id, Ct));
            await context.SaveChangesAsync(Ct);
        }

        Assert.Null(await Stored(team.Id));
    }

    // ---------------------------------------------------------------------------------------------
    // Authentication
    // ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("GET", "my-teams")]
    [InlineData("GET", "msels/00000000-0000-0000-0000-000000000001/teams")]
    [InlineData("GET", "users/00000000-0000-0000-0000-000000000001/teams")]
    [InlineData("GET", "teams/00000000-0000-0000-0000-000000000001")]
    [InlineData("POST", "teams")]
    [InlineData("POST", "teams/msel/00000000-0000-0000-0000-000000000001/unit/00000000-0000-0000-0000-000000000002")]
    [InlineData("PUT", "teams/00000000-0000-0000-0000-000000000001")]
    [InlineData("DELETE", "teams/00000000-0000-0000-0000-000000000001")]
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

    private const string Teams = "/api/teams";

    private const string MyTeams = "/api/my-teams";

    private static string TeamRoute(Guid id) => $"{Teams}/{id}";

    private static string TeamsOf(Guid mselId) => $"/api/msels/{mselId}/teams";

    private static string TeamsOfUser(Guid userId) => $"/api/users/{userId}/teams";

    private static string FromUnit(Guid mselId, Guid unitId) =>
        $"{Teams}/msel/{mselId}/unit/{unitId}";

    /// <summary>
    /// The wire shape of a team. A record so a test can vary one property of a stored row with a
    /// <c>with</c> expression. <c>MselId</c> is <c>Guid?</c> because that is what the view model declares,
    /// and sending null is the "omits it" case both write paths are characterized against;
    /// <c>DateCreated</c> and <c>CreatedBy</c> are non-nullable on <c>ViewModels.Base</c>, so they are
    /// always sent as values.
    /// </summary>
    private sealed record TeamBody
    {
        public Guid Id { get; init; }
        public string Name { get; init; }
        public string ShortName { get; init; }
        public string Description { get; init; }
        public Guid? MselId { get; init; }
        public string Email { get; init; }
        public bool canTeamLeaderInvite { get; init; }
        public bool canTeamMemberInvite { get; init; }

        public Guid CreatedBy { get; init; }
        public DateTime DateCreated { get; init; }
        public Guid? ModifiedBy { get; init; }
        public DateTime? DateModified { get; init; }
    }

    private static TeamBody Body(Guid mselId) => new()
    {
        MselId = mselId,
        Name = "posted by the test",
        ShortName = "posted",
        Description = "posted by the test"
    };

    private static TeamBody BodyFor(TeamEntity team) => new()
    {
        Id = team.Id,
        MselId = team.MselId,
        Name = team.Name,
        ShortName = team.ShortName,
        Description = team.Description,
        Email = team.Email,
        canTeamLeaderInvite = team.canTeamLeaderInvite,
        canTeamMemberInvite = team.canTeamMemberInvite
    };

    private async Task<MselEntity> SeedMsel(bool isTemplate = false)
    {
        var msel = BlueprintAppFactory.Msel(isTemplate: isTemplate);
        await Seed(msel);

        return msel;
    }

    private async Task<TeamEntity> SeedTeam(MselEntity msel)
    {
        var team = BlueprintAppFactory.Team(msel.Id);
        await Seed(team);

        return team;
    }

    /// <summary>
    /// A unit holding <paramref name="userIds"/> and assigned to no MSEL - what
    /// <c>POST teams/msel/{mselId}/unit/{unitId}</c> copies a team out of. The users must already exist:
    /// <c>UnitUserEntity.UserId</c> is a foreign key.
    /// </summary>
    private async Task<UnitEntity> SeedUnit(params Guid[] userIds)
    {
        var unit = new UnitEntity
        {
            Id = Guid.NewGuid(),
            Name = $"unit-{Guid.NewGuid()}",
            ShortName = "unit",
            Description = "Seeded by TeamEndpointTests",
            CreatedBy = Guid.NewGuid()
        };

        await Seed(unit);
        await Seed([.. userIds.Select(x => new UnitUserEntity(x, unit.Id))]);

        return unit;
    }

    private Task<HttpResponseMessage> Post(HttpClient client, TeamBody body) =>
        client.PostAsJsonAsync(Teams, body, Ct);

    private Task<HttpResponseMessage> PostFromUnit(HttpClient client, Guid mselId, Guid unitId) =>
        client.PostAsync(FromUnit(mselId, unitId), null, Ct);

    private Task<HttpResponseMessage> Put(HttpClient client, Guid id, TeamBody body) =>
        client.PutAsJsonAsync(TeamRoute(id), body, Ct);

    private async Task<List<ViewModels.Team>> GetTeams(HttpClient client, string url)
    {
        var response = await client.GetAsync(url, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await Read<List<ViewModels.Team>>(response);
    }

    private async Task<ViewModels.Team> GetTeam(HttpClient client, Guid id)
    {
        var response = await client.GetAsync(TeamRoute(id), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await Read<ViewModels.Team>(response);
    }

    private async Task<TeamEntity> Stored(Guid id)
    {
        await using var context = NewContext();

        return await context.Teams.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, Ct);
    }

    private async Task<MselEntity> StoredMsel(Guid id)
    {
        await using var context = NewContext();

        return await context.Msels.AsNoTracking().SingleAsync(x => x.Id == id, Ct);
    }

    private async Task<int> CountOn(Guid mselId)
    {
        await using var context = NewContext();

        return await context.Teams.CountAsync(x => x.MselId == mselId, Ct);
    }

    private async Task<int> MembersOf(Guid teamId)
    {
        await using var context = NewContext();

        return await context.TeamUsers.CountAsync(x => x.TeamId == teamId, Ct);
    }

    private async Task<List<Guid>> MemberIdsOf(Guid teamId)
    {
        await using var context = NewContext();

        return await context.TeamUsers
            .Where(x => x.TeamId == teamId)
            .Select(x => x.UserId)
            .ToListAsync(Ct);
    }

    private async Task<int> RolesOn(Guid teamId)
    {
        await using var context = NewContext();

        return await context.UserTeamRoles.CountAsync(x => x.TeamId == teamId, Ct);
    }

    private static void AssertStampedBetween(DateTime? actual, DateTime notBefore, DateTime notAfter)
    {
        Assert.NotNull(actual);
        Assert.InRange(actual.Value, notBefore.AddSeconds(-1), notAfter.AddSeconds(1));
    }

    private async Task<T> Read<T>(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync(Ct);

        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {body}");

        return JsonSerializer.Deserialize<T>(body, JsonOptions);
    }
}
