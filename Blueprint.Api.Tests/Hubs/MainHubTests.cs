// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Blueprint.Api.Data.Enumerations;
using Blueprint.Api.Hubs;
using Blueprint.Api.Infrastructure.Authorization;
using Blueprint.Api.Infrastructure.Identity;
using Blueprint.Api.Services;
using Blueprint.Api.Tests.Support;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Blueprint.Api.Tests.Hubs;

/// <summary><c>MainHub</c> - the one SignalR hub in the API, and the whole of what blueprint.ui subscribes
/// to. The methods that decide which groups a connection belongs to; presence has its own tests,
/// <see cref="MainHubPresenceTests"/>.</summary>
public class MainHubTests(DatabaseFixture fixture) : DatabaseTestBase(fixture)
{
    private readonly PresenceTracker _presence = new();

    // ---------------------------------------------------------------------------------------------
    // Join
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Join adds a group per reachable MSEL per unit and one for the caller themselves. A unit's MSEL is
    /// reachable when the caller holds a role on it: a MSEL's group is sent every event and cell, hidden or
    /// not, and a unit member with no role is at most a participant, shown only the Exercise View.
    /// </summary>
    [Fact]
    public async Task Join_AddsAGroupPerReachableMsel_PerUnit_AndOneForTheCallerThemselves()
    {
        // A real user row, because a unit membership is a foreign key to one.
        var user = TestData.User();
        await Seed(user);
        var actor = user.Id;
        var mine = TestData.Msel(createdBy: actor);
        var template = TestData.Msel(isTemplate: true);
        var strangers = TestData.Msel();
        var unitsMsel = TestData.Msel();
        var unitsMselWithNoRole = TestData.Msel();
        await Seed(mine, template, strangers, unitsMsel, unitsMselWithNoRole);
        var unit = TestData.Unit();
        await Seed(unit);
        await Seed(
            TestData.UnitUser(actor, unit.Id),
            TestData.MselUnit(unit.Id, unitsMsel.Id),
            TestData.MselUnit(unit.Id, unitsMselWithNoRole.Id),
            TestData.UserMselRole(actor, unitsMsel.Id, MselRole.Viewer));
        var harness = Harness(actor);

        await Hub(harness).Join();

        AssertGroups(
            [
                mine.Id.ToString(),
                template.Id.ToString(),
                unitsMsel.Id.ToString(),
                unit.Id.ToString(),
                actor.ToString()
            ],
            Added(harness));
        Assert.DoesNotContain(strangers.Id.ToString(), Added(harness));
        Assert.DoesNotContain(unitsMselWithNoRole.Id.ToString(), Added(harness));
        Assert.Empty(harness.Clients.ReceivedCalls());
        AssertAddressedOnlyOthersInGroup(harness);
    }

    /// <summary>
    /// <see cref="SystemPermission.ViewMsels"/>, the permission every read route asks for, is what joins a
    /// connection to every MSEL in the installation.
    /// </summary>
    [Fact]
    public async Task Join_WithViewMsels_AddsEveryMselInTheInstallation()
    {
        var actor = Guid.NewGuid();
        var strangers = TestData.Msel();
        var archived = TestData.Msel(status: MselItemStatus.Archived);
        await Seed(strangers, archived);
        var harness = Harness(actor);

        await Hub(harness, SystemPermission.ViewMsels).Join();

        AssertGroups([strangers.Id.ToString(), actor.ToString()], Added(harness));
        Assert.DoesNotContain(archived.Id.ToString(), Added(harness));
        AssertAddressedOnlyOthersInGroup(harness);
    }

    /// <summary>
    /// <see cref="SystemPermission.ManageUsers"/> administers accounts and says nothing about MSELs, so
    /// its holder is joined only to the MSELs they belong to and never receives another exercise's edits.
    /// </summary>
    [Fact]
    public async Task Join_WithManageUsers_AddsNoMselTheCallerIsNotOn()
    {
        var actor = Guid.NewGuid();
        var strangers = TestData.Msel();
        await Seed(strangers);
        var harness = Harness(actor);

        await Hub(harness, SystemPermission.ManageUsers).Join();

        AssertGroups([actor.ToString()], Added(harness));
    }

    [Fact]
    public async Task SelectMsel_WithManageUsers_ForAMselTheCallerIsNotOn_JoinsNothing()
    {
        var actor = Guid.NewGuid();
        var strangers = TestData.Msel();
        await Seed(strangers);
        var harness = Harness(actor);

        await Hub(harness, SystemPermission.ManageUsers).SelectMsel([strangers.Id]);

        Assert.DoesNotContain(strangers.Id.ToString(), Added(harness));
    }

    [Fact]
    public async Task Join_SkipsAnArchivedMsel_EvenTheCallersOwn()
    {
        var actor = Guid.NewGuid();
        var live = TestData.Msel(createdBy: actor);
        var archived = TestData.Msel(createdBy: actor, status: MselItemStatus.Archived);
        var archivedTemplate = TestData.Msel(
            isTemplate: true, status: MselItemStatus.Archived);
        await Seed(live, archived, archivedTemplate);
        var harness = Harness(actor);

        await Hub(harness).Join();

        AssertGroups([live.Id.ToString(), actor.ToString()], Added(harness));
        AssertAddressedOnlyOthersInGroup(harness);
    }

    /// <summary>A caller with no <c>sub</c> claim throws from <c>Claims.First</c>.</summary>
    [Fact]
    public async Task Join_ForACallerWithNoSubClaim_Throws()
    {
        var harness = Harness([new Claim("name", "no-sub")]);

        await Assert.ThrowsAsync<InvalidOperationException>(() => Hub(harness).Join());
        AssertAddressedOnlyOthersInGroup(harness);
    }

    // ---------------------------------------------------------------------------------------------
    // Leave
    // ---------------------------------------------------------------------------------------------

    /// <summary>Leave removes the MSEL groups and the callers own but not the unit groups.</summary>
    [Fact]
    public async Task Leave_RemovesTheMselGroupsAndTheCallersOwn_ButNotTheUnitGroups()
    {
        var user = TestData.User();
        await Seed(user);
        var actor = user.Id;
        var msel = TestData.Msel(createdBy: actor);
        await Seed(msel);
        var unit = TestData.Unit();
        await Seed(unit);
        await Seed(TestData.UnitUser(actor, unit.Id));
        var harness = Harness(actor);
        var hub = Hub(harness);

        await hub.Join();
        await hub.Leave();

        Assert.Contains(unit.Id.ToString(), Added(harness));
        AssertGroups([msel.Id.ToString(), actor.ToString()], Removed(harness));
        AssertAddressedOnlyOthersInGroup(harness);
    }

    // ---------------------------------------------------------------------------------------------
    // JoinAdmin / LeaveAdmin
    // ---------------------------------------------------------------------------------------------

    /// <summary>Each admin group is joined by the permission that opens it.</summary>
    [Theory]
    [InlineData(SystemPermission.EditMsels, MainHub.ADMIN_DATA_GROUP)]
    [InlineData(SystemPermission.ViewGroups, MainHub.GROUP_GROUP)]
    [InlineData(SystemPermission.ViewRoles, MainHub.ROLE_GROUP)]
    [InlineData(SystemPermission.ViewUsers, MainHub.USER_GROUP)]
    public async Task JoinAdmin_AddsTheGroupForThePermissionTheCallerHolds(
        SystemPermission permission, string group)
    {
        var actor = Guid.NewGuid();
        var harness = Harness(actor);

        await Hub(harness, permission).JoinAdmin();

        AssertGroups([actor.ToString(), group], Added(harness));
        AssertAddressedOnlyOthersInGroup(harness);
    }

    [Fact]
    public async Task JoinAdmin_WithNoPermissions_AddsOnlyTheCallersOwnGroup()
    {
        var actor = Guid.NewGuid();
        var harness = Harness(actor);

        await Hub(harness).JoinAdmin();

        AssertGroups([actor.ToString()], Added(harness));
        AssertAddressedOnlyOthersInGroup(harness);
    }

    [Fact]
    public async Task LeaveAdmin_RemovesExactlyWhatJoinAdminAdded()
    {
        var actor = Guid.NewGuid();
        var harness = Harness(actor);
        var hub = Hub(
            harness,
            SystemPermission.EditMsels,
            SystemPermission.ViewGroups,
            SystemPermission.ViewRoles,
            SystemPermission.ViewUsers);

        await hub.JoinAdmin();
        await hub.LeaveAdmin();

        AssertGroups(
            [
                actor.ToString(),
                MainHub.ADMIN_DATA_GROUP,
                MainHub.GROUP_GROUP,
                MainHub.ROLE_GROUP,
                MainHub.USER_GROUP
            ],
            Added(harness));
        Assert.Equal(Added(harness), Removed(harness));
        AssertAddressedOnlyOthersInGroup(harness);
    }

    /// <summary>
    /// A hub attached to <paramref name="harness"/>, reading this test's own database, whose caller holds
    /// <paramref name="permissions"/> and nothing else.
    /// </summary>
    /// <remarks>
    /// <c>ITeamService</c>, <c>IMselService</c> and <c>DatabaseOptions</c> are deliberately null: the hub
    /// assigns all three and reads none of them, so passing null is the assertion. The authorization
    /// service is the real <see cref="BlueprintAuthorizationService"/> over the real
    /// <see cref="SystemPermissionHandler"/>; with no current principal it falls back to the identity
    /// resolver, as on a real hub connect, and the resolver answers a principal holding exactly
    /// <paramref name="permissions"/>.
    /// </remarks>
    private MainHub Hub(HubHarness harness, params SystemPermission[] permissions)
    {
        var claims = Substitute.For<IUserClaimsService>();
        claims.GetCurrentClaimsPrincipal().Returns((ClaimsPrincipal)null);
        var identity = Substitute.For<IIdentityResolver>();
        identity.GetClaimsPrincipal().Returns(new ClaimsPrincipalBuilder().WithSystemPermissions(permissions).Build());
        var authorization = AuthorizationHarness.CreateBlueprintAuthorizationService(claims, identity);

        return harness.Attach(new MainHub(null, null, Db, null, authorization, _presence, NullLogger<MainHub>.Instance));
    }

    /// <summary>
    /// The shared harness for a caller with a <c>sub</c> and, unless <paramref name="userName"/> is null, a
    /// <c>name</c>: the test identity mints <c>name</c> only when the request carries one, so a nameless
    /// caller is a real state over HTTP too, and it is what reaches MainHub's "Unknown" fallback.
    /// </summary>
    private static HubHarness Harness(Guid userId, string userName = "test-user") =>
        Harness(userName is null
            ? [new Claim("sub", userId.ToString())]
            : [new Claim("sub", userId.ToString()), new Claim("name", userName)]);

    private static HubHarness Harness(IEnumerable<Claim> claims) =>
        new(user: new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")));

    /// <summary>The groups the hub added this connection to, in order.</summary>
    private static List<string> Added(HubHarness harness) => GroupCalls(harness, nameof(IGroupManager.AddToGroupAsync));

    /// <summary>The groups the hub removed this connection from, in order.</summary>
    private static List<string> Removed(HubHarness harness) => GroupCalls(harness, nameof(IGroupManager.RemoveFromGroupAsync));

    private static List<string> GroupCalls(HubHarness harness, string method) =>
        [.. harness.Groups.ReceivedCalls()
            .Where(x => x.GetMethodInfo().Name == method)
            .Select(x => (string)x.GetArguments()[1])];

    /// <summary>
    /// Every client the hub addressed was <c>Clients.Group</c>, and the hub neither read the connection's
    /// features nor aborted it, which a harness-driven call cannot stand for.
    /// </summary>
    private static void AssertAddressedOnlyOthersInGroup(HubHarness harness)
    {
        Assert.All(
            harness.Clients.ReceivedCalls(),
            x => Assert.Equal(nameof(IHubCallerClients.Group), x.GetMethodInfo().Name));
        Assert.DoesNotContain(
            harness.Context.ReceivedCalls(),
            x => x.GetMethodInfo().Name is "get_" + nameof(HubCallerContext.Features) or nameof(HubCallerContext.Abort));
    }

    /// <remarks>
    /// Group membership is a set - the hub joins in whatever order its queries answered, and a client
    /// cannot observe the order - so both sides are sorted. Asserting the sequence would be a flake.
    /// </remarks>
    private static void AssertGroups(IEnumerable<string> expected, IEnumerable<string> actual) =>
        Assert.Equal(expected.Order().ToList(), actual.Order().ToList());
}
