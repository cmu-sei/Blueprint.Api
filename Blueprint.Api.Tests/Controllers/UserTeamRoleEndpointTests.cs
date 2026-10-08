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

/// <summary><c>UserTeamRoleService</c> / <c>UserTeamRoleController</c> - the four routes behind the role a
/// user holds on a team, closing the team-membership trio alongside <c>TeamEndpointTests</c> and
/// <c>TeamUserEndpointTests</c>.</summary>
public class UserTeamRoleEndpointTests(DatabaseFixture fixture, BlueprintAppFactory factory)
    : ApiTestBase(fixture, factory), IClassFixture<BlueprintAppFactory>
{
    // ---------------------------------------------------------------------------------------------
    // GET msels/{mselId}/userteamroles
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task GetByMsel_ReturnsEveryRoleOnTheMselsTeams()
    {
        var msel = await SeedMsel();
        var first = await SeedTeam(msel);
        var second = await SeedTeam(msel);
        var one = await Actor().OnTeam(first).SeedAsync();
        var two = await Actor().OnTeam(second).SeedAsync();
        await SeedRole(one.Id, first.Id);
        await SeedRole(two.Id, second.Id, "Approver");
        var actor = await Actor().OnMsel(msel, MselRole.Viewer).SeedAsync();

        var roles = await GetRoles(Client(actor), RolesOf(msel.Id));

        Assert.Equal(new[] { one.Id, two.Id }.Order(), roles.Select(x => x.UserId).Order());
        Assert.Equal(new[] { "Approver", "Submitter" }, roles.Select(x => x.Role).Order());
    }

    [Fact]
    public async Task GetByMsel_DoesNotReturnAnotherMselsRoles()
    {
        var msel = await SeedMsel();
        var mine = await SeedTeam(msel);
        var member = await Actor().OnTeam(mine).SeedAsync();
        await SeedRole(member.Id, mine.Id);

        var other = await SeedTeam(await SeedMsel());
        await SeedRole((await Actor().OnTeam(other).SeedAsync()).Id, other.Id);

        var actor = await Actor().OnMsel(msel, MselRole.Viewer).SeedAsync();

        var roles = await GetRoles(Client(actor), RolesOf(msel.Id));

        Assert.Equal(member.Id, Assert.Single(roles).UserId);
    }

    [Fact]
    public async Task GetByMsel_ForAMselWithNoRoles_IsAnEmptyList()
    {
        var msel = await SeedMsel();
        await SeedTeam(msel);
        var actor = await Actor().OnMsel(msel, MselRole.Viewer).SeedAsync();

        Assert.Empty(await GetRoles(Client(actor), RolesOf(msel.Id)));
    }

    [Fact]
    public async Task GetByMsel_WithViewMselsAndNoRoleOnTheMsel_Is200()
    {
        var msel = await SeedMsel();
        var team = await SeedTeam(msel);
        await SeedRole((await Actor().OnTeam(team).SeedAsync()).Id, team.Id);
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewMsels).SeedAsync();

        Assert.Single(await GetRoles(Client(actor), RolesOf(msel.Id)));
    }

    [Fact]
    public async Task GetByMsel_is_forbidden_for_a_caller_holding_Owner_only_in_another_msel()
    {
        var msel = await SeedMsel();
        var actor = await Actor().OnNewMsel(MselRole.Owner).SeedAsync();

        var response = await Client(actor).GetAsync(RolesOf(msel.Id), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <remarks>
    /// <c>MselViewRequirement</c> accepts membership of any team on the MSEL, so every participant may
    /// read every other participant's roles on every other team - the same breadth
    /// <c>TeamUserEndpointTests.GetByTeam_ForAMemberOfOneTeam_ListsEveryTeamOnTheMsel</c> records for the
    /// membership rows, arrived at through a different requirement. Neither route is scoped to the
    /// caller's own team.
    /// </remarks>
    [Fact]
    public async Task GetByMsel_ForAMemberOfOneOfItsTeams_Is200()
    {
        var msel = await SeedMsel();
        var team = await SeedTeam(msel);
        var actor = await Actor().OnTeam(team).SeedAsync();
        await SeedRole(actor.Id, team.Id);

        Assert.Single(await GetRoles(Client(actor), RolesOf(msel.Id)));
    }

    /// <remarks>
    /// The controller resolves <c>CreateMsels</c> as well as <c>ViewMsels</c> and passes both down, and
    /// the four-argument <c>MselViewRequirement</c> overload admits a <c>CreateMsels</c> holder to a
    /// <em>template</em> MSEL only - so somebody about to copy a template may read the roles they are
    /// about to copy. This is the only route in the trio that passes that flag:
    /// <c>TeamController</c>'s list route calls the three-argument overload and
    /// <c>TeamUserController</c>'s routes call <c>MselUserRequirement</c>, so the same caller reading the
    /// same template's teams and team users is refused both.
    /// </remarks>
    [Fact]
    public async Task GetByMsel_WithCreateMselsForATemplate_Is200()
    {
        var template = await SeedMsel(isTemplate: true);
        var team = await SeedTeam(template);
        await SeedRole((await Actor().OnTeam(team).SeedAsync()).Id, team.Id);
        var actor = await Actor().WithSystemPermissions(SystemPermission.CreateMsels).SeedAsync();

        Assert.Single(await GetRoles(Client(actor), RolesOf(template.Id)));
    }

    [Fact]
    public async Task GetByMsel_ForATemplate_is_forbidden_for_a_caller_holding_only_EditMsels()
    {
        var template = await SeedMsel(isTemplate: true);
        var team = await SeedTeam(template);
        await SeedRole((await Actor().OnTeam(team).SeedAsync()).Id, team.Id);
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();

        var response = await Client(actor).GetAsync(RolesOf(template.Id), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task GetByMsel_WithCreateMselsForAMselThatIsNotATemplate_Is403()
    {
        var msel = await SeedMsel();
        var actor = await Actor().WithSystemPermissions(SystemPermission.CreateMsels).SeedAsync();

        var response = await Client(actor).GetAsync(RolesOf(msel.Id), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>Get by MSEL for a MSEL that is not there is answered with a 403.</summary>
    [Fact]
    public async Task GetByMsel_ForAMselThatIsNotThere_Is403()
    {
        var stranger = await Actor().OnNewMsel(MselRole.Owner).SeedAsync();

        var response = await Client(stranger).GetAsync(RolesOf(Guid.NewGuid()), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task GetByMsel_ForAMselThatIsNotThere_WithViewMsels_IsAnEmptyList()
    {
        var privileged = await Actor().WithSystemPermissions(SystemPermission.ViewMsels).SeedAsync();

        Assert.Empty(await GetRoles(Client(privileged), RolesOf(Guid.NewGuid())));
    }

    // ---------------------------------------------------------------------------------------------
    // GET userteamroles/{id}
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Get_ReturnsTheRole()
    {
        var msel = await SeedMsel();
        var team = await SeedTeam(msel);
        var member = await Actor().OnTeam(team).SeedAsync();
        var role = await SeedRole(member.Id, team.Id);
        var actor = await Actor().OnMsel(msel, MselRole.Viewer).SeedAsync();

        var answered = await GetRole(Client(actor), role.Id);

        Assert.Equal(role.Id, answered.Id);
        Assert.Equal(member.Id, answered.UserId);
        Assert.Equal(team.Id, answered.TeamId);
        Assert.Equal("Submitter", answered.Role);
    }

    /// <summary>Get for an id that is not there is answered with a 500 for a caller without ViewMsels.</summary>
    [Fact]
    public async Task Get_ForAnIdThatIsNotThere_WithoutViewMsels_Is500()
    {
        var stranger = await Actor().SeedAsync();

        var response = await Client(stranger).GetAsync(RoleRoute(Guid.NewGuid()), Ct);

        var failure = await AssertJsonError<Blueprint.Api.ViewModels.ApiError>(HttpStatusCode.InternalServerError, response);
        Assert.Equal("Object reference not set to an instance of an object.", failure.Title);
        Assert.Contains("UserTeamRoleService.GetAsync", failure.Detail);
    }

    [Fact]
    public async Task Get_ForAnIdThatIsNotThere_WithViewMsels_Is404()
    {
        var privileged = await Actor().WithSystemPermissions(SystemPermission.ViewMsels).SeedAsync();

        var response = await Client(privileged).GetAsync(RoleRoute(Guid.NewGuid()), Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Get_WithViewMselsAndNoRoleOnTheMsel_Is200()
    {
        var msel = await SeedMsel();
        var team = await SeedTeam(msel);
        var member = await Actor().OnTeam(team).SeedAsync();
        var role = await SeedRole(member.Id, team.Id);
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewMsels).SeedAsync();

        Assert.Equal(role.Id, (await GetRole(Client(actor), role.Id)).Id);
    }

    [Fact]
    public async Task Get_is_forbidden_for_a_caller_holding_Owner_only_in_another_msel()
    {
        var msel = await SeedMsel();
        var team = await SeedTeam(msel);
        var member = await Actor().OnTeam(team).SeedAsync();
        var role = await SeedRole(member.Id, team.Id);
        var actor = await Actor().OnNewMsel(MselRole.Owner).SeedAsync();

        var response = await Client(actor).GetAsync(RoleRoute(role.Id), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Get_on_a_template_with_CreateMsels_returns_the_role()
    {
        var msel = await SeedMsel(isTemplate: true);
        var team = await SeedTeam(msel);
        var subject = TestData.User();
        await Seed(subject);
        var role = await SeedRole(subject.Id, team.Id);
        var actor = await Actor().WithSystemPermissions(SystemPermission.CreateMsels).SeedAsync();

        Assert.Equal(role.Id, (await GetRole(Client(actor), role.Id)).Id);
    }

    [Fact]
    public async Task Get_on_a_template_is_forbidden_for_a_caller_holding_only_EditMsels()
    {
        var msel = await SeedMsel(isTemplate: true);
        var team = await SeedTeam(msel);
        var subject = TestData.User();
        await Seed(subject);
        var role = await SeedRole(subject.Id, team.Id);
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();

        var response = await Client(actor).GetAsync(RoleRoute(role.Id), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Get_on_a_msel_that_is_not_a_template_is_forbidden_for_a_caller_holding_only_CreateMsels()
    {
        var msel = await SeedMsel();
        var team = await SeedTeam(msel);
        var subject = TestData.User();
        await Seed(subject);
        var role = await SeedRole(subject.Id, team.Id);
        var actor = await Actor().WithSystemPermissions(SystemPermission.CreateMsels).SeedAsync();

        var response = await Client(actor).GetAsync(RoleRoute(role.Id), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Get_ForAMemberOfTheTeam_Is200()
    {
        var msel = await SeedMsel();
        var team = await SeedTeam(msel);
        var actor = await Actor().OnTeam(team).SeedAsync();
        var role = await SeedRole(actor.Id, team.Id);

        Assert.Equal(role.Id, (await GetRole(Client(actor), role.Id)).Id);
    }

    // ---------------------------------------------------------------------------------------------
    // POST userteamroles
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Create_StoresTheRoleAndAnswers201()
    {
        var msel = await SeedMsel();
        var team = await SeedTeam(msel);
        var member = await Actor().OnTeam(team).SeedAsync();
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        var response = await Post(Client(actor), Body(member.Id, team.Id));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var created = await Read<ViewModels.UserTeamRole>(response);

        Assert.EndsWith($"/api/userteamroles/{created.Id}", response.Headers.Location?.ToString());
        Assert.Equal("Submitter", created.Role);
        Assert.Single(await StoredFor(team.Id));
    }

    [Fact]
    public async Task Create_ForATeamThatIsNotThere_Is404()
    {
        var member = await Actor().SeedAsync();
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();

        var response = await Post(Client(actor), Body(member.Id, Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <remarks>
    /// The minimum is <c>EditMsels</c> - a third permission for the third half of one conceptual
    /// operation, after <c>EditMsels</c> for the team itself and <c>ManageUsers</c> for the membership.
    /// A <c>ManageUsers</c> holder may put a user on a team and not say what they do there; an
    /// <c>EditMsels</c> holder may say what they do there without being able to put them on it.
    /// </remarks>
    [Fact]
    public async Task Create_WithEditMselsAndNoRoleOnTheMsel_Is201()
    {
        var team = await SeedTeam(await SeedMsel());
        var member = await Actor().SeedAsync();
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();

        var response = await Post(Client(actor), Body(member.Id, team.Id));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    /// <remarks>
    /// The other branch is <c>MselOwnerRequirement</c>, so the MSEL's <c>Editor</c> is refused although
    /// the system permission the route asks for is named <c>EditMsels</c>.
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
        var member = await Actor().OnTeam(team).SeedAsync();

        var response = await Post(Client(member), Body(member.Id, team.Id));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(await StoredFor(team.Id));
    }

    /// <summary>Create for the same user team and role twice is answered with a 500.</summary>
    [Fact]
    public async Task Create_ForTheSameUserTeamAndRoleTwice_Is500()
    {
        var msel = await SeedMsel();
        var team = await SeedTeam(msel);
        var member = await Actor().OnTeam(team).SeedAsync();
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        await Post(Client(actor), Body(member.Id, team.Id));

        var response = await Post(Client(actor), Body(member.Id, team.Id));

        var failure = await AssertJsonError<Blueprint.Api.ViewModels.ApiError>(HttpStatusCode.InternalServerError, response);
        Assert.Equal("An error occurred while saving the entity changes. See the inner exception for details.", failure.Title);
        Assert.Contains("UserTeamRoleService.CreateAsync", failure.Detail);
    }

    /// <remarks>
    /// One user may hold any number of roles on one team, and nothing in the API reports the set as a
    /// unit - so an interface offering a single role per member has to delete before it grants, and
    /// nothing makes it.
    /// </remarks>
    [Fact]
    public async Task Create_ForTheSameUserAndTeamWithADifferentRole_Is201()
    {
        var msel = await SeedMsel();
        var team = await SeedTeam(msel);
        var member = await Actor().OnTeam(team).SeedAsync();
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        await Post(Client(actor), Body(member.Id, team.Id));

        var response = await Post(Client(actor), Body(member.Id, team.Id) with { Role = "Approver" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(2, (await StoredFor(team.Id)).Count);
    }

    /// <summary>Create with a role name nothing recognizes is answered with a 201.</summary>
    [Fact]
    public async Task Create_WithARoleNameNothingRecognizes_Is201()
    {
        var msel = await SeedMsel();
        var team = await SeedTeam(msel);
        var member = await Actor().OnTeam(team).SeedAsync();
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        var created = await Read<ViewModels.UserTeamRole>(
            await Post(Client(actor), Body(member.Id, team.Id) with { Role = "Grand Poobah" }));

        Assert.Equal("Grand Poobah", created.Role);
    }

    /// <remarks>
    /// No membership check anywhere: the service loads the team and asks about its MSEL, never about the
    /// user. So the row names a user no team-user query answers, and <c>IntegrationCiteExtensions</c>
    /// will never reach it - it walks the team's users and looks their roles up, not the other way round.
    /// A grant to a non-member is therefore accepted and then ignored.
    /// </remarks>
    [Fact]
    public async Task Create_ForAUserNotOnTheTeam_Is201()
    {
        var msel = await SeedMsel();
        var team = await SeedTeam(msel);
        var stranger = await Actor().SeedAsync();
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        var response = await Post(Client(actor), Body(stranger.Id, team.Id));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task Create_ForAUserThatIsNotThere_Is500()
    {
        var msel = await SeedMsel();
        var team = await SeedTeam(msel);
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        var response = await Post(Client(actor), Body(Guid.NewGuid(), team.Id));

        var failure = await AssertJsonError<Blueprint.Api.ViewModels.ApiError>(HttpStatusCode.InternalServerError, response);
        Assert.Equal("An error occurred while saving the entity changes. See the inner exception for details.", failure.Title);
        Assert.Contains("UserTeamRoleService.CreateAsync", failure.Detail);
    }

    [Fact]
    public async Task Create_KeepsTheBodysId()
    {
        var msel = await SeedMsel();
        var team = await SeedTeam(msel);
        var member = await Actor().OnTeam(team).SeedAsync();
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();
        var id = Guid.NewGuid();

        var created = await Read<ViewModels.UserTeamRole>(
            await Post(Client(actor), Body(member.Id, team.Id) with { Id = id }));

        Assert.Equal(id, created.Id);
    }

    /// <remarks>
    /// <c>UserTeamRoleEntity</c> <em>is</em> a <c>BaseEntity</c>, unlike <c>TeamUserEntity</c>, so the
    /// audit fields are real here and <c>SaveEntries</c> stamps them on the server - a hostile
    /// <c>dateCreated</c> and <c>createdBy</c> in the body are both discarded.
    /// </remarks>
    [Fact]
    public async Task Create_StampsTheAuditFieldsOnTheServer()
    {
        var msel = await SeedMsel();
        var team = await SeedTeam(msel);
        var member = await Actor().OnTeam(team).SeedAsync();
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();
        var before = DateTime.UtcNow;

        var created = await Read<ViewModels.UserTeamRole>(await Post(
            Client(actor),
            Body(member.Id, team.Id) with
            {
                CreatedBy = Guid.NewGuid(),
                DateCreated = new DateTime(1999, 1, 1, 0, 0, 0, DateTimeKind.Utc)
            }));

        Assert.Equal(actor.Id, created.CreatedBy);
        AssertStampedBetween(created.DateCreated, before, DateTime.UtcNow);
        Assert.Null(created.ModifiedBy);
        Assert.Null(created.DateModified);
    }

    /// <summary>A grant marks the MSEL modified and records the caller.</summary>
    [Fact]
    public async Task Create_MarksTheMselModified()
    {
        var msel = await SeedMsel();
        var team = await SeedTeam(msel);
        var member = await Actor().OnTeam(team).SeedAsync();
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();
        var before = DateTime.UtcNow;

        await Post(Client(actor), Body(member.Id, team.Id));

        var stored = await StoredMsel(msel.Id);

        Assert.Equal(actor.Id, stored.ModifiedBy);
        AssertStampedBetween(stored.DateModified, before, DateTime.UtcNow);
    }

    /// <summary>Create broadcasts only the MSELs own modification.</summary>
    [Fact]
    public async Task Create_BroadcastsOnlyTheMselsOwnModification()
    {
        var msel = await SeedMsel();
        var team = await SeedTeam(msel);
        var member = await Actor().OnTeam(team).SeedAsync();
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        await Post(Client(actor), Body(member.Id, team.Id));

        Assert.All(Hub.Sent(msel.Id, team.Id, member.Id), send => Assert.Equal(MainHubMethods.MselUpdated, send.Method));
        Assert.Equal(
            new[] { MainHub.ADMIN_DATA_GROUP, msel.Id.ToString() }.Order(),
            Hub.Recipients(MainHubMethods.MselUpdated, msel.Id).Order());
    }

    // ---------------------------------------------------------------------------------------------
    // DELETE userteamroles/{id}
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Delete_RemovesTheRoleAndAnswers204()
    {
        var msel = await SeedMsel();
        var team = await SeedTeam(msel);
        var member = await Actor().OnTeam(team).SeedAsync();
        var role = await SeedRole(member.Id, team.Id);
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        var response = await Client(actor).DeleteAsync(RoleRoute(role.Id), Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Empty(await StoredFor(team.Id));
    }

    /// <summary>An unknown role row is a 404 on delete for a caller holding nothing.</summary>
    [Fact]
    public async Task Delete_ForAnIdThatIsNotThere_Is404ForAStrangerToo()
    {
        var stranger = await Actor().SeedAsync();

        var response = await Client(stranger).DeleteAsync(RoleRoute(Guid.NewGuid()), Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Delete_ForAnIdThatIsNotThere_WithEditMsels_Is404()
    {
        var privileged = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();

        var response = await Client(privileged).DeleteAsync(RoleRoute(Guid.NewGuid()), Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Delete_WithEditMselsAndNoRoleOnTheMsel_Is204()
    {
        var team = await SeedTeam(await SeedMsel());
        var member = await Actor().OnTeam(team).SeedAsync();
        var role = await SeedRole(member.Id, team.Id);
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();

        var response = await Client(actor).DeleteAsync(RoleRoute(role.Id), Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    /// <summary>A member of the team without a role on the MSEL is refused the delete.</summary>
    [Fact]
    public async Task Delete_is_forbidden_for_a_member_of_the_team_without_a_role_on_the_msel()
    {
        var msel = await SeedMsel();
        var team = await SeedTeam(msel);
        var member = await Actor().OnTeam(team).SeedAsync();
        var role = await SeedRole(member.Id, team.Id);

        var response = await Client(member).DeleteAsync(RoleRoute(role.Id), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Single(await StoredFor(team.Id));
    }

    /// <summary>An editor of the MSEL is refused the delete: it asks for ownership.</summary>
    [Fact]
    public async Task Delete_is_forbidden_for_an_editor_of_the_msel()
    {
        var msel = await SeedMsel();
        var team = await SeedTeam(msel);
        var member = await Actor().OnTeam(team).SeedAsync();
        var role = await SeedRole(member.Id, team.Id);
        var editor = await Actor().OnMsel(msel, MselRole.Editor).SeedAsync();

        var response = await Client(editor).DeleteAsync(RoleRoute(role.Id), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Single(await StoredFor(team.Id));
    }

    [Fact]
    public async Task Delete_MarksTheMselModified()
    {
        var msel = await SeedMsel();
        var team = await SeedTeam(msel);
        var member = await Actor().OnTeam(team).SeedAsync();
        var role = await SeedRole(member.Id, team.Id);
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();
        var before = DateTime.UtcNow;

        await Client(actor).DeleteAsync(RoleRoute(role.Id), Ct);

        var stored = await StoredMsel(msel.Id);

        Assert.Equal(actor.Id, stored.ModifiedBy);
        AssertStampedBetween(stored.DateModified, before, DateTime.UtcNow);
    }

    /// <remarks>
    /// Revoking a role is the same story as granting one: one <c>MselUpdated</c> per group and nothing
    /// naming what was revoked. A client cannot distinguish this from a MSEL rename.
    /// </remarks>
    [Fact]
    public async Task Delete_BroadcastsOnlyTheMselsOwnModification()
    {
        var msel = await SeedMsel();
        var team = await SeedTeam(msel);
        var member = await Actor().OnTeam(team).SeedAsync();
        var role = await SeedRole(member.Id, team.Id);
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        await Client(actor).DeleteAsync(RoleRoute(role.Id), Ct);

        Assert.All(Hub.Sent(msel.Id, team.Id, member.Id), send => Assert.Equal(MainHubMethods.MselUpdated, send.Method));
        Assert.Equal(
            new[] { MainHub.ADMIN_DATA_GROUP, msel.Id.ToString() }.Order(),
            Hub.Recipients(MainHubMethods.MselUpdated, msel.Id).Order());
    }

    /// <summary>Deleting a team deletes its roles by the cascade.</summary>
    [Fact]
    public async Task ADeletedTeamTakesItsRolesWithIt()
    {
        var msel = await SeedMsel();
        var team = await SeedTeam(msel);
        var member = await Actor().OnTeam(team).SeedAsync();
        await SeedRole(member.Id, team.Id);
        var actor = await Actor().OnMsel(msel, MselRole.Owner).SeedAsync();

        var response = await Client(actor).DeleteAsync($"/api/teams/{team.Id}", Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Empty(await StoredFor(team.Id));
        Assert.Null((await StoredMsel(msel.Id)).DateModified);
    }

    // ---------------------------------------------------------------------------------------------
    // Authentication
    // ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("GET", "msels/00000000-0000-0000-0000-000000000001/userteamroles")]
    [InlineData("GET", "userteamroles/00000000-0000-0000-0000-000000000001")]
    [InlineData("POST", "userteamroles")]
    [InlineData("DELETE", "userteamroles/00000000-0000-0000-0000-000000000001")]
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

    private const string Roles = "/api/userteamroles";

    private static string RoleRoute(Guid id) => $"{Roles}/{id}";

    private static string RolesOf(Guid mselId) => $"/api/msels/{mselId}/userteamroles";

    /// <summary>
    /// The wire shape of a user's role on a team. Every property is writable through
    /// <c>UserTeamRoleProfile</c>'s bare bidirectional map; the audit fields are real (the entity is a
    /// <c>BaseEntity</c>) and are stamped on the server, which
    /// <see cref="Create_StampsTheAuditFieldsOnTheServer"/> pins.
    /// </summary>
    private sealed record UserTeamRoleBody
    {
        public Guid Id { get; init; }
        public Guid UserId { get; init; }
        public Guid TeamId { get; init; }
        public string Role { get; init; }

        public Guid CreatedBy { get; init; }
        public DateTime DateCreated { get; init; }
        public Guid? ModifiedBy { get; init; }
        public DateTime? DateModified { get; init; }
    }

    private static UserTeamRoleBody Body(Guid userId, Guid teamId) => new()
    {
        UserId = userId,
        TeamId = teamId,
        Role = "Submitter"
    };

    private async Task<MselEntity> SeedMsel(bool isTemplate = false)
    {
        var msel = TestData.Msel(isTemplate: isTemplate);
        await Seed(msel);

        return msel;
    }

    private async Task<TeamEntity> SeedTeam(MselEntity msel)
    {
        var team = TestData.Team(msel.Id);
        await Seed(team);

        return team;
    }

    private async Task<UserTeamRoleEntity> SeedRole(
        Guid userId, Guid teamId, string role = "Submitter")
    {
        var entity = TestData.UserTeamRole(userId, teamId, role);
        await Seed(entity);

        return entity;
    }

    private Task<HttpResponseMessage> Post(HttpClient client, UserTeamRoleBody body) =>
        client.PostAsJsonAsync(Roles, body, Ct);

    private async Task<List<ViewModels.UserTeamRole>> GetRoles(HttpClient client, string url)
    {
        var response = await client.GetAsync(url, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await Read<List<ViewModels.UserTeamRole>>(response);
    }

    private async Task<ViewModels.UserTeamRole> GetRole(HttpClient client, Guid id)
    {
        var response = await client.GetAsync(RoleRoute(id), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await Read<ViewModels.UserTeamRole>(response);
    }

    private async Task<List<UserTeamRoleEntity>> StoredFor(Guid teamId)
    {
        await using var context = NewContext();

        return await context.UserTeamRoles
            .AsNoTracking()
            .Where(x => x.TeamId == teamId)
            .ToListAsync(Ct);
    }

    private async Task<MselEntity> StoredMsel(Guid id)
    {
        await using var context = NewContext();

        return await context.Msels.AsNoTracking().SingleAsync(x => x.Id == id, Ct);
    }

    /// <summary>
    /// A server-stamped timestamp lands between the request and the assertion. Postgres stores
    /// microseconds where <c>DateTime</c> holds ticks, so the bounds are widened by a millisecond either
    /// side rather than compared exactly.
    /// </summary>
    private static void AssertStampedBetween(DateTime? actual, DateTime before, DateTime after)
    {
        Assert.NotNull(actual);
        Assert.InRange(actual.Value, before.AddMilliseconds(-1), after.AddMilliseconds(1));
    }

    private async Task<T> Read<T>(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync(Ct);

        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {body}");

        return JsonSerializer.Deserialize<T>(body, JsonOptions);
    }
}
