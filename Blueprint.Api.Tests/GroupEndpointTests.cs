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
using Blueprint.Api.Hubs;
using Blueprint.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Blueprint.Api.Tests;

/// <summary>
/// <c>GroupService</c> / <c>GroupController</c> - the nine routes over groups and their memberships.
/// A group is a named set of users, global rather than MSEL-scoped, gated by <c>ViewGroups</c> on the four
/// reads and <c>ManageGroups</c> on the five writes.
/// </summary>
/// <remarks>
/// The 401 and 403 cases for these routes live in <see cref="RouteAuthorizationTests"/>; this file covers
/// the happy path and the not-found path per route, per the thin protocol adopted for the remaining
/// services.
/// <para />
/// <c>GroupService</c> is the best-behaved service in the tier: every single-row read is
/// <c>SingleOrDefaultAsync</c>, every write null-checks before touching anything, and the controller
/// overwrites the body's <c>GroupId</c> with the route's - so none of the four defect shapes this branch
/// has recorded a dozen times is present. What it cannot do is matter: group membership grants nothing
/// anywhere in the API.
/// </remarks>
public class GroupEndpointTests(DatabaseFixture fixture, BlueprintAppFactory factory)
    : ApiTestBase(fixture, factory), IClassFixture<BlueprintAppFactory>
{
    // ---------------------------------------------------------------------------------------------
    // GET groups, GET groups/{id}
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task GetAll_ReturnsEveryGroup()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewGroups).SeedAsync();
        var one = BlueprintAppFactory.Group("group-one");
        var two = BlueprintAppFactory.Group("group-two");
        await Seed(one, two);

        var groups = await Get<List<ViewModels.Group>>(Client(actor), "api/groups");

        Assert.Equal(2, groups.Count);
        Assert.Contains("group-one", groups.Select(x => x.Name));
        Assert.Contains("group-two", groups.Select(x => x.Name));
    }

    [Fact]
    public async Task Get_ForAGroup_Is200()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewGroups).SeedAsync();
        var group = BlueprintAppFactory.Group("the-group");
        await Seed(group);

        var answered = await Get<ViewModels.Group>(Client(actor), $"api/groups/{group.Id}");

        Assert.Equal(group.Id, answered.Id);
        Assert.Equal("the-group", answered.Name);
    }

    [Fact]
    public async Task Get_ForAnIdThatIsNotThere_Is404()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewGroups).SeedAsync();

        var response = await Client(actor).GetAsync($"api/groups/{Guid.NewGuid()}", Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ---------------------------------------------------------------------------------------------
    // POST groups
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Create_WithManageGroups_Is201()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageGroups).SeedAsync();

        var response = await Client(actor).PostAsJsonAsync(
            "api/groups", new { name = "new-group", description = "made by a test" }, Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<ViewModels.Group>(JsonOptions, Ct);
        Assert.Equal("new-group", created.Name);
        Assert.EndsWith($"/api/groups/{created.Id}", response.Headers.Location.ToString());

        var stored = await NewContext().Groups.SingleOrDefaultAsync(x => x.Id == created.Id, Ct);
        Assert.Equal("new-group", stored.Name);
    }

    /// <remarks>
    /// BUG: <c>GroupEntity.Name</c> is uniquely indexed and nothing checks it first, so a duplicate is a
    /// <c>DbUpdateException</c> - not an <c>IApiException</c> - and the caller is told 500 rather than 409.
    /// </remarks>
    [Fact]
    public async Task Create_WithADuplicateName_Is500()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageGroups).SeedAsync();
        await Seed(BlueprintAppFactory.Group("taken"));

        var response = await Client(actor).PostAsJsonAsync("api/groups", new { name = "taken" }, Ct);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    // ---------------------------------------------------------------------------------------------
    // PUT groups/{id}
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Update_WithManageGroups_Is200()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageGroups).SeedAsync();
        var group = BlueprintAppFactory.Group("before");
        await Seed(group);

        var response = await Client(actor).PutAsJsonAsync(
            $"api/groups/{group.Id}",
            new { id = group.Id, name = "after", description = "edited" },
            Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var stored = await NewContext().Groups.SingleAsync(x => x.Id == group.Id, Ct);
        Assert.Equal("after", stored.Name);
        Assert.Equal("edited", stored.Description);
    }

    [Fact]
    public async Task Update_ForAnIdThatIsNotThere_Is404()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageGroups).SeedAsync();
        var id = Guid.NewGuid();

        var response = await Client(actor).PutAsJsonAsync(
            $"api/groups/{id}", new { id, name = "nothing here" }, Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ---------------------------------------------------------------------------------------------
    // DELETE groups/{id}
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Delete_WithManageGroups_Is204()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageGroups).SeedAsync();
        var group = BlueprintAppFactory.Group();
        await Seed(group);

        var response = await Client(actor).DeleteAsync($"api/groups/{group.Id}", Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Empty(await NewContext().Groups.Where(x => x.Id == group.Id).ToListAsync(Ct));
    }

    [Fact]
    public async Task Delete_ForAnIdThatIsNotThere_Is404()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageGroups).SeedAsync();

        var response = await Client(actor).DeleteAsync($"api/groups/{Guid.NewGuid()}", Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <remarks>
    /// The cascade is the migrations', declared on <c>GroupMembershipConfiguration</c>'s FK. Deleting a
    /// group is therefore silent about how many memberships it took with it.
    /// </remarks>
    [Fact]
    public async Task Delete_TakesItsMembershipsWithIt()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageGroups).SeedAsync();
        var group = BlueprintAppFactory.Group();
        var member = BlueprintAppFactory.User();
        await Seed(group, member);
        await Seed(BlueprintAppFactory.GroupMembership(group.Id, member.Id));

        var response = await Client(actor).DeleteAsync($"api/groups/{group.Id}", Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Empty(await NewContext().GroupMemberships.Where(x => x.GroupId == group.Id).ToListAsync(Ct));
        Assert.NotNull(await NewContext().Users.SingleOrDefaultAsync(x => x.Id == member.Id, Ct));
    }

    // ---------------------------------------------------------------------------------------------
    // GET groups/{groupId}/memberships, GET groups/memberships/{id}
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task GetMemberships_ForAGroup_Is200()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewGroups).SeedAsync();
        var group = BlueprintAppFactory.Group();
        var other = BlueprintAppFactory.Group();
        var member = BlueprintAppFactory.User();
        await Seed(group, other, member);
        await Seed(
            BlueprintAppFactory.GroupMembership(group.Id, member.Id),
            BlueprintAppFactory.GroupMembership(other.Id, member.Id));

        var memberships = await Get<List<ViewModels.GroupMembership>>(
            Client(actor), $"api/groups/{group.Id}/memberships");

        Assert.Single(memberships);
        Assert.Equal(group.Id, memberships[0].GroupId);
        Assert.Equal(member.Id, memberships[0].UserId);
    }

    /// <remarks>
    /// Seeds a membership of another group, so the empty answer is the filter working rather than an empty
    /// database - the weakness <c>4dd5201</c> found in two earlier files.
    /// </remarks>
    [Fact]
    public async Task GetMemberships_ForAGroupThatIsNotThere_IsAnEmptyList()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewGroups).SeedAsync();
        var group = BlueprintAppFactory.Group();
        var member = BlueprintAppFactory.User();
        await Seed(group, member);
        await Seed(BlueprintAppFactory.GroupMembership(group.Id, member.Id));

        var memberships = await Get<List<ViewModels.GroupMembership>>(
            Client(actor), $"api/groups/{Guid.NewGuid()}/memberships");

        Assert.Empty(memberships);
    }

    [Fact]
    public async Task GetMembership_ForAMembership_Is200()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewGroups).SeedAsync();
        var group = BlueprintAppFactory.Group();
        var member = BlueprintAppFactory.User();
        await Seed(group, member);
        var membership = BlueprintAppFactory.GroupMembership(group.Id, member.Id);
        await Seed(membership);

        var answered = await Get<ViewModels.GroupMembership>(
            Client(actor), $"api/groups/memberships/{membership.Id}");

        Assert.Equal(membership.Id, answered.Id);
        Assert.Equal(group.Id, answered.GroupId);
        Assert.Equal(member.Id, answered.UserId);
    }

    [Fact]
    public async Task GetMembership_ForAnIdThatIsNotThere_Is404()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewGroups).SeedAsync();

        var response = await Client(actor).GetAsync($"api/groups/memberships/{Guid.NewGuid()}", Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ---------------------------------------------------------------------------------------------
    // POST groups/{groupId}/memberships, DELETE groups/memberships/{id}
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task CreateMembership_WithManageGroups_Is201()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageGroups).SeedAsync();
        var group = BlueprintAppFactory.Group();
        var member = BlueprintAppFactory.User();
        await Seed(group, member);

        var response = await Client(actor).PostAsJsonAsync(
            $"api/groups/{group.Id}/memberships", new { userId = member.Id }, Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<ViewModels.GroupMembership>(JsonOptions, Ct);
        Assert.Equal(group.Id, created.GroupId);
        Assert.Equal(member.Id, created.UserId);
        Assert.EndsWith($"/api/groups/memberships/{created.Id}", response.Headers.Location.ToString());
    }

    /// <remarks>
    /// The one thing this controller does that five others in the branch do not: it overwrites the body's
    /// <c>GroupId</c> with the route's (<c>GroupController.cs:198</c>), so a body naming another group
    /// cannot repoint the row. A positive control for the request-body defect shape.
    /// </remarks>
    [Fact]
    public async Task CreateMembership_IgnoresTheGroupIdInTheBody()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageGroups).SeedAsync();
        var routeGroup = BlueprintAppFactory.Group();
        var bodyGroup = BlueprintAppFactory.Group();
        var member = BlueprintAppFactory.User();
        await Seed(routeGroup, bodyGroup, member);

        var response = await Client(actor).PostAsJsonAsync(
            $"api/groups/{routeGroup.Id}/memberships",
            new { groupId = bodyGroup.Id, userId = member.Id },
            Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<ViewModels.GroupMembership>(JsonOptions, Ct);
        Assert.Equal(routeGroup.Id, created.GroupId);
    }

    /// <remarks>
    /// BUG: nothing checks that the user exists, so the foreign key answers for it and the caller is told
    /// 500 rather than 404. Same shape as <c>UnitUserService.CreateAsync</c> (<c>977f578</c>), which at
    /// least has a comment claiming to validate.
    /// </remarks>
    [Fact]
    public async Task CreateMembership_ForAUserThatIsNotThere_Is500()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageGroups).SeedAsync();
        var group = BlueprintAppFactory.Group();
        await Seed(group);

        var response = await Client(actor).PostAsJsonAsync(
            $"api/groups/{group.Id}/memberships", new { userId = Guid.NewGuid() }, Ct);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    [Fact]
    public async Task DeleteMembership_WithManageGroups_Is204()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageGroups).SeedAsync();
        var group = BlueprintAppFactory.Group();
        var member = BlueprintAppFactory.User();
        await Seed(group, member);
        var membership = BlueprintAppFactory.GroupMembership(group.Id, member.Id);
        await Seed(membership);

        var response = await Client(actor).DeleteAsync($"api/groups/memberships/{membership.Id}", Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Empty(await NewContext().GroupMemberships.Where(x => x.Id == membership.Id).ToListAsync(Ct));
    }

    [Fact]
    public async Task DeleteMembership_ForAnIdThatIsNotThere_Is404()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageGroups).SeedAsync();

        var response = await Client(actor).DeleteAsync($"api/groups/memberships/{Guid.NewGuid()}", Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ---------------------------------------------------------------------------------------------
    // Broadcasts
    // ---------------------------------------------------------------------------------------------

    /// <remarks>
    /// The first assertions in the suite against <c>GroupHandler</c> and <c>GroupMembershipHandler</c>:
    /// both address <c>MainHub.GROUP_GROUP</c> through the params-<c>string[]</c> overload of
    /// <c>IHubClients.Groups</c>, which <c>HubRecorder</c> only learned to record in <c>4f4cffe</c>.
    /// </remarks>
    [Fact]
    public async Task Create_TellsTheGroupGroup()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageGroups).SeedAsync();

        var response = await Client(actor).PostAsJsonAsync("api/groups", new { name = "announced" }, Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var sent = Assert.Single(Hub.Of(MainHubMethods.GroupCreated));
        Assert.Equal(MainHub.GROUP_GROUP, sent.Group);
        Assert.Equal("announced", Assert.IsType<ViewModels.Group>(sent.Payload).Name);
    }

    [Fact]
    public async Task CreateMembership_TellsTheGroupGroup()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ManageGroups).SeedAsync();
        var group = BlueprintAppFactory.Group();
        var member = BlueprintAppFactory.User();
        await Seed(group, member);

        var response = await Client(actor).PostAsJsonAsync(
            $"api/groups/{group.Id}/memberships", new { userId = member.Id }, Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var sent = Assert.Single(Hub.Of(MainHubMethods.GroupMembershipCreated));
        Assert.Equal(MainHub.GROUP_GROUP, sent.Group);
        Assert.Equal(member.Id, Assert.IsType<ViewModels.GroupMembership>(sent.Payload).UserId);
    }

    // ---------------------------------------------------------------------------------------------
    // What a group is for
    // ---------------------------------------------------------------------------------------------

    /// <remarks>
    /// BUG: group membership grants nothing. <c>UserClaimsService</c>'s <c>groupIds</c> is dead code
    /// (Phase 2), no requirement helper reads <c>GroupMemberships</c>, and no route is scoped by a group -
    /// so the nine routes in this file maintain a table with no consumer. This test is the evidence: a
    /// member of a group is refused the same MSEL a non-member is.
    /// </remarks>
    [Fact]
    public async Task AGroupMembership_GrantsItsMemberNothing()
    {
        var group = BlueprintAppFactory.Group();
        await Seed(group);
        var member = await Actor().SeedAsync();
        await Seed(BlueprintAppFactory.GroupMembership(group.Id, member.Id));
        var msel = BlueprintAppFactory.Msel();
        await Seed(msel);

        var response = await Client(member).GetAsync($"api/msels/{msel.Id}", Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    private async Task<T> Get<T>(HttpClient client, string url)
    {
        var response = await client.GetAsync(url, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await response.Content.ReadFromJsonAsync<T>(JsonOptions, Ct);
    }
}
