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
using Blueprint.Api.Data;
using Blueprint.Api.Data.Enumerations;
using Blueprint.Api.Data.Models;
using Blueprint.Api.Hubs;
using Blueprint.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Blueprint.Api.Tests;

/// <summary>
/// <c>UserService</c> / <c>UserController</c> - the eight routes behind the person every other row in the
/// application points at. The layer underneath the team and unit membership units (<c>4dd5201</c>,
/// <c>977f578</c>): those decide what a user reaches, this one decides who exists and what system role
/// they hold, and the system role is what grants all 28 <c>SystemPermission</c>s.
/// </summary>
/// <remarks>
/// <para>
/// <strong><c>GET users</c> hands the whole user directory to anybody on any team.</strong>
/// <c>UserController.cs:45</c> resolves <c>ViewUsers</c> and passes it down, and
/// <c>UserService.cs:56-70</c> then treats <em>either</em> the permission <em>or</em> a single
/// <c>TeamUser</c> row as a licence to read every user in the installation - so the permission gates
/// nothing a team membership does not already open
/// (<see cref="GetAll_ForACallerOnATeamHoldingNoPermissionAtAll_ReturnsEveryUserInTheInstallation"/>).
/// The narrow fall-back branch - "just yourself" - applies only to a caller on no team at all, which
/// includes a MSEL's own owner, since a unit is not a team
/// (<see cref="GetAll_ForAMselOwnerReachingItThroughAUnit_ReturnsOnlyThemselves"/>). The membership test
/// takes no <c>CancellationToken</c>.
/// </para>
/// <para>
/// <strong><c>POST users</c> without an id in the body creates the user and answers 500.</strong>
/// <c>CreateAsync</c> ends <c>return await GetAsync(user.Id, true, ct)</c> - the <em>body's</em> id, where
/// the database generated its own (<c>AddPostgresUUIDGeneration</c> gives the column a default), so the
/// read finds nothing and <c>CreatedAtAction</c>'s <c>createdUser.Id</c> dereferences null. The row is
/// already saved and already broadcast as created by then, so the caller is told the create failed and
/// every connected client is told it succeeded
/// (<see cref="Create_ThatOmitsTheId_CreatesTheUserAndAnswers500"/>).
/// <c>UserMselRoleService.CreateAsync</c> one file over returns the <em>entity's</em> id and is the model
/// to copy.
/// </para>
/// <para>
/// <strong>A <c>ManageUsers</c> holder may give themselves every permission in the installation</strong>
/// by naming <c>SystemRoleDefaults.AdministratorRoleId</c> in a PUT to their own row, that role shipping
/// with <c>AllPermissions = true</c> (<see cref="Update_MayGiveTheCallerEveryPermissionInTheInstallation"/>
/// and <see cref="Create_MayGiveTheNewUserEveryPermissionInTheInstallation"/>). The only thing
/// <c>UpdateAsync</c> guards is changing your own <em>id</em>; changing somebody else's is not refused,
/// it is a 500 from EF declining to modify a key
/// (<see cref="Update_ThatChangesSomebodyElsesId_Is500"/>). So the permission that exists to administer
/// the user directory is also the permission to become an administrator, and nothing records the
/// escalation beyond the row's own <c>ModifiedBy</c>.
/// </para>
/// <para>
/// <strong>Nothing invalidates a deleted or re-roled user's claims.</strong> <c>UserService</c> injects
/// <c>IUserClaimsService</c> at <c>:41</c>, assigns it at <c>:49</c> and never reads it, so neither
/// <c>UpdateAsync</c> nor <c>DeleteAsync</c> touches the claims cache the real
/// <c>UserClaimsService</c> keeps - and Phase 2 found that cache is only <c>jti</c>-invalidated behind
/// <c>UseGroupsFromIdP || UseRolesFromIdP</c>, both false in shipped configuration. The harness disables
/// claims caching (<c>ClaimsTransformation:EnableCaching=false</c>) so that tests are independent of one
/// another, which is exactly why no test here can observe it. Untested deliberately, recorded so the
/// gap is not mistaken for coverage.
/// </para>
/// <para>
/// <strong><c>USER_GROUP</c> is joined and nothing ever broadcasts to it.</strong> <c>MainHub.cs:254</c>
/// puts every <c>ViewUsers</c> holder in <c>"AdminUserGroup"</c> and not one of the 25 handlers sends
/// there; <c>UserHandler.GetGroups</c> uses <c>userEntity.CreatedBy.ToString()</c> and
/// <c>ADMIN_DATA_GROUP</c> instead, so what a user-administration screen is subscribed to is the one
/// group it is never told anything on, and what it would have to subscribe to instead is the group that
/// receives every notification in the application
/// (<see cref="Create_TellsTheCreatorsOwnGroupAndTheAdminDataGroup_NeverTheUserGroup"/>).
/// <c>ROLE_GROUP</c> and <c>GROUP_GROUP</c> are both wired up, so this is the only dead one of the four.
/// </para>
/// <para>
/// Also characterized: <c>GET users/{id}</c> refuses a stranger before it looks, so an unknown id is a
/// 403 for a caller without <c>ViewUsers</c> and a 404 for one with it
/// (<see cref="Get_ForAnIdThatIsNotThere_WithoutViewUsers_Is403"/>), while <c>GET teams/{id}/users</c>
/// one route over checks existence first and is a clean 404 for everybody; <c>GET units/{id}/users</c>
/// takes no permission argument and checks nothing about the unit, so an unknown unit is an empty list
/// and a member of a real one cannot read it without <c>ViewUnits</c>; and every <c>ProjectTo</c> here
/// names <c>dest.Permissions</c>, a destination member no profile maps, which AutoMapper 13 tolerates
/// silently and answers null (<see cref="GetAll_AnswersNoPermissions"/>).
/// </para>
/// </remarks>
public class UserEndpointTests(DatabaseFixture fixture, BlueprintAppFactory factory)
    : ApiTestBase(fixture, factory), IClassFixture<BlueprintAppFactory>
{
    // ---------------------------------------------------------------------------------------------
    // GET users
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task GetAll_WithViewUsers_ReturnsEveryUserInTheInstallation()
    {
        var first = BlueprintAppFactory.User();
        var second = BlueprintAppFactory.User();
        await Seed(first, second);
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewUsers).SeedAsync();

        var users = await GetUsers(Client(actor));

        Assert.Contains(first.Id, users.Select(x => x.Id));
        Assert.Contains(second.Id, users.Select(x => x.Id));
        Assert.Contains(actor.Id, users.Select(x => x.Id));
    }

    /// <remarks>
    /// The headline. This caller holds no system role at all - no <c>ViewUsers</c>, no anything - and is
    /// on one team of one MSEL, which is what every participant in an exercise is. The answer is the
    /// whole directory, including two users with no connection to that MSEL, because
    /// <c>UserService.cs:56-57</c> asks only whether a <c>TeamUser</c> row exists and not which team it
    /// names. Fixing this - scoping the read, or enforcing the permission the controller already
    /// resolved - reddens this test and greens
    /// <see cref="GetAll_ForACallerOnNoTeam_ReturnsOnlyThemselves"/>'s expectation for everybody.
    /// </remarks>
    [Fact]
    public async Task GetAll_ForACallerOnATeamHoldingNoPermissionAtAll_ReturnsEveryUserInTheInstallation()
    {
        var stranger = BlueprintAppFactory.User();
        await Seed(stranger);
        var msel = await SeedMsel();
        var team = await SeedTeam(msel.Id);
        var actor = await Actor().OnTeam(team).SeedAsync();

        var users = await GetUsers(Client(actor));

        Assert.Contains(stranger.Id, users.Select(x => x.Id));
    }

    [Fact]
    public async Task GetAll_ForACallerOnNoTeam_ReturnsOnlyThemselves()
    {
        await Seed(BlueprintAppFactory.User(), BlueprintAppFactory.User());
        var actor = await Actor().SeedAsync();

        var users = await GetUsers(Client(actor));

        Assert.Equal(actor.Id, Assert.Single(users).Id);
    }

    /// <remarks>
    /// A unit is not a team. This caller owns the MSEL - <c>TestActorBuilder.OnMsel</c> writes the unit,
    /// the unit membership and the <c>Owner</c> role row - and reads one user, themselves, where the
    /// participant in <see cref="GetAll_ForACallerOnATeamHoldingNoPermissionAtAll_ReturnsEveryUserInTheInstallation"/>
    /// reads all of them. The two routes into a MSEL disagree about a question neither of them is about.
    /// </remarks>
    [Fact]
    public async Task GetAll_ForAMselOwnerReachingItThroughAUnit_ReturnsOnlyThemselves()
    {
        var stranger = BlueprintAppFactory.User();
        await Seed(stranger);
        var msel = await SeedMsel();
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        var users = await GetUsers(Client(actor));

        Assert.Equal(actor.Id, Assert.Single(users).Id);
    }

    /// <remarks>
    /// Both reads project with <c>ProjectTo&lt;ViewModels.User&gt;(…, dest =&gt; dest.Permissions)</c>,
    /// naming an <c>ExplicitExpansion</c> member that <c>UserProfile</c> never maps from anything -
    /// <c>UserEntity</c> has no permissions, they come from the system role. AutoMapper 13 validates
    /// lazily and per map, so naming an unmapped destination member in an expansion is not an error, and
    /// the property crosses the wire as null. <c>GET me/systemPermissions</c> is where a client actually
    /// asks this question; see <c>ClaimsPipelineTests</c>.
    /// </remarks>
    [Fact]
    public async Task GetAll_AnswersNoPermissions()
    {
        var actor = await Actor().WithAllSystemPermissions().SeedAsync();

        var users = await GetUsers(Client(actor));

        Assert.All(users, user => Assert.Null(user.Permissions));
    }

    // ---------------------------------------------------------------------------------------------
    // GET users/{id}
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Get_WithViewUsers_ReturnsTheUser()
    {
        var subject = BlueprintAppFactory.User(name: "vera", roleId: SystemRoleDefaults.ObserverRoleId);
        await Seed(subject);
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewUsers).SeedAsync();

        var user = await GetUser(Client(actor), subject.Id);

        Assert.Equal(subject.Id, user.Id);
        Assert.Equal("vera", user.Name);
        Assert.Equal(SystemRoleDefaults.ObserverRoleId, user.RoleId);
    }

    /// <remarks>
    /// The one thing this route does without a permission: <c>id != _user.GetId()</c> is the whole of the
    /// check, so anybody may read their own row. It is also the only route in the file a caller holding
    /// nothing at all can use.
    /// </remarks>
    [Fact]
    public async Task Get_ForYourselfWithoutAnyPermission_Is200()
    {
        var actor = await Actor().WithName("themselves").SeedAsync();

        var user = await GetUser(Client(actor), actor.Id);

        Assert.Equal("themselves", user.Name);
    }

    [Fact]
    public async Task Get_ForSomebodyElse_WithoutViewUsers_Is403()
    {
        var subject = BlueprintAppFactory.User();
        await Seed(subject);
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageUsers).SeedAsync();

        var response = await Client(actor).GetAsync(UserRoute(subject.Id), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <remarks>
    /// The 404 comes from <c>UserController.cs:69-70</c>, not from the service: <c>GetAsync</c> is
    /// <c>SingleOrDefaultAsync</c> and answers null, which the controller turns into
    /// <c>EntityNotFoundException</c>. That is the well-behaved shape, and it is worth naming because
    /// four services on this branch use <c>SingleAsync</c> plus an unreachable null check instead and
    /// answer 500.
    /// </remarks>
    [Fact]
    public async Task Get_ForAnIdThatIsNotThere_WithViewUsers_Is404()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewUsers).SeedAsync();

        var response = await Client(actor).GetAsync(UserRoute(Guid.NewGuid()), Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <remarks>
    /// Permission before existence, so a caller without <c>ViewUsers</c> cannot tell an id that is not
    /// there from one they may not read. <c>GET teams/{teamId}/users</c> two routes below is the
    /// opposite - <c>GetByTeamAsync</c> looks the team up first - so one controller answers an unknown
    /// id two ways depending on which route it arrived on
    /// (<see cref="GetByTeam_ForATeamThatIsNotThere_Is404ForEverybody"/>).
    /// </remarks>
    [Fact]
    public async Task Get_ForAnIdThatIsNotThere_WithoutViewUsers_Is403()
    {
        var actor = await Actor().SeedAsync();

        var response = await Client(actor).GetAsync(UserRoute(Guid.NewGuid()), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <remarks>
    /// <c>UserEntity</c> is a <c>BaseEntity</c>, so unlike <c>TeamUserEntity</c> (<c>4dd5201</c>) and
    /// <c>UnitUserEntity</c> (<c>977f578</c>) the four audit properties on the view model are backed by
    /// real columns and the values mean something. The row that says a person exists is audited; the
    /// rows that say what they may reach are not.
    /// </remarks>
    [Fact]
    public async Task Get_AnswersAuditFieldsBackedByRealColumns()
    {
        var creator = Guid.NewGuid();
        var subject = BlueprintAppFactory.User(createdBy: creator);
        await Seed(subject);
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewUsers).SeedAsync();

        var user = await GetUser(Client(actor), subject.Id);

        Assert.Equal(creator, user.CreatedBy);
        Assert.NotEqual(default, user.DateCreated);
        Assert.Null(user.ModifiedBy);
        Assert.Null(user.DateModified);
    }

    // ---------------------------------------------------------------------------------------------
    // GET msels/{mselId}/users
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task GetByMsel_ReturnsTheUnitMembersAndTheTeamMembers()
    {
        var msel = await SeedMsel();
        var team = await SeedTeam(msel.Id);
        var onTheTeam = await Actor().OnTeam(team).SeedAsync();
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewMsels).SeedAsync();
        var inTheUnit = await Actor().OnMsel(msel, MselRole.Viewer).SeedAsync();

        var users = await GetUsers(Client(actor), MselUsers(msel.Id));

        Assert.Contains(onTheTeam.Id, users.Select(x => x.Id));
        Assert.Contains(inTheUnit.Id, users.Select(x => x.Id));
    }

    /// <remarks>
    /// <c>Union</c> over two lists of entities loaded by the same context deduplicates by reference, so
    /// one person reachable both ways is listed once. Nothing about that is stated anywhere; a
    /// <c>Concat</c> would list them twice.
    /// </remarks>
    [Fact]
    public async Task GetByMsel_ListsAUserWhoIsBothAUnitMemberAndOnATeamOnce()
    {
        var msel = await SeedMsel();
        var team = await SeedTeam(msel.Id);
        var both = await Actor().OnMsel(msel, MselRole.Viewer).OnTeam(team).SeedAsync();
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewMsels).SeedAsync();

        var users = await GetUsers(Client(actor), MselUsers(msel.Id));

        Assert.Single(users.Where(x => x.Id == both.Id).ToList());
    }

    [Fact]
    public async Task GetByMsel_ForAViewerOnTheMsel_Is200()
    {
        var msel = await SeedMsel();
        var actor = await Actor().OnMsel(msel, MselRole.Viewer).SeedAsync();

        var users = await GetUsers(Client(actor), MselUsers(msel.Id));

        Assert.Equal(actor.Id, Assert.Single(users).Id);
    }

    [Fact]
    public async Task GetByMsel_WithNoRoleOnTheMsel_Is403()
    {
        var msel = await SeedMsel();
        var actor = await Actor().SeedAsync();

        var response = await Client(actor).GetAsync(MselUsers(msel.Id), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <remarks>
    /// The route resolves <c>CreateMsels</c> as well as <c>ViewMsels</c> and passes it into
    /// <c>MselViewRequirement</c>'s four-argument overload, whose template fall-through is the only thing
    /// that reads it. So a content developer with no role anywhere reads a template MSEL's participants,
    /// which for a template is nobody in particular.
    /// </remarks>
    [Fact]
    public async Task GetByMsel_ForATemplate_WithCreateMsels_Is200()
    {
        var msel = BlueprintAppFactory.Msel(isTemplate: true);
        await Seed(msel);
        var actor = await Actor().WithSystemPermissions(SystemPermission.CreateMsels).SeedAsync();

        var users = await GetUsers(Client(actor), MselUsers(msel.Id));

        Assert.Empty(users);
    }

    /// <remarks>
    /// Seeds a membership on another MSEL that must not appear, so an inverted filter reddens this -
    /// the weakness <c>4dd5201</c> found in its own empty-list assertions.
    /// </remarks>
    [Fact]
    public async Task GetByMsel_ForAMselThatIsNotThere_WithViewMsels_IsAnEmptyList()
    {
        var other = await SeedMsel();
        await Actor().OnMsel(other, MselRole.Viewer).SeedAsync();
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewMsels).SeedAsync();

        var users = await GetUsers(Client(actor), MselUsers(Guid.NewGuid()));

        Assert.Empty(users);
    }

    /// <remarks>
    /// <c>MselViewRequirement</c> answers false for a MSEL that is not there rather than throwing, so
    /// this is a clean 403 - and a caller without <c>ViewMsels</c> therefore cannot tell a MSEL that is
    /// gone from one they may not see, while a caller with it gets the empty list above.
    /// </remarks>
    [Fact]
    public async Task GetByMsel_ForAMselThatIsNotThere_WithoutViewMsels_Is403()
    {
        var actor = await Actor().SeedAsync();

        var response = await Client(actor).GetAsync(MselUsers(Guid.NewGuid()), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task GetByMsel_DoesNotReturnAnotherMselsUsers()
    {
        var msel = await SeedMsel();
        var other = await SeedMsel();
        var elsewhere = await Actor().OnMsel(other, MselRole.Viewer).SeedAsync();
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewMsels).SeedAsync();

        var users = await GetUsers(Client(actor), MselUsers(msel.Id));

        Assert.DoesNotContain(elsewhere.Id, users.Select(x => x.Id));
    }

    // ---------------------------------------------------------------------------------------------
    // GET teams/{teamId}/users
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task GetByTeam_ReturnsTheTeamsMembers()
    {
        var msel = await SeedMsel();
        var team = await SeedTeam(msel.Id);
        var member = await Actor().OnTeam(team).SeedAsync();
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewMsels).SeedAsync();

        var users = await GetUsers(Client(actor), TeamUsers(team.Id));

        Assert.Equal(member.Id, Assert.Single(users).Id);
    }

    [Fact]
    public async Task GetByTeam_ForAViewerOfTheMsel_Is200()
    {
        var msel = await SeedMsel();
        var team = await SeedTeam(msel.Id);
        var actor = await Actor().OnMsel(msel, MselRole.Viewer).OnTeam(team).SeedAsync();

        var users = await GetUsers(Client(actor), TeamUsers(team.Id));

        Assert.Equal(actor.Id, Assert.Single(users).Id);
    }

    [Fact]
    public async Task GetByTeam_WithNoRoleOnTheMsel_Is403()
    {
        var msel = await SeedMsel();
        var team = await SeedTeam(msel.Id);
        var actor = await Actor().SeedAsync();

        var response = await Client(actor).GetAsync(TeamUsers(team.Id), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <remarks>
    /// <c>GetByTeamAsync</c> reads the team before it decides anything, so the answer is the same for a
    /// caller who holds <c>ViewMsels</c> and one who holds nothing. That is the right shape, and it is
    /// the opposite of <c>GetAsync(id, …)</c> four routes up
    /// (<see cref="Get_ForAnIdThatIsNotThere_WithoutViewUsers_Is403"/>) - one controller, two answers to
    /// the same question. The lookup takes no <c>CancellationToken</c>.
    /// </remarks>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task GetByTeam_ForATeamThatIsNotThere_Is404ForEverybody(bool hasViewMsels)
    {
        var builder = Actor();

        if (hasViewMsels)
        {
            builder = builder.WithSystemPermissions(SystemPermission.ViewMsels);
        }

        var actor = await builder.SeedAsync();

        var response = await Client(actor).GetAsync(TeamUsers(Guid.NewGuid()), Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetByTeam_DoesNotReturnAnotherTeamsMembers()
    {
        var msel = await SeedMsel();
        var team = await SeedTeam(msel.Id);
        var other = await SeedTeam(msel.Id);
        var elsewhere = await Actor().OnTeam(other).SeedAsync();
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewMsels).SeedAsync();

        var users = await GetUsers(Client(actor), TeamUsers(team.Id));

        Assert.DoesNotContain(elsewhere.Id, users.Select(x => x.Id));
    }

    // ---------------------------------------------------------------------------------------------
    // GET units/{unitId}/users
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task GetByUnit_WithViewUnits_ReturnsTheUnitsMembers()
    {
        var unit = BlueprintAppFactory.Unit();
        await Seed(unit);
        var member = BlueprintAppFactory.User();
        await Seed(member);
        await Seed(BlueprintAppFactory.UnitUser(member.Id, unit.Id));
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewUnits).SeedAsync();

        var users = await GetUsers(Client(actor), UnitUsers(unit.Id));

        Assert.Equal(member.Id, Assert.Single(users).Id);
    }

    /// <remarks>
    /// <c>GetByUnitAsync</c> is the only method on the service with no permission argument at all - the
    /// controller refuses outright at <c>:132-133</c> and the service then asks nothing about the unit.
    /// So a member of the unit cannot list the people they are in it with, while a <c>ViewUnits</c>
    /// holder with no connection to it can list any unit in the installation, which is the same shape as
    /// <c>GET unitusers</c> having no filter (<c>977f578</c>).
    /// </remarks>
    [Fact]
    public async Task GetByUnit_WithoutViewUnits_Is403EvenForAMemberOfTheUnit()
    {
        var unit = BlueprintAppFactory.Unit();
        await Seed(unit);
        var actor = await Actor().SeedAsync();
        await Seed(BlueprintAppFactory.UnitUser(actor.Id, unit.Id));

        var response = await Client(actor).GetAsync(UnitUsers(unit.Id), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <remarks>
    /// No existence check, so an unknown unit is indistinguishable from an empty one. Seeds a membership
    /// in a real unit that must not appear, so an inverted filter reddens this.
    /// </remarks>
    [Fact]
    public async Task GetByUnit_ForAUnitThatIsNotThere_IsAnEmptyList()
    {
        var unit = BlueprintAppFactory.Unit();
        await Seed(unit);
        var member = BlueprintAppFactory.User();
        await Seed(member);
        await Seed(BlueprintAppFactory.UnitUser(member.Id, unit.Id));
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewUnits).SeedAsync();

        var users = await GetUsers(Client(actor), UnitUsers(Guid.NewGuid()));

        Assert.Empty(users);
    }

    // ---------------------------------------------------------------------------------------------
    // POST users
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Create_WithAnId_Is201()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageUsers).SeedAsync();
        var id = Guid.NewGuid();

        var response = await Post(Client(actor), new UserBody { Id = id, Name = "newcomer" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.EndsWith($"/api/users/{id}", response.Headers.Location?.ToString());

        var created = await Read<ViewModels.User>(response);

        Assert.Equal(id, created.Id);
        Assert.Equal("newcomer", created.Name);
        Assert.Equal("newcomer", (await Stored(id)).Name);
    }

    /// <remarks>
    /// The headline. <c>CreateAsync</c> saves the entity, then re-reads by
    /// <c>user.Id</c> - the id on the incoming view model, which is all zeros here - and answers null,
    /// which <c>UserController.cs:159</c> dereferences building the <c>Location</c> header. The user
    /// exists (asserted below, by a second request that finds them) and the <c>UserCreated</c>
    /// broadcast has already gone out, so the only party told the request failed is the one that made
    /// it. Returning <c>GetAsync(userEntity.Id, …)</c> is the one-word fix, and
    /// <c>UserMselRoleService.CreateAsync</c> already writes it that way.
    /// </remarks>
    [Fact]
    public async Task Create_ThatOmitsTheId_CreatesTheUserAndAnswers500()
    {
        var actor = await Actor().WithSystemPermissions(
            SystemPermission.ManageUsers, SystemPermission.ViewUsers).SeedAsync();

        var response = await Post(Client(actor), new { name = "nameless" });

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);

        var stored = await StoredUsers();

        Assert.Contains("nameless", stored.Select(x => x.Name));
        Assert.NotEqual(Guid.Empty, stored.Single(x => x.Name == "nameless").Id);

        var users = await GetUsers(Client(actor));

        Assert.Contains("nameless", users.Select(x => x.Name));
    }

    [Fact]
    public async Task Create_WithManageUsersOnly_Is201()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageUsers).SeedAsync();

        var response = await Post(Client(actor), new UserBody { Id = Guid.NewGuid(), Name = "n" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task Create_WithoutManageUsers_Is403()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewUsers).SeedAsync();

        var response = await Post(Client(actor), new UserBody { Id = Guid.NewGuid(), Name = "n" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.DoesNotContain("n", (await StoredUsers()).Select(x => x.Name));
    }

    /// <remarks>
    /// The body's audit fields are ignored twice over: <c>CreateAsync</c> overwrites <c>CreatedBy</c>
    /// with the caller (the controller's own assignment at <c>:157</c> being dead code that sets the
    /// same value) and <c>BlueprintContext.SaveEntries</c> stamps the dates. A hostile 1999 date and a
    /// bogus creator both disappear.
    /// </remarks>
    [Fact]
    public async Task Create_StampsTheAuditFieldsOnTheServer()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageUsers).SeedAsync();
        var id = Guid.NewGuid();
        var before = DateTime.UtcNow;

        var response = await Post(Client(actor), new UserBody
        {
            Id = id,
            Name = "stamped",
            CreatedBy = Guid.NewGuid(),
            DateCreated = new DateTime(1999, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            ModifiedBy = Guid.NewGuid(),
            DateModified = new DateTime(1999, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var stored = await Stored(id);

        Assert.Equal(actor.Id, stored.CreatedBy);
        Assert.InRange(stored.DateCreated, before, DateTime.UtcNow);
        Assert.Null(stored.ModifiedBy);
        Assert.Null(stored.DateModified);
    }

    /// <remarks>
    /// <c>UserHandler.GetGroups</c> names the new user's <c>CreatedBy</c> - the caller - and
    /// <c>ADMIN_DATA_GROUP</c>. <c>MainHub.cs:254</c> puts every <c>ViewUsers</c> holder in
    /// <c>USER_GROUP</c>, and this asserts that group hears nothing, here or anywhere: no handler among
    /// the 25 sends to it. So a client watching for users has to subscribe to the group that receives
    /// every notification in the application instead, and filter.
    /// </remarks>
    [Fact]
    public async Task Create_TellsTheCreatorsOwnGroupAndTheAdminDataGroup_NeverTheUserGroup()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageUsers).SeedAsync();

        var response = await Post(Client(actor), new UserBody { Id = Guid.NewGuid(), Name = "n" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var recipients = Hub.Recipients(MainHubMethods.UserCreated);

        Assert.Contains(actor.Id.ToString(), recipients);
        Assert.Contains(MainHub.ADMIN_DATA_GROUP, recipients);
        Assert.DoesNotContain(MainHub.USER_GROUP, Hub.Sends.Select(x => x.Group));
    }

    /// <remarks>
    /// Half of the escalation, from the other side: <c>RoleId</c> is an ordinary mapped property, so a
    /// <c>ManageUsers</c> holder may mint an administrator. The assertion is not that the column was
    /// written but that the new user's next request is allowed - <c>GET system-roles</c> requires
    /// <c>ViewRoles</c>, which they hold through <c>AllPermissions</c> and nothing else.
    /// </remarks>
    [Fact]
    public async Task Create_MayGiveTheNewUserEveryPermissionInTheInstallation()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageUsers).SeedAsync();
        var id = Guid.NewGuid();

        var response = await Post(Client(actor), new UserBody
        {
            Id = id,
            Name = "administrator",
            RoleId = SystemRoleDefaults.AdministratorRoleId
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var theirs = await Client(id, "administrator").GetAsync("/api/system-roles", Ct);

        Assert.Equal(HttpStatusCode.OK, theirs.StatusCode);
    }

    // ---------------------------------------------------------------------------------------------
    // PUT users/{id}
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Update_RenamesTheUser_Is200()
    {
        var subject = BlueprintAppFactory.User(name: "before");
        await Seed(subject);
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageUsers).SeedAsync();

        var response = await Put(Client(actor), subject.Id, BodyFor(subject) with { Name = "after" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("after", (await Read<ViewModels.User>(response)).Name);
        Assert.Equal("after", (await Stored(subject.Id)).Name);
    }

    [Fact]
    public async Task Update_WithManageUsersOnly_Is200()
    {
        var subject = BlueprintAppFactory.User();
        await Seed(subject);
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageUsers).SeedAsync();

        var response = await Put(Client(actor), subject.Id, BodyFor(subject) with { Name = "renamed" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Update_WithoutManageUsers_Is403()
    {
        var subject = BlueprintAppFactory.User(name: "before");
        await Seed(subject);
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewUsers).SeedAsync();

        var response = await Put(Client(actor), subject.Id, BodyFor(subject) with { Name = "after" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("before", (await Stored(subject.Id)).Name);
    }

    /// <remarks>
    /// Existence is checked after the permission but the route needs <c>ManageUsers</c> either way, so
    /// there is no 403-for-a-stranger variant to contrast this with - a caller who may not update
    /// anything cannot reach the lookup at all.
    /// </remarks>
    [Fact]
    public async Task Update_ForAnIdThatIsNotThere_Is404()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageUsers).SeedAsync();
        var id = Guid.NewGuid();

        var response = await Put(Client(actor), id, new UserBody { Id = id, Name = "ghost" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <remarks>
    /// The headline. The caller holds <c>ManageUsers</c> and nothing else - no <c>ViewRoles</c>, which
    /// is why the first request is 403 - and puts <c>SystemRoleDefaults.AdministratorRoleId</c> on their
    /// own row. The second identical request is 200, because the seeded Administrator role has
    /// <c>AllPermissions = true</c> and <c>UserClaimsService.cs:209</c> expands that to every value of
    /// the enum. The route exists to administer other people's accounts and administers the caller's own
    /// just as happily; <c>UpdateAsync</c>'s only guard is about ids
    /// (<see cref="Update_ThatChangesTheCallersOwnId_Is403"/>).
    /// <para />
    /// The claims cache is disabled in this harness, which is why the second request sees the new role
    /// immediately. In a shipped installation it would be seen once the cache entry expired, and no
    /// sooner, because nothing in <c>UserService</c> invalidates it.
    /// </remarks>
    [Fact]
    public async Task Update_MayGiveTheCallerEveryPermissionInTheInstallation()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageUsers).SeedAsync();
        var client = Client(actor);

        Assert.Equal(
            HttpStatusCode.Forbidden, (await client.GetAsync("/api/system-roles", Ct)).StatusCode);

        var response = await Put(client, actor.Id, new UserBody
        {
            Id = actor.Id,
            Name = actor.Name,
            RoleId = SystemRoleDefaults.AdministratorRoleId
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/system-roles", Ct)).StatusCode);
    }

    /// <remarks>
    /// The one thing the route refuses, and the guard the unit unit found copy-pasted onto a unit id
    /// (<c>UnitService.cs:104-108</c>, where it compares a <em>unit</em> id against the caller's user id
    /// and can therefore only fire by coincidence). Here it is in its right place.
    /// </remarks>
    [Fact]
    public async Task Update_ThatChangesTheCallersOwnId_Is403()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageUsers).SeedAsync();

        var response = await Put(Client(actor), actor.Id, new UserBody
        {
            Id = Guid.NewGuid(),
            Name = actor.Name
        });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.NotNull(await Stored(actor.Id));
    }

    /// <remarks>
    /// The same request against somebody else's row is not refused. <c>UserProfile</c> maps <c>Id</c>
    /// both ways, so <c>_mapper.Map(user, userToUpdate)</c> writes the body's id onto a tracked entity
    /// and EF declines to modify a key - an <c>InvalidOperationException</c>, so a 500 rather than the
    /// 400 a rejected request should be. Guarding the mismatch for every id, not just the caller's own,
    /// is the fix, and it is the same line.
    /// </remarks>
    [Fact]
    public async Task Update_ThatChangesSomebodyElsesId_Is500()
    {
        var subject = BlueprintAppFactory.User(name: "before");
        await Seed(subject);
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageUsers).SeedAsync();

        var response = await Put(
            Client(actor), subject.Id, BodyFor(subject) with { Id = Guid.NewGuid(), Name = "after" });

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("before", (await Stored(subject.Id)).Name);
    }

    /// <remarks>
    /// <c>SaveEntries</c> restores <c>CreatedBy</c> and <c>DateCreated</c> from
    /// <c>entry.OriginalValues</c> on a modified entry (<c>BlueprintContext.cs:150-151</c>), so a body
    /// naming somebody else as creator changes nothing. The seam for this test is the context, not the
    /// service.
    /// </remarks>
    [Fact]
    public async Task Update_PreservesTheCreationFields()
    {
        var creator = Guid.NewGuid();
        var subject = BlueprintAppFactory.User(createdBy: creator);
        await Seed(subject);
        var created = (await Stored(subject.Id)).DateCreated;
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageUsers).SeedAsync();

        var response = await Put(Client(actor), subject.Id, BodyFor(subject) with
        {
            Name = "renamed",
            CreatedBy = Guid.NewGuid(),
            DateCreated = new DateTime(1999, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var stored = await Stored(subject.Id);

        Assert.Equal(creator, stored.CreatedBy);
        Assert.Equal(created, stored.DateCreated);
    }

    /// <remarks>
    /// <c>SaveEntries</c> stamps <c>DateModified</c> and never touches <c>ModifiedBy</c>, so the
    /// modifier is whatever the service assigned - here the caller, at <c>UserService.cs:166</c> (the
    /// controller's assignment at <c>:182</c> being dead code that sets the same value). A user route
    /// therefore records who made the change, where a PUT to a team does not
    /// (<c>4dd5201</c>): same context, different service.
    /// </remarks>
    [Fact]
    public async Task Update_RecordsTheCallerAsTheModifier()
    {
        var subject = BlueprintAppFactory.User();
        await Seed(subject);
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageUsers).SeedAsync();
        var before = DateTime.UtcNow;

        var response = await Put(Client(actor), subject.Id, BodyFor(subject) with
        {
            Name = "renamed",
            ModifiedBy = Guid.NewGuid()
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var stored = await Stored(subject.Id);

        Assert.Equal(actor.Id, stored.ModifiedBy);
        Assert.InRange(stored.DateModified.Value, before, DateTime.UtcNow);
    }

    // ---------------------------------------------------------------------------------------------
    // DELETE users/{id}
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Delete_Is204()
    {
        var subject = BlueprintAppFactory.User();
        await Seed(subject);
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageUsers).SeedAsync();

        var response = await Client(actor).DeleteAsync(UserRoute(subject.Id), Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Null(await Stored(subject.Id));
    }

    [Fact]
    public async Task Delete_WithManageUsersOnly_Is204()
    {
        var subject = BlueprintAppFactory.User();
        await Seed(subject);
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageUsers).SeedAsync();

        var response = await Client(actor).DeleteAsync(UserRoute(subject.Id), Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task Delete_WithoutManageUsers_Is403()
    {
        var subject = BlueprintAppFactory.User();
        await Seed(subject);
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewUsers).SeedAsync();

        var response = await Client(actor).DeleteAsync(UserRoute(subject.Id), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.NotNull(await Stored(subject.Id));
    }

    [Fact]
    public async Task Delete_ForAnIdThatIsNotThere_Is404()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageUsers).SeedAsync();

        var response = await Client(actor).DeleteAsync(UserRoute(Guid.NewGuid()), Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <remarks>
    /// The other half of the copy-pasted guard: "You cannot delete your own account", here about an
    /// account. <c>UnitService.DeleteAsync</c> carries the same sentence about a <em>unit</em>, so a unit
    /// whose id happens to equal some user's id is undeletable by that user and by nobody else
    /// (<c>977f578</c>). It fires before the existence check, which for the caller's own id can never
    /// matter.
    /// </remarks>
    [Fact]
    public async Task Delete_ForTheCallersOwnAccount_Is403()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageUsers).SeedAsync();

        var response = await Client(actor).DeleteAsync(UserRoute(actor.Id), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.NotNull(await Stored(actor.Id));
    }

    /// <remarks>
    /// The cascade is the database's, declared by the migrations rather than by any
    /// <c>IEntityTypeConfiguration</c>, and it takes the role rows with the membership rows - so
    /// deleting a person is the one operation in the application that does not leave orphaned
    /// <c>UserMselRoleEntity</c> rows behind (compare <c>MselUnitService.DeleteAsync</c>, which leaves
    /// every one of them). Nothing warns that a delete removes the person from live exercises, and
    /// nothing tells those exercises' clients: the <c>UserDeleted</c> broadcast names the user's
    /// <c>CreatedBy</c> group and <c>ADMIN_DATA_GROUP</c>, never the MSELs they were on.
    /// </remarks>
    [Fact]
    public async Task Delete_TakesTheirMembershipsAndMselRolesWithThem()
    {
        var msel = await SeedMsel();
        var team = await SeedTeam(msel.Id);
        var subject = await Actor().OnMsel(msel, MselRole.Viewer).OnTeam(team).SeedAsync();
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageUsers).SeedAsync();

        var response = await Client(actor).DeleteAsync(UserRoute(subject.Id), Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        await using var context = NewContext();

        Assert.Empty(await context.TeamUsers.Where(x => x.UserId == subject.Id).ToListAsync(Ct));
        Assert.Empty(await context.UnitUsers.Where(x => x.UserId == subject.Id).ToListAsync(Ct));
        Assert.Empty(await context.UserMselRoles.Where(x => x.UserId == subject.Id).ToListAsync(Ct));
    }

    // ---------------------------------------------------------------------------------------------
    // Authentication
    // ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("GET", "users")]
    [InlineData("GET", "users/00000000-0000-0000-0000-000000000001")]
    [InlineData("GET", "msels/00000000-0000-0000-0000-000000000001/users")]
    [InlineData("GET", "teams/00000000-0000-0000-0000-000000000001/users")]
    [InlineData("GET", "units/00000000-0000-0000-0000-000000000001/users")]
    [InlineData("POST", "users")]
    [InlineData("PUT", "users/00000000-0000-0000-0000-000000000001")]
    [InlineData("DELETE", "users/00000000-0000-0000-0000-000000000001")]
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

    private const string Users = "/api/users";

    private static string UserRoute(Guid id) => $"{Users}/{id}";

    private static string MselUsers(Guid mselId) => $"/api/msels/{mselId}/users";

    private static string TeamUsers(Guid teamId) => $"/api/teams/{teamId}/users";

    private static string UnitUsers(Guid unitId) => $"/api/units/{unitId}/users";

    /// <summary>
    /// The wire shape of a user. The two non-nullable audit properties are non-nullable here too,
    /// because <c>ViewModels.Base</c> declares them so and a body sending null for either is a 400 that
    /// never reaches the controller.
    /// </summary>
    private sealed record UserBody
    {
        public Guid Id { get; init; }
        public string Name { get; init; }
        public Guid? RoleId { get; init; }

        public Guid CreatedBy { get; init; }
        public DateTime DateCreated { get; init; }
        public Guid? ModifiedBy { get; init; }
        public DateTime? DateModified { get; init; }
    }

    /// <summary>
    /// The body a well-behaved client sends for a PUT: the stored row, echoed back. Vary it with
    /// <c>with</c> rather than adding optional parameters, so that a test about an omitted property can
    /// say so.
    /// </summary>
    private static UserBody BodyFor(UserEntity user) => new()
    {
        Id = user.Id,
        Name = user.Name,
        RoleId = user.RoleId,
        CreatedBy = user.CreatedBy,
        DateCreated = user.DateCreated
    };

    private async Task<MselEntity> SeedMsel()
    {
        var msel = BlueprintAppFactory.Msel();
        await Seed(msel);

        return msel;
    }

    private async Task<TeamEntity> SeedTeam(Guid mselId)
    {
        var team = BlueprintAppFactory.Team(mselId);
        await Seed(team);

        return team;
    }

    private Task<HttpResponseMessage> Post(HttpClient client, object body) =>
        client.PostAsJsonAsync(Users, body, Ct);

    private Task<HttpResponseMessage> Put(HttpClient client, Guid id, UserBody body) =>
        client.PutAsJsonAsync(UserRoute(id), body, Ct);

    private Task<List<ViewModels.User>> GetUsers(HttpClient client) => GetUsers(client, Users);

    private async Task<List<ViewModels.User>> GetUsers(HttpClient client, string route)
    {
        var response = await client.GetAsync(route, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await Read<List<ViewModels.User>>(response);
    }

    private async Task<ViewModels.User> GetUser(HttpClient client, Guid id)
    {
        var response = await client.GetAsync(UserRoute(id), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await Read<ViewModels.User>(response);
    }

    private async Task<UserEntity> Stored(Guid id)
    {
        await using var context = NewContext();

        return await context.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, Ct);
    }

    private async Task<List<UserEntity>> StoredUsers()
    {
        await using var context = NewContext();

        return await context.Users.AsNoTracking().ToListAsync(Ct);
    }

    private async Task<T> Read<T>(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync(Ct);

        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {body}");

        return JsonSerializer.Deserialize<T>(body, JsonOptions);
    }
}
