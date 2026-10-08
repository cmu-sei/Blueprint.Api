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
using Blueprint.Api.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Blueprint.Api.Tests.Controllers;

/// <summary><c>TeamUserService</c> / <c>TeamUserController</c> - the six routes behind the join row that
/// puts a user on a team, and the membership every <c>MselViewRequirement</c> and
/// <c>MselUserRequirement</c> check falls back on.</summary>
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
    public async Task GetByMsel_is_forbidden_for_a_caller_holding_Owner_only_in_another_msel()
    {
        var msel = await SeedMsel();
        var actor = await Actor().OnNewMsel(MselRole.Owner).SeedAsync();

        var response = await Client(actor).GetAsync(TeamUsersOf(msel.Id), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>A member of a unit on the MSEL, with no role, lists every team member of the MSEL.</summary>
    [Fact]
    public async Task GetByMsel_ForAUnitMemberWithNoRole_Is200()
    {
        var msel = await SeedMsel();
        await Actor().OnTeam(await SeedTeam(msel)).SeedAsync();
        var actor = await Actor().InUnitOf(msel).SeedAsync();

        Assert.Single(await GetRows(Client(actor), TeamUsersOf(msel.Id)));
    }

    /// <summary>Get by MSEL for a MSEL that is not there is answered with a 500 for a caller without ViewMsels.</summary>
    [Fact]
    public async Task GetByMsel_ForAMselThatIsNotThere_Is500()
    {
        var stranger = await Actor().SeedAsync();

        var response = await Client(stranger).GetAsync(TeamUsersOf(Guid.NewGuid()), Ct);

        var failure = await AssertJsonError<Blueprint.Api.ViewModels.ApiError>(HttpStatusCode.InternalServerError, response);
        Assert.Equal("Object reference not set to an instance of an object.", failure.Title);
        Assert.Contains("MselUserRequirement.IsMet", failure.Detail);
    }

    [Fact]
    public async Task GetByMsel_ForAMselThatIsNotThere_WithViewMsels_IsAnEmptyList()
    {
        var privileged = await Actor().WithSystemPermissions(SystemPermission.ViewMsels).SeedAsync();

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

    /// <summary>Get by team for a team that is not there is answered with a 404 for a stranger too.</summary>
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
    public async Task GetByTeam_is_forbidden_for_a_caller_holding_Owner_only_in_another_msel()
    {
        var msel = await SeedMsel();
        var team = await SeedTeam(msel);
        var actor = await Actor().OnNewMsel(MselRole.Owner).SeedAsync();

        var response = await Client(actor).GetAsync(TeamUsersOfTeam(team.Id), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>Get by team for a member of one team lists every team on the MSEL.</summary>
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
    public async Task Get_is_forbidden_for_a_caller_holding_Owner_only_in_another_msel()
    {
        var msel = await SeedMsel();
        var team = await SeedTeam(msel);
        var member = await Actor().OnTeam(team).SeedAsync();
        var row = await StoredFor(member.Id, team.Id);
        var actor = await Actor().OnNewMsel(MselRole.Owner).SeedAsync();

        var response = await Client(actor).GetAsync(TeamUserRoute(row.Id), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>Get for a unit member with no role is answered with a 403 though they may list the same row.</summary>
    [Fact]
    public async Task Get_ForAUnitMemberWithNoRole_Is403_ThoughTheyMayListTheSameRow()
    {
        var msel = await SeedMsel();
        var team = await SeedTeam(msel);
        var member = await Actor().OnTeam(team).SeedAsync();
        var row = await StoredFor(member.Id, team.Id);
        var actor = await Actor().InUnitOf(msel).SeedAsync();

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

    /// <summary>Get answers audit fields the entity does not have.</summary>
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
    public async Task Create_is_forbidden_for_a_caller_holding_Owner_only_in_another_msel()
    {
        var msel = await SeedMsel();
        var team = await SeedTeam(msel);
        var member = await Actor().OnNewMsel(MselRole.Owner).SeedAsync();
        var actor = await Actor().OnNewMsel(MselRole.Owner).SeedAsync();

        var response = await Post(Client(actor), Body(member.Id, team.Id));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Null(await StoredFor(member.Id, team.Id));
    }

    /// <summary>Create for a user already on the team is answered with a 500.</summary>
    [Fact]
    public async Task Create_ForAUserAlreadyOnTheTeam_Is500()
    {
        var msel = await SeedMsel();
        var team = await SeedTeam(msel);
        var member = await Actor().OnTeam(team).SeedAsync();
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        var response = await Post(Client(actor), Body(member.Id, team.Id));

        var failure = await AssertJsonError<Blueprint.Api.ViewModels.ApiError>(HttpStatusCode.InternalServerError, response);
        Assert.Equal("An error occurred while saving the entity changes. See the inner exception for details.", failure.Title);
        Assert.Contains("TeamUserService.CreateAsync", failure.Detail);
    }

    /// <summary>Create for a user that is not there is answered with a 500.</summary>
    [Fact]
    public async Task Create_ForAUserThatIsNotThere_Is500()
    {
        var msel = await SeedMsel();
        var team = await SeedTeam(msel);
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        var response = await Post(Client(actor), Body(Guid.NewGuid(), team.Id));

        var failure = await AssertJsonError<Blueprint.Api.ViewModels.ApiError>(HttpStatusCode.InternalServerError, response);
        Assert.Equal("An error occurred while saving the entity changes. See the inner exception for details.", failure.Title);
        Assert.Contains("TeamUserService.CreateAsync", failure.Detail);
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

    /// <summary>A create keeps the id the body carries.</summary>
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

    /// <summary>A new membership is broadcast to the team, the user and the admin data group.</summary>
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
            Hub.Recipients(MainHubMethods.TeamUserCreated, member.Id, team.Id).Order());
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
            Hub.Recipients(MainHubMethods.TeamUserDeleted, member.Id, team.Id).Order());
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

    /// <summary>A member of the team without a role on the MSEL is refused the delete.</summary>
    [Fact]
    public async Task Delete_is_forbidden_for_a_member_of_the_team_without_a_role_on_the_msel()
    {
        var msel = await SeedMsel();
        var team = await SeedTeam(msel);
        var member = await Actor().OnTeam(team).SeedAsync();
        var row = await StoredFor(member.Id, team.Id);

        var response = await Client(member).DeleteAsync(TeamUserRoute(row.Id), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.NotNull(await StoredFor(member.Id, team.Id));
    }

    /// <summary>An editor of the MSEL is refused the delete: it asks for ownership.</summary>
    [Fact]
    public async Task Delete_is_forbidden_for_an_editor_of_the_msel()
    {
        var msel = await SeedMsel();
        var team = await SeedTeam(msel);
        var member = await Actor().OnTeam(team).SeedAsync();
        var row = await StoredFor(member.Id, team.Id);
        var editor = await Actor().OnMsel(msel, MselRole.Editor).SeedAsync();

        var response = await Client(editor).DeleteAsync(TeamUserRoute(row.Id), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.NotNull(await StoredFor(member.Id, team.Id));
    }

    /// <summary>Delete decides from the stored row's team, so owning another MSEL grants nothing.</summary>
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

    /// <summary>The route passes its ids in the declared order: the right pair is a 204 and the transposed pair
    /// a 404.</summary>
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

        var response = await Client().SendAsync(request, Ct);

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
        var msel = TestData.Msel();
        await Seed(msel);

        return msel;
    }

    private async Task<TeamEntity> SeedTeam(MselEntity msel)
    {
        var team = TestData.Team(msel.Id);
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
