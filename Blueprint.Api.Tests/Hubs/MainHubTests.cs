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
using NSubstitute;
using Xunit;

namespace Blueprint.Api.Tests.Hubs;

/// <summary><c>MainHub</c> - the one SignalR hub in the API, and the whole of what blueprint.ui subscribes
/// to. Eight methods: four that decide which groups a connection belongs to, and four that maintain the
/// per-MSEL presence list.</summary>
public class MainHubTests(DatabaseFixture fixture) : DatabaseTestBase(fixture)
{
    /// <remarks>
    /// One cache per test, standing in for the host-wide singleton
    /// (<c>Startup.cs:254 services.AddSingleton&lt;Hubs.HubCache&gt;()</c>). Shared by every hub this
    /// test builds, which is what lets one test play two connections against each other.
    /// </remarks>
    private readonly HubCache _cache = new();

    // ---------------------------------------------------------------------------------------------
    // Join
    // ---------------------------------------------------------------------------------------------

    /// <summary>Join adds a group per reachable MSEL per unit and one for the caller themselves.</summary>
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
        await Seed(mine, template, strangers, unitsMsel);
        var unit = TestData.Unit();
        await Seed(unit);
        await Seed(
            TestData.UnitUser(actor, unit.Id),
            TestData.MselUnit(unit.Id, unitsMsel.Id));
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
        Assert.Empty(harness.Clients.ReceivedCalls());
        AssertAddressedOnlyOthersInGroup(harness);
    }

    /// <summary>Join with manage users adds every MSEL in the installation.</summary>
    [Fact]
    public async Task Join_WithManageUsers_AddsEveryMselInTheInstallation()
    {
        var actor = Guid.NewGuid();
        var strangers = TestData.Msel();
        var archived = TestData.Msel(status: MselItemStatus.Archived);
        await Seed(strangers, archived);
        var harness = Harness(actor);

        await Hub(harness, SystemPermission.ManageUsers).Join();

        AssertGroups([strangers.Id.ToString(), actor.ToString()], Added(harness));
        Assert.DoesNotContain(archived.Id.ToString(), Added(harness));
        AssertAddressedOnlyOthersInGroup(harness);
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

    /// <summary>Leaving broadcasts the departure under the caller's current claims and forgets the
    /// connection.</summary>
    [Fact]
    public async Task Leave_ForATrackedConnection_BroadcastsTheClaimsIdentity_AndForgetsTheConnection()
    {
        var actor = Guid.NewGuid();
        var msel = TestData.Msel(createdBy: actor);
        await Seed(msel);
        var harness = Harness(actor, "current-name");
        Track(msel.Id, actor, "stale-name");

        await Hub(harness).Leave();

        var send = Assert.Single(Presence(harness, MainHubMethods.PresenceDeparted));
        Assert.Equal(msel.Id.ToString(), send.Group);
        Assert.Equal((actor.ToString(), "current-name"), Identity(send.Payload));
        Assert.Empty(_cache.Connections);
        AssertAddressedOnlyOthersInGroup(harness);
    }

    [Fact]
    public async Task Leave_ForAConnectionNobodyIsTracking_BroadcastsNothing()
    {
        var actor = Guid.NewGuid();
        var msel = TestData.Msel(createdBy: actor);
        await Seed(msel);
        Track(msel.Id, Guid.NewGuid(), "somebody-else", "another-connection");
        var harness = Harness(actor);

        await Hub(harness).Leave();

        Assert.Empty(harness.Clients.ReceivedCalls());
        Assert.Single(_cache.Connections);
        AssertAddressedOnlyOthersInGroup(harness);
    }

    // ---------------------------------------------------------------------------------------------
    // OnDisconnectedAsync
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task OnDisconnected_ForATrackedConnection_BroadcastsTheCachedIdentity()
    {
        var mselId = Guid.NewGuid();
        var whoTheyWere = Guid.NewGuid();
        Track(mselId, whoTheyWere, "cached-name");
        var harness = Harness(Guid.NewGuid(), "whoever-is-connected-now");

        await Hub(harness).OnDisconnectedAsync(null);

        var send = Assert.Single(Presence(harness, MainHubMethods.PresenceDeparted));
        Assert.Equal(mselId.ToString(), send.Group);
        Assert.Equal((whoTheyWere.ToString(), "cached-name"), Identity(send.Payload));
        Assert.Empty(_cache.Connections);
        Assert.Empty(Removed(harness));
        AssertAddressedOnlyOthersInGroup(harness);
    }

    [Fact]
    public async Task OnDisconnected_ForAConnectionNobodyIsTracking_BroadcastsNothing()
    {
        var harness = Harness(Guid.NewGuid());

        await Hub(harness).OnDisconnectedAsync(null);

        Assert.Empty(harness.Clients.ReceivedCalls());
        AssertAddressedOnlyOthersInGroup(harness);
    }

    /// <summary>A tracked connection with no MSEL is forgotten without a broadcast.</summary>
    [Fact]
    public async Task OnDisconnected_ForATrackedConnectionOnNoMsel_ForgetsItSilently()
    {
        Track(mselId: null, Guid.NewGuid(), "on-no-msel");
        var harness = Harness(Guid.NewGuid());

        await Hub(harness).OnDisconnectedAsync(null);

        Assert.Empty(harness.Clients.ReceivedCalls());
        Assert.Empty(_cache.Connections);
        AssertAddressedOnlyOthersInGroup(harness);
    }

    // ---------------------------------------------------------------------------------------------
    // SelectMsel
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task SelectMsel_ForAReachableMsel_JoinsIt_TracksTheConnection_AndTellsTheOthers()
    {
        var actor = Guid.NewGuid();
        var msel = TestData.Msel(createdBy: actor);
        await Seed(msel);
        var harness = Harness(actor, "arriving");

        await Hub(harness).SelectMsel([msel.Id]);

        AssertGroups([msel.Id.ToString()], Added(harness));
        AssertGroups([msel.Id.ToString()], Removed(harness));
        var send = Assert.Single(Presence(harness, MainHubMethods.PresenceArrived));
        Assert.Equal(msel.Id.ToString(), send.Group);
        Assert.Equal((actor.ToString(), "arriving"), Identity(send.Payload));
        var tracked = Assert.Single(_cache.Connections).Value;
        Assert.Equal(HubHarness.ConnectionId, tracked.ConnectionId);
        Assert.Equal(msel.Id.ToString(), tracked.MselId);
        Assert.Equal(actor.ToString(), tracked.UserId);
        Assert.Equal("arriving", tracked.UserName);
        AssertAddressedOnlyOthersInGroup(harness);
    }

    /// <summary>Select MSEL for a MSEL the caller cannot reach joins nothing and says nothing.</summary>
    [Fact]
    public async Task SelectMsel_ForAMselTheCallerCannotReach_JoinsNothing_AndSaysNothing()
    {
        var actor = await new TestActorBuilder(Db, Ct).OnNewMsel(MselRole.Owner).SeedAsync();
        var strangers = TestData.Msel();
        await Seed(strangers);
        var harness = Harness(actor.Id);

        await Hub(harness).SelectMsel([strangers.Id]);

        Assert.Empty(Added(harness));
        Assert.Empty(harness.Clients.ReceivedCalls());
        Assert.Empty(_cache.Connections);
        AssertAddressedOnlyOthersInGroup(harness);
    }

    [Fact]
    public async Task SelectMsel_WithNoIds_LeavesEveryMselGroup_AndForgetsTheConnection()
    {
        var actor = Guid.NewGuid();
        var msel = TestData.Msel(createdBy: actor);
        var other = TestData.Msel(createdBy: actor);
        await Seed(msel, other);
        var harness = Harness(actor, "departing");
        Track(msel.Id, actor, "departing");

        await Hub(harness).SelectMsel([]);

        Assert.Empty(Added(harness));
        AssertGroups([msel.Id.ToString(), other.Id.ToString()], Removed(harness));
        var send = Assert.Single(Presence(harness, MainHubMethods.PresenceDeparted));
        Assert.Equal(msel.Id.ToString(), send.Group);
        Assert.Empty(_cache.Connections);
        AssertAddressedOnlyOthersInGroup(harness);
    }

    /// <summary>Selecting two MSELs joins neither.</summary>
    [Fact]
    public async Task SelectMsel_WithTwoIds_JoinsNeither()
    {
        var actor = Guid.NewGuid();
        var first = TestData.Msel(createdBy: actor);
        var second = TestData.Msel(createdBy: actor);
        await Seed(first, second);
        var harness = Harness(actor);

        await Hub(harness).SelectMsel([first.Id, second.Id]);

        Assert.Empty(Added(harness));
        Assert.Empty(harness.Clients.ReceivedCalls());
        Assert.Empty(_cache.Connections);
        AssertAddressedOnlyOthersInGroup(harness);
    }

    [Fact]
    public async Task SelectMsel_SwitchingMsels_TellsTheOldOneAndTheNewOne()
    {
        var actor = Guid.NewGuid();
        var was = TestData.Msel(createdBy: actor);
        var now = TestData.Msel(createdBy: actor);
        await Seed(was, now);
        var harness = Harness(actor, "switching");
        Track(was.Id, actor, "switching");

        await Hub(harness).SelectMsel([now.Id]);

        Assert.Equal(was.Id.ToString(), Assert.Single(Presence(harness, MainHubMethods.PresenceDeparted)).Group);
        Assert.Equal(now.Id.ToString(), Assert.Single(Presence(harness, MainHubMethods.PresenceArrived)).Group);
        Assert.Equal(now.Id.ToString(), Assert.Single(_cache.Connections).Value.MselId);
        AssertAddressedOnlyOthersInGroup(harness);
    }

    // ---------------------------------------------------------------------------------------------
    // Greet
    // ---------------------------------------------------------------------------------------------

    /// <summary>Greet for a MSEL the caller cannot reach broadcasts anyway.</summary>
    [Fact]
    public async Task Greet_ForAMselTheCallerCannotReach_BroadcastsAnyway()
    {
        var actor = Guid.NewGuid();
        var strangers = TestData.Msel();
        await Seed(strangers);
        var harness = Harness(actor, "gatecrasher");

        await Hub(harness).Greet(strangers.Id.ToString());

        var send = Assert.Single(Presence(harness, MainHubMethods.PresenceGreeted));
        Assert.Equal(strangers.Id.ToString(), send.Group);
        Assert.Equal((actor.ToString(), "gatecrasher"), Identity(send.Payload));
        AssertAddressedOnlyOthersInGroup(harness);
    }

    /// <summary>A caller with no <c>name</c> claim is announced as <c>Unknown</c>, to whatever group name was
    /// given.</summary>
    [Fact]
    public async Task Greet_ForACallerWithNoNameClaim_AnnouncesThemAsUnknown()
    {
        var actor = Guid.NewGuid();
        var harness = Harness(actor, userName: null);

        await Hub(harness).Greet("not-a-msel");

        var send = Assert.Single(Presence(harness, MainHubMethods.PresenceGreeted));
        Assert.Equal("not-a-msel", send.Group);
        Assert.Equal((actor.ToString(), "Unknown"), Identity(send.Payload));
        AssertAddressedOnlyOthersInGroup(harness);
    }

    // ---------------------------------------------------------------------------------------------
    // GetPresence
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task GetPresence_ListsEverybodyElseOnThatMsel_AndNotTheCaller()
    {
        var actor = Guid.NewGuid();
        var msel = TestData.Msel(createdBy: actor);
        await Seed(msel);
        var colleague = Guid.NewGuid();
        Track(msel.Id, actor, "me");
        Track(msel.Id, colleague, "them", "their-connection");
        Track(Guid.NewGuid(), Guid.NewGuid(), "elsewhere", "a-third-connection");
        var harness = Harness(actor, "me");

        var presence = await Hub(harness).GetPresence(msel.Id.ToString());

        Assert.Equal(
            (colleague.ToString(), "them"), Identity(Assert.Single(presence)));
        AssertAddressedOnlyOthersInGroup(harness);
    }

    /// <summary>Presence for a MSEL the caller cannot reach lists its occupants.</summary>
    [Fact]
    public async Task GetPresence_ForAMselTheCallerCannotReach_ListsItsOccupantsAnyway()
    {
        var actor = Guid.NewGuid();
        var strangers = TestData.Msel();
        await Seed(strangers);
        var occupant = Guid.NewGuid();
        Track(strangers.Id, occupant, "somebody-working", "their-connection");
        var harness = Harness(actor);

        var presence = await Hub(harness).GetPresence(strangers.Id.ToString());

        Assert.Equal(
            (occupant.ToString(), "somebody-working"), Identity(Assert.Single(presence)));
        AssertAddressedOnlyOthersInGroup(harness);
    }

    [Fact]
    public async Task GetPresence_ForAMselNobodyIsOn_IsEmpty()
    {
        var actor = Guid.NewGuid();
        var msel = TestData.Msel(createdBy: actor);
        await Seed(msel);
        Track(Guid.NewGuid(), Guid.NewGuid(), "on-another-msel", "their-connection");
        var harness = Harness(actor);

        Assert.Empty(await Hub(harness).GetPresence(msel.Id.ToString()));
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

        // Presence messages go to the others in a MSEL's group; each lands on a proxy of the harness's own.
        harness.Clients.OthersInGroup(Arg.Any<string>()).Returns(call => harness.Group(OthersIn(call.Arg<string>())));

        return harness.Attach(new MainHub(null, null, Db, null, authorization, _cache));
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

    private static string OthersIn(string group) => $"others-in:{group}";

    /// <summary>The groups the hub added this connection to, in order.</summary>
    private static List<string> Added(HubHarness harness) => GroupCalls(harness, nameof(IGroupManager.AddToGroupAsync));

    /// <summary>The groups the hub removed this connection from, in order.</summary>
    private static List<string> Removed(HubHarness harness) => GroupCalls(harness, nameof(IGroupManager.RemoveFromGroupAsync));

    private static List<string> GroupCalls(HubHarness harness, string method) =>
        [.. harness.Groups.ReceivedCalls()
            .Where(x => x.GetMethodInfo().Name == method)
            .Select(x => (string)x.GetArguments()[1])];

    /// <summary>Every presence message the hub sent to the others in a group, with that group.</summary>
    private static List<PresenceSend> Presence(HubHarness harness, string method = null) =>
        [.. harness.Clients.ReceivedCalls()
            .Where(x => x.GetMethodInfo().Name == nameof(IHubCallerClients.OthersInGroup))
            .Select(x => (string)x.GetArguments()[0])
            .Distinct()
            .ToList()
            .SelectMany(group => harness.Group(OthersIn(group)).ReceivedCalls()
                .Where(x => x.GetMethodInfo().Name == nameof(IClientProxy.SendCoreAsync))
                .Select(x => new PresenceSend(group, (string)x.GetArguments()[0], ((object[])x.GetArguments()[1])[0])))
            .Where(x => method is null || x.Method == method)];

    /// <summary>
    /// Every client the hub addressed was <c>Clients.OthersInGroup</c>, the one form <see cref="Presence"/>
    /// reads, and the hub neither read the connection's features nor aborted it, which a harness-driven
    /// call cannot stand for.
    /// </summary>
    private static void AssertAddressedOnlyOthersInGroup(HubHarness harness)
    {
        Assert.All(
            harness.Clients.ReceivedCalls(),
            x => Assert.Equal(nameof(IHubCallerClients.OthersInGroup), x.GetMethodInfo().Name));
        Assert.DoesNotContain(
            harness.Context.ReceivedCalls(),
            x => x.GetMethodInfo().Name is "get_" + nameof(HubCallerContext.Features) or nameof(HubCallerContext.Abort));
    }

    /// <summary>The <c>id</c> and <c>name</c> of a presence payload, which is an anonymous object.</summary>
    private static (string Id, string Name) Identity(object payload) =>
        (Property(payload, "id"), Property(payload, "name"));

    private static string Property(object payload, string name) =>
        payload?.GetType().GetProperty(name)?.GetValue(payload)?.ToString();

    private sealed record PresenceSend(string Group, string Method, object Payload);

    /// <summary>Puts a connection in the cache, as <c>SelectMsel</c> would have.</summary>
    private void Track(
        Guid? mselId, Guid userId, string userName, string connectionId = HubHarness.ConnectionId)
    {
        _cache.Connections[connectionId] = new CachedConnection
        {
            ConnectionId = connectionId,
            MselId = mselId?.ToString(),
            UserId = userId.ToString(),
            UserName = userName
        };
    }

    /// <remarks>
    /// Group membership is a set - the hub joins in whatever order its queries answered, and a client
    /// cannot observe the order - so both sides are sorted. Asserting the sequence would be a flake.
    /// </remarks>
    private static void AssertGroups(IEnumerable<string> expected, IEnumerable<string> actual) =>
        Assert.Equal(expected.Order().ToList(), actual.Order().ToList());
}
