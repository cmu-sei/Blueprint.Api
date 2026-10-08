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
using Blueprint.Api.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Blueprint.Api.Tests.Controllers;

/// <summary><c>UserService</c> / <c>UserController</c> - the eight routes behind the person every other row
/// in the application points at. The layer underneath team and unit membership: those decide what a user reaches, this one decides who exists and what system role they
/// hold, and the system role is what grants all 28 <c>SystemPermission</c>s.</summary>
public class UserEndpointTests(DatabaseFixture fixture, BlueprintAppFactory factory)
    : ApiTestBase(fixture, factory), IClassFixture<BlueprintAppFactory>
{
    // ---------------------------------------------------------------------------------------------
    // GET users
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task GetAll_WithViewUsers_ReturnsEveryUserInTheInstallation()
    {
        var first = TestData.User();
        var second = TestData.User();
        await Seed(first, second);
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewUsers).SeedAsync();

        var users = await GetUsers(Client(actor));

        Assert.Contains(first.Id, users.Select(x => x.Id));
        Assert.Contains(second.Id, users.Select(x => x.Id));
        Assert.Contains(actor.Id, users.Select(x => x.Id));
    }

    /// <summary>Get all for a caller on a team holding no permission at all returns every user in the installation.</summary>
    [Fact]
    public async Task GetAll_ForACallerOnATeamHoldingNoPermissionAtAll_ReturnsEveryUserInTheInstallation()
    {
        var stranger = TestData.User();
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
        await Seed(TestData.User(), TestData.User());
        var actor = await Actor().SeedAsync();

        var users = await GetUsers(Client(actor));

        Assert.Equal(actor.Id, Assert.Single(users).Id);
    }

    /// <summary>A MSEL owner reaching it through a unit, on no team, lists only themselves.</summary>
    [Fact]
    public async Task GetAll_ForAMselOwnerReachingItThroughAUnit_ReturnsOnlyThemselves()
    {
        var stranger = TestData.User();
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
        var subject = TestData.User(name: "vera", roleId: SystemRoleDefaults.ObserverRoleId);
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
        var subject = TestData.User();
        await Seed(subject);
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageUsers).SeedAsync();

        var response = await Client(actor).GetAsync(UserRoute(subject.Id), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>An unknown user is a 404 for a <c>ViewUsers</c> holder, from the controller's check.</summary>
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
    public async Task Get_ForAnIdThatIsNotThere_is_forbidden_for_a_caller_holding_only_ViewRoles()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewRoles).SeedAsync();

        var response = await Client(actor).GetAsync(UserRoute(Guid.NewGuid()), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <remarks>
    /// <c>UserEntity</c> is a <c>BaseEntity</c>, so unlike <c>TeamUserEntity</c> and
    /// <c>UnitUserEntity</c> the four audit properties on the view model are backed by
    /// real columns and the values mean something. The row that says a person exists is audited; the
    /// rows that say what they may reach are not.
    /// </remarks>
    [Fact]
    public async Task Get_AnswersAuditFieldsBackedByRealColumns()
    {
        var creator = Guid.NewGuid();
        var subject = TestData.User(createdBy: creator);
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
    public async Task GetByMsel_is_forbidden_for_a_caller_holding_Owner_only_in_another_msel()
    {
        var msel = await SeedMsel();
        var actor = await Actor().OnNewMsel(MselRole.Owner).SeedAsync();

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
        var msel = TestData.Msel(isTemplate: true);
        await Seed(msel);
        var actor = await Actor().WithSystemPermissions(SystemPermission.CreateMsels).SeedAsync();

        var users = await GetUsers(Client(actor), MselUsers(msel.Id));

        Assert.Empty(users);
    }

    [Fact]
    public async Task GetByMsel_ForATemplate_is_forbidden_for_a_caller_holding_only_EditMsels()
    {
        var msel = TestData.Msel(isTemplate: true);
        await Seed(msel);
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();

        var response = await Client(actor).GetAsync(MselUsers(msel.Id), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>An unknown MSEL's user list is empty for a <c>ViewMsels</c> holder.</summary>
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
    public async Task GetByMsel_ForAMselThatIsNotThere_is_forbidden_for_a_caller_holding_Owner_only_in_another_msel()
    {
        var actor = await Actor().OnNewMsel(MselRole.Owner).SeedAsync();

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
    public async Task GetByTeam_is_forbidden_for_a_caller_holding_Owner_only_in_another_msel()
    {
        var msel = await SeedMsel();
        var team = await SeedTeam(msel.Id);
        var actor = await Actor().OnNewMsel(MselRole.Owner).SeedAsync();

        var response = await Client(actor).GetAsync(TeamUsers(team.Id), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <remarks>
    /// <c>GetByTeamAsync</c> reads the team before it decides anything, so the answer is the same for a
    /// caller who holds <c>ViewMsels</c> and one who holds nothing. That is the right shape, and it is
    /// the opposite of <c>GetAsync(id, …)</c> four routes up
    /// (<see cref="Get_ForAnIdThatIsNotThere_is_forbidden_for_a_caller_holding_only_ViewRoles"/>) - one controller, two answers to
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
        var unit = TestData.Unit();
        await Seed(unit);
        var member = TestData.User();
        await Seed(member);
        await Seed(TestData.UnitUser(member.Id, unit.Id));
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewUnits).SeedAsync();

        var users = await GetUsers(Client(actor), UnitUsers(unit.Id));

        Assert.Equal(member.Id, Assert.Single(users).Id);
    }

    /// <remarks>
    /// <c>GetByUnitAsync</c> is the only method on the service with no permission argument at all - the
    /// controller refuses outright at <c>:132-133</c> and the service then asks nothing about the unit.
    /// So a member of the unit cannot list the people they are in it with, while a <c>ViewUnits</c>
    /// holder with no connection to it can list any unit in the installation, which is the same shape as
    /// <c>GET unitusers</c> having no filter.
    /// </remarks>
    [Fact]
    public async Task GetByUnit_WithoutViewUnits_Is403EvenForAMemberOfTheUnit()
    {
        var unit = TestData.Unit();
        await Seed(unit);
        var actor = await Actor().InUnit(unit).SeedAsync();

        var response = await Client(actor).GetAsync(UnitUsers(unit.Id), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>An unknown unit's user list is empty.</summary>
    [Fact]
    public async Task GetByUnit_ForAUnitThatIsNotThere_IsAnEmptyList()
    {
        var unit = TestData.Unit();
        await Seed(unit);
        var member = TestData.User();
        await Seed(member);
        await Seed(TestData.UnitUser(member.Id, unit.Id));
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

    /// <summary>Create that omits the id creates the user and answers 500.</summary>
    [Fact]
    public async Task Create_ThatOmitsTheId_CreatesTheUserAndAnswers500()
    {
        var actor = await Actor().WithSystemPermissions(
            SystemPermission.ManageUsers, SystemPermission.ViewUsers).SeedAsync();

        var response = await Post(Client(actor), new { name = "nameless" });

        var failure = await AssertJsonError<Blueprint.Api.ViewModels.ApiError>(HttpStatusCode.InternalServerError, response);
        Assert.Equal("Object reference not set to an instance of an object.", failure.Title);
        Assert.Contains("UserController.Create", failure.Detail);

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

    /// <summary>A create ignores the body's audit fields.</summary>
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

        var recipients = Hub.Recipients(MainHubMethods.UserCreated, actor.Id);

        Assert.Contains(actor.Id.ToString(), recipients);
        Assert.Contains(MainHub.ADMIN_DATA_GROUP, recipients);
        Assert.Empty(Hub.ToGroup(MainHub.USER_GROUP));
    }

    // Same case as Update_lets_a_caller_holding_only_ManageUsers_give_itself_the_Administrator_role.
    [Fact]
    public async Task Create_lets_a_caller_holding_only_ManageUsers_create_an_Administrator()
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
        Assert.Equal(SystemRoleDefaults.AdministratorRoleId, (await ReadBack(rb => rb.Users.SingleAsync(x => x.Id == id, Ct))).RoleId);
        Assert.Equal(HttpStatusCode.OK, (await ClientFor(id, "administrator").GetAsync(SystemRolesRoute, Ct)).StatusCode);
    }

    // ---------------------------------------------------------------------------------------------
    // PUT users/{id}
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Update_RenamesTheUser_Is200()
    {
        var subject = TestData.User(name: "before");
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
        var subject = TestData.User();
        await Seed(subject);
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageUsers).SeedAsync();

        var response = await Put(Client(actor), subject.Id, BodyFor(subject) with { Name = "renamed" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Update_WithoutManageUsers_Is403()
    {
        var subject = TestData.User(name: "before");
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

    /// <summary>A caller holding only <c>ManageUsers</c> puts the seeded Administrator role on its own row.</summary>
    [Fact]
    public async Task Update_lets_a_caller_holding_only_ManageUsers_give_itself_the_Administrator_role()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageUsers).SeedAsync();
        var client = Client(actor);

        var response = await Put(client, actor.Id, new UserBody
        {
            Id = actor.Id,
            Name = actor.Name,
            RoleId = SystemRoleDefaults.AdministratorRoleId
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(SystemRoleDefaults.AdministratorRoleId, (await ReadBack(rb => rb.Users.SingleAsync(x => x.Id == actor.Id, Ct))).RoleId);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(SystemRolesRoute, Ct)).StatusCode);
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

    /// <summary>Update that changes somebody elses id is answered with a 500.</summary>
    [Fact]
    public async Task Update_ThatChangesSomebodyElsesId_Is500()
    {
        var subject = TestData.User(name: "before");
        await Seed(subject);
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageUsers).SeedAsync();

        var response = await Put(
            Client(actor), subject.Id, BodyFor(subject) with { Id = Guid.NewGuid(), Name = "after" });

        Assert.Equal("The property 'UserEntity.Id' is part of a key and so cannot be modified or marked as modified. To change the principal of an existing entity with an identifying foreign key, first delete the dependent and invoke 'SaveChanges', and then associate the dependent with the new principal.", (await AssertJsonError<Blueprint.Api.ViewModels.ApiError>(HttpStatusCode.InternalServerError, response)).Title);
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
        var subject = TestData.User(createdBy: creator);
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

    /// <summary>An update records the caller as <c>ModifiedBy</c>.</summary>
    [Fact]
    public async Task Update_RecordsTheCallerAsTheModifier()
    {
        var subject = TestData.User();
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
        var subject = TestData.User();
        await Seed(subject);
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageUsers).SeedAsync();

        var response = await Client(actor).DeleteAsync(UserRoute(subject.Id), Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Null(await Stored(subject.Id));
    }

    [Fact]
    public async Task Delete_WithManageUsersOnly_Is204()
    {
        var subject = TestData.User();
        await Seed(subject);
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageUsers).SeedAsync();

        var response = await Client(actor).DeleteAsync(UserRoute(subject.Id), Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task Delete_WithoutManageUsers_Is403()
    {
        var subject = TestData.User();
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
    /// whose id happens to equal some user's id is undeletable by that user and by nobody else.
    /// It fires before the existence check, which for the caller's own id can never
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

    /// <summary>Deleting a user deletes their memberships and MSEL roles by the cascade.</summary>
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

        var response = await Client().SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ---------------------------------------------------------------------------------------------
    // Harness
    // ---------------------------------------------------------------------------------------------

    private const string Users = "/api/users";

    /// <summary>A route gated on <c>ViewRoles</c>, which the Administrator role holds and <c>ManageUsers</c> does not grant.</summary>
    private const string SystemRolesRoute = "/api/system-roles";

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
        var msel = TestData.Msel();
        await Seed(msel);

        return msel;
    }

    private async Task<TeamEntity> SeedTeam(Guid mselId)
    {
        var team = TestData.Team(mselId);
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
