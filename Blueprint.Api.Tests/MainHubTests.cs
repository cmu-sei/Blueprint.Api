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
using Blueprint.Api.Tests.Infrastructure;
using NSubstitute;
using Xunit;

namespace Blueprint.Api.Tests;

/// <summary>
/// <c>MainHub</c> - the one SignalR hub in the API, and the whole of what blueprint.ui subscribes to.
/// Eight methods: four that decide which groups a connection belongs to, and four that maintain the
/// per-MSEL presence list.
/// </summary>
/// <remarks>
/// Driven by direct invocation through <see cref="HubHarness"/>, because the group names are the contract
/// and nothing else can see them. <see cref="MainHubConnectionTests"/> covers what only a real
/// <c>HubConnection</c> can prove - that the hub is mapped where clients dial, that every one of its
/// endpoints is authorized, and that an invocation arrives and answers.
/// <para />
/// <c>MainHub</c> takes its <c>BlueprintContext</c> by constructor injection, so it can be driven against
/// this test's own database with no host at all - which is why the split works here and did not in vm.api,
/// whose hub resolved its context per invocation and so could only ever read the host's.
/// <para />
/// BUG: three of the six constructor dependencies - <c>ITeamService</c>, <c>IMselService</c> and
/// <c>DatabaseOptions</c> - are assigned and never read. <see cref="Hub"/> passes null for all three, so
/// every test in this file is the assertion; one of them turns red the day a hub method starts using one.
/// <para />
/// BUG: the two cancellation tokens the hub has are both useless. <c>_ct</c> comes from a
/// <c>CancellationTokenSource</c> the constructor creates and nothing ever cancels, <c>GetAdminIdList</c>
/// builds a second one with <c>new CancellationToken()</c> and uses it for three of its four checks, and
/// not one of the six database queries is passed a token at all - so a client that disconnects mid-call
/// leaves the query running.
/// </remarks>
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

    /// <remarks>
    /// BUG: "all templates" is in the same clause as "MSELs I created", so every connected user is
    /// joined to the group of every template in the installation and receives every edit made to one.
    /// Templates are readable by design (<c>MselViewRequirement</c> says so), but reading one on request
    /// and being pushed every change to one are different things, and the second is what a client with no
    /// interest in templates cannot opt out of.
    /// </remarks>
    [Fact]
    public async Task Join_AddsAGroupPerReachableMsel_PerUnit_AndOneForTheCallerThemselves()
    {
        // A real user row, because a unit membership is a foreign key to one.
        var user = BlueprintAppFactory.User();
        await Seed(user);
        var actor = user.Id;
        var mine = BlueprintAppFactory.Msel(createdBy: actor);
        var template = BlueprintAppFactory.Msel(isTemplate: true);
        var strangers = BlueprintAppFactory.Msel();
        var unitsMsel = BlueprintAppFactory.Msel();
        await Seed(mine, template, strangers, unitsMsel);
        var unit = BlueprintAppFactory.Unit();
        await Seed(unit);
        await Seed(
            BlueprintAppFactory.UnitUser(actor, unit.Id),
            BlueprintAppFactory.MselUnit(unit.Id, unitsMsel.Id));
        var harness = new HubHarness(actor);

        await Hub(harness).Join();

        AssertGroups(
            [
                mine.Id.ToString(),
                template.Id.ToString(),
                unitsMsel.Id.ToString(),
                unit.Id.ToString(),
                actor.ToString()
            ],
            harness.Added);
        Assert.DoesNotContain(strangers.Id.ToString(), harness.Added);
        Assert.Empty(harness.Sends);
    }

    /// <remarks>
    /// BUG: the permission that decides whether a connection sees every MSEL in the installation is
    /// <see cref="SystemPermission.ManageUsers"/> - a user-administration permission that says nothing
    /// about MSELs. <see cref="SystemPermission.ViewMsels"/> is what every read route in the API asks for
    /// and is not consulted here, so a holder of it is joined to the MSELs they belong to and told nothing
    /// about the rest, while a holder of <c>ManageUsers</c> and no MSEL permission at all receives every
    /// edit to every exercise.
    /// </remarks>
    [Fact]
    public async Task Join_WithManageUsers_AddsEveryMselInTheInstallation()
    {
        var actor = Guid.NewGuid();
        var strangers = BlueprintAppFactory.Msel();
        var archived = BlueprintAppFactory.Msel(status: MselItemStatus.Archived);
        await Seed(strangers, archived);
        var harness = new HubHarness(actor);

        await Hub(harness, SystemPermission.ManageUsers).Join();

        AssertGroups([strangers.Id.ToString(), actor.ToString()], harness.Added);
        Assert.DoesNotContain(archived.Id.ToString(), harness.Added);
    }

    [Fact]
    public async Task Join_SkipsAnArchivedMsel_EvenTheCallersOwn()
    {
        var actor = Guid.NewGuid();
        var live = BlueprintAppFactory.Msel(createdBy: actor);
        var archived = BlueprintAppFactory.Msel(createdBy: actor, status: MselItemStatus.Archived);
        var archivedTemplate = BlueprintAppFactory.Msel(
            isTemplate: true, status: MselItemStatus.Archived);
        await Seed(live, archived, archivedTemplate);
        var harness = new HubHarness(actor);

        await Hub(harness).Join();

        AssertGroups([live.Id.ToString(), actor.ToString()], harness.Added);
    }

    /// <remarks>
    /// The <c>sub</c> claim is read with <c>Claims.First(...)</c> rather than <c>FirstOrDefault</c>, so a
    /// principal without one is an <c>InvalidOperationException</c> out of the hub method. Unreachable
    /// over a real connection - <c>TestAuthHandler</c> refuses a request with no <c>X-Test-User</c> and
    /// Keycloak always mints a <c>sub</c> - and pinned here because it is the difference between the two
    /// ways this hub is driven.
    /// </remarks>
    [Fact]
    public async Task Join_ForACallerWithNoSubClaim_Throws()
    {
        var harness = new HubHarness([new Claim("name", "no-sub")]);

        await Assert.ThrowsAsync<InvalidOperationException>(() => Hub(harness).Join());
    }

    // ---------------------------------------------------------------------------------------------
    // Leave
    // ---------------------------------------------------------------------------------------------

    /// <remarks>
    /// BUG: <c>Join</c> adds a group per unit the caller belongs to and <c>Leave</c> removes only the MSEL
    /// groups and the caller's own, so a connection that leaves and rejoins accumulates nothing but the
    /// unit groups are never given up. Harmless while the connection lives, because SignalR drops every
    /// group on disconnect - but it makes <c>Leave</c> a partial undo of <c>Join</c>, and a client calling
    /// it to stop receiving updates still receives everything addressed to its units.
    /// </remarks>
    [Fact]
    public async Task Leave_RemovesTheMselGroupsAndTheCallersOwn_ButNotTheUnitGroups()
    {
        var user = BlueprintAppFactory.User();
        await Seed(user);
        var actor = user.Id;
        var msel = BlueprintAppFactory.Msel(createdBy: actor);
        await Seed(msel);
        var unit = BlueprintAppFactory.Unit();
        await Seed(unit);
        await Seed(BlueprintAppFactory.UnitUser(actor, unit.Id));
        var harness = new HubHarness(actor);
        var hub = Hub(harness);

        await hub.Join();
        await hub.Leave();

        Assert.Contains(unit.Id.ToString(), harness.Added);
        AssertGroups([msel.Id.ToString(), actor.ToString()], harness.Removed);
    }

    /// <remarks>
    /// The departure names the caller as their claims describe them now, where
    /// <c>OnDisconnectedAsync</c> names them as they were when they selected the MSEL. The two payloads
    /// are built from different sources for the same event.
    /// </remarks>
    [Fact]
    public async Task Leave_ForATrackedConnection_BroadcastsTheClaimsIdentity_AndForgetsTheConnection()
    {
        var actor = Guid.NewGuid();
        var msel = BlueprintAppFactory.Msel(createdBy: actor);
        await Seed(msel);
        var harness = new HubHarness(actor, "current-name");
        Track(msel.Id, actor, "stale-name");

        await Hub(harness).Leave();

        var send = Assert.Single(harness.Of(MainHubMethods.PresenceDeparted));
        Assert.Equal(HubHarness.ToOthersInGroup, send.Form);
        Assert.Equal(msel.Id.ToString(), send.Group);
        Assert.Equal((actor.ToString(), "current-name"), HubHarness.Identity(send.Payload));
        Assert.Empty(_cache.Connections);
    }

    [Fact]
    public async Task Leave_ForAConnectionNobodyIsTracking_BroadcastsNothing()
    {
        var actor = Guid.NewGuid();
        var msel = BlueprintAppFactory.Msel(createdBy: actor);
        await Seed(msel);
        Track(msel.Id, Guid.NewGuid(), "somebody-else", "another-connection");
        var harness = new HubHarness(actor);

        await Hub(harness).Leave();

        Assert.Empty(harness.Sends);
        Assert.Single(_cache.Connections);
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
        var harness = new HubHarness(Guid.NewGuid(), "whoever-is-connected-now");

        await Hub(harness).OnDisconnectedAsync(null);

        var send = Assert.Single(harness.Of(MainHubMethods.PresenceDeparted));
        Assert.Equal(HubHarness.ToOthersInGroup, send.Form);
        Assert.Equal(mselId.ToString(), send.Group);
        Assert.Equal((whoTheyWere.ToString(), "cached-name"), HubHarness.Identity(send.Payload));
        Assert.Empty(_cache.Connections);
        Assert.Empty(harness.Removed);
    }

    [Fact]
    public async Task OnDisconnected_ForAConnectionNobodyIsTracking_BroadcastsNothing()
    {
        var harness = new HubHarness(Guid.NewGuid());

        await Hub(harness).OnDisconnectedAsync(null);

        Assert.Empty(harness.Sends);
    }

    /// <remarks>
    /// A connection is tracked only by <c>SelectMsel</c>, which always writes a MSEL id, so a cached
    /// connection with none is not a state the hub can reach on its own. It is reachable through the
    /// cache being a host-wide singleton any code may write to, and the guard is what keeps a departure
    /// from being addressed to a group named by the empty string - which is the same defect four of the
    /// 25 event handlers do have.
    /// </remarks>
    [Fact]
    public async Task OnDisconnected_ForATrackedConnectionOnNoMsel_ForgetsItSilently()
    {
        Track(mselId: null, Guid.NewGuid(), "on-no-msel");
        var harness = new HubHarness(Guid.NewGuid());

        await Hub(harness).OnDisconnectedAsync(null);

        Assert.Empty(harness.Sends);
        Assert.Empty(_cache.Connections);
    }

    // ---------------------------------------------------------------------------------------------
    // SelectMsel
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task SelectMsel_ForAReachableMsel_JoinsIt_TracksTheConnection_AndTellsTheOthers()
    {
        var actor = Guid.NewGuid();
        var msel = BlueprintAppFactory.Msel(createdBy: actor);
        await Seed(msel);
        var harness = new HubHarness(actor, "arriving");

        await Hub(harness).SelectMsel([msel.Id]);

        AssertGroups([msel.Id.ToString()], harness.Added);
        AssertGroups([msel.Id.ToString()], harness.Removed);
        var send = Assert.Single(harness.Of(MainHubMethods.PresenceArrived));
        Assert.Equal(HubHarness.ToOthersInGroup, send.Form);
        Assert.Equal(msel.Id.ToString(), send.Group);
        Assert.Equal((actor.ToString(), "arriving"), HubHarness.Identity(send.Payload));
        var tracked = Assert.Single(_cache.Connections).Value;
        Assert.Equal(HubHarness.DefaultConnectionId, tracked.ConnectionId);
        Assert.Equal(msel.Id.ToString(), tracked.MselId);
        Assert.Equal(actor.ToString(), tracked.UserId);
        Assert.Equal("arriving", tracked.UserName);
    }

    /// <remarks>
    /// BUG: a MSEL the caller cannot reach is ignored in silence - no group, no cache entry, no error and
    /// no answer, because <c>SelectMsel</c> returns <c>Task</c>. So a client that selects a MSEL it is not
    /// entitled to believes it is subscribed, receives nothing, and has no way to tell that from an
    /// exercise nobody is editing. Every other authorization decision in the API answers 403.
    /// </remarks>
    [Fact]
    public async Task SelectMsel_ForAMselTheCallerCannotReach_JoinsNothing_AndSaysNothing()
    {
        var actor = Guid.NewGuid();
        var strangers = BlueprintAppFactory.Msel();
        await Seed(strangers);
        var harness = new HubHarness(actor);

        await Hub(harness).SelectMsel([strangers.Id]);

        Assert.Empty(harness.Added);
        Assert.Empty(harness.Sends);
        Assert.Empty(_cache.Connections);
    }

    [Fact]
    public async Task SelectMsel_WithNoIds_LeavesEveryMselGroup_AndForgetsTheConnection()
    {
        var actor = Guid.NewGuid();
        var msel = BlueprintAppFactory.Msel(createdBy: actor);
        var other = BlueprintAppFactory.Msel(createdBy: actor);
        await Seed(msel, other);
        var harness = new HubHarness(actor, "departing");
        Track(msel.Id, actor, "departing");

        await Hub(harness).SelectMsel([]);

        Assert.Empty(harness.Added);
        AssertGroups([msel.Id.ToString(), other.Id.ToString()], harness.Removed);
        var send = Assert.Single(harness.Of(MainHubMethods.PresenceDeparted));
        Assert.Equal(msel.Id.ToString(), send.Group);
        Assert.Empty(_cache.Connections);
    }

    /// <remarks>
    /// BUG: the join is guarded by <c>args.Count() == 1</c> with no <c>else</c> that reports anything, so
    /// selecting two MSELs leaves the caller in neither - the same silent no-op as an unauthorized id,
    /// reached by a different route. The parameter is an array because the UI sends one, and nothing in
    /// the hub says only one element is understood.
    /// </remarks>
    [Fact]
    public async Task SelectMsel_WithTwoIds_JoinsNeither()
    {
        var actor = Guid.NewGuid();
        var first = BlueprintAppFactory.Msel(createdBy: actor);
        var second = BlueprintAppFactory.Msel(createdBy: actor);
        await Seed(first, second);
        var harness = new HubHarness(actor);

        await Hub(harness).SelectMsel([first.Id, second.Id]);

        Assert.Empty(harness.Added);
        Assert.Empty(harness.Sends);
        Assert.Empty(_cache.Connections);
    }

    [Fact]
    public async Task SelectMsel_SwitchingMsels_TellsTheOldOneAndTheNewOne()
    {
        var actor = Guid.NewGuid();
        var was = BlueprintAppFactory.Msel(createdBy: actor);
        var now = BlueprintAppFactory.Msel(createdBy: actor);
        await Seed(was, now);
        var harness = new HubHarness(actor, "switching");
        Track(was.Id, actor, "switching");

        await Hub(harness).SelectMsel([now.Id]);

        Assert.Equal(was.Id.ToString(), Assert.Single(harness.Of(MainHubMethods.PresenceDeparted)).Group);
        Assert.Equal(now.Id.ToString(), Assert.Single(harness.Of(MainHubMethods.PresenceArrived)).Group);
        Assert.Equal(now.Id.ToString(), Assert.Single(_cache.Connections).Value.MselId);
    }

    // ---------------------------------------------------------------------------------------------
    // Greet
    // ---------------------------------------------------------------------------------------------

    /// <remarks>
    /// BUG: <c>Greet</c> checks nothing at all - not the caller's permissions, not their reachable MSELs,
    /// not even that the argument is a <see cref="Guid"/>. It reads two claims and broadcasts, so any
    /// authenticated caller may announce themselves to the people editing any exercise in the
    /// installation, under whatever display name their token carries. Whoever a client is showing as
    /// present on a MSEL is therefore not a list of people entitled to be there.
    /// </remarks>
    [Fact]
    public async Task Greet_ForAMselTheCallerCannotReach_BroadcastsAnyway()
    {
        var actor = Guid.NewGuid();
        var strangers = BlueprintAppFactory.Msel();
        await Seed(strangers);
        var harness = new HubHarness(actor, "gatecrasher");

        await Hub(harness).Greet(strangers.Id.ToString());

        var send = Assert.Single(harness.Of(MainHubMethods.PresenceGreeted));
        Assert.Equal(HubHarness.ToOthersInGroup, send.Form);
        Assert.Equal(strangers.Id.ToString(), send.Group);
        Assert.Equal((actor.ToString(), "gatecrasher"), HubHarness.Identity(send.Payload));
    }

    /// <remarks>
    /// Two facts on one screen: the group name is whatever string arrived - <c>Greet</c> touches no
    /// database and parses nothing, so "not-a-msel" is a group like any other - and a caller whose token
    /// carries no <c>name</c> is announced as "Unknown". Blueprint's Keycloak realm ships users without
    /// one (<c>MoodleOAuth</c> and the requirement tests both rely on that), so the fallback is the
    /// ordinary case rather than the edge.
    /// </remarks>
    [Fact]
    public async Task Greet_ForACallerWithNoNameClaim_AnnouncesThemAsUnknown()
    {
        var actor = Guid.NewGuid();
        var harness = new HubHarness(actor, userName: null);

        await Hub(harness).Greet("not-a-msel");

        var send = Assert.Single(harness.Of(MainHubMethods.PresenceGreeted));
        Assert.Equal("not-a-msel", send.Group);
        Assert.Equal((actor.ToString(), "Unknown"), HubHarness.Identity(send.Payload));
    }

    // ---------------------------------------------------------------------------------------------
    // GetPresence
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task GetPresence_ListsEverybodyElseOnThatMsel_AndNotTheCaller()
    {
        var actor = Guid.NewGuid();
        var msel = BlueprintAppFactory.Msel(createdBy: actor);
        await Seed(msel);
        var colleague = Guid.NewGuid();
        Track(msel.Id, actor, "me");
        Track(msel.Id, colleague, "them", "their-connection");
        Track(Guid.NewGuid(), Guid.NewGuid(), "elsewhere", "a-third-connection");
        var harness = new HubHarness(actor, "me");

        var presence = await Hub(harness).GetPresence(msel.Id.ToString());

        Assert.Equal(
            (colleague.ToString(), "them"), HubHarness.Identity(Assert.Single(presence)));
    }

    /// <remarks>
    /// BUG: <c>GetPresence</c> checks nothing either, so any authenticated caller may ask who is working
    /// on any MSEL and is answered every connected user's id and display name. Together with
    /// <c>Greet</c> above, the presence half of this hub is unauthorized end to end: one method to read
    /// who is there and one to join them.
    /// </remarks>
    [Fact]
    public async Task GetPresence_ForAMselTheCallerCannotReach_ListsItsOccupantsAnyway()
    {
        var actor = Guid.NewGuid();
        var strangers = BlueprintAppFactory.Msel();
        await Seed(strangers);
        var occupant = Guid.NewGuid();
        Track(strangers.Id, occupant, "somebody-working", "their-connection");
        var harness = new HubHarness(actor);

        var presence = await Hub(harness).GetPresence(strangers.Id.ToString());

        Assert.Equal(
            (occupant.ToString(), "somebody-working"), HubHarness.Identity(Assert.Single(presence)));
    }

    [Fact]
    public async Task GetPresence_ForAMselNobodyIsOn_IsEmpty()
    {
        var actor = Guid.NewGuid();
        var msel = BlueprintAppFactory.Msel(createdBy: actor);
        await Seed(msel);
        Track(Guid.NewGuid(), Guid.NewGuid(), "on-another-msel", "their-connection");
        var harness = new HubHarness(actor);

        Assert.Empty(await Hub(harness).GetPresence(msel.Id.ToString()));
    }

    // ---------------------------------------------------------------------------------------------
    // JoinAdmin / LeaveAdmin
    // ---------------------------------------------------------------------------------------------

    /// <remarks>
    /// The four group names are half of the SignalR contract blueprint.ui is written against - the other
    /// half being <c>MainHubMethods</c>' method names - and they are spelt out in both repositories with
    /// nothing keeping the two copies honest. Phase 4's <c>signalr-contract.json</c> is where that gets
    /// pinned; this theory is what says which permission opens which.
    /// </remarks>
    [Theory]
    [InlineData(SystemPermission.EditMsels, MainHub.ADMIN_DATA_GROUP)]
    [InlineData(SystemPermission.ViewGroups, MainHub.GROUP_GROUP)]
    [InlineData(SystemPermission.ViewRoles, MainHub.ROLE_GROUP)]
    [InlineData(SystemPermission.ViewUsers, MainHub.USER_GROUP)]
    public async Task JoinAdmin_AddsTheGroupForThePermissionTheCallerHolds(
        SystemPermission permission, string group)
    {
        var actor = Guid.NewGuid();
        var harness = new HubHarness(actor);

        await Hub(harness, permission).JoinAdmin();

        AssertGroups([actor.ToString(), group], harness.Added);
    }

    [Fact]
    public async Task JoinAdmin_WithNoPermissions_AddsOnlyTheCallersOwnGroup()
    {
        var actor = Guid.NewGuid();
        var harness = new HubHarness(actor);

        await Hub(harness).JoinAdmin();

        AssertGroups([actor.ToString()], harness.Added);
    }

    [Fact]
    public async Task LeaveAdmin_RemovesExactlyWhatJoinAdminAdded()
    {
        var actor = Guid.NewGuid();
        var harness = new HubHarness(actor);
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
            harness.Added);
        Assert.Equal(harness.Added, harness.Removed);
    }

    /// <summary>
    /// A hub attached to <paramref name="harness"/>, reading this test's own database, whose caller holds
    /// <paramref name="permissions"/> and nothing else.
    /// </summary>
    /// <remarks>
    /// <c>ITeamService</c>, <c>IMselService</c> and <c>DatabaseOptions</c> are deliberately null: the hub
    /// assigns all three and reads none of them, so passing null is the assertion. The authorization
    /// service is a substitute because what it decides is a <see cref="SystemPermission"/> lookup that
    /// <see cref="BlueprintAuthorizationServiceTests"/> already covers whole; what matters here is which
    /// permission the hub asks about.
    /// </remarks>
    private MainHub Hub(HubHarness harness, params SystemPermission[] permissions)
    {
        var authorization = Substitute.For<IBlueprintAuthorizationService>();
        authorization
            .AuthorizeAsync(Arg.Any<SystemPermission[]>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<SystemPermission[]>().Any(x => permissions.Contains(x)));

        return harness.Attach(new MainHub(null, null, Db, null, authorization, _cache));
    }

    /// <summary>Puts a connection in the cache, as <c>SelectMsel</c> would have.</summary>
    private void Track(
        Guid? mselId, Guid userId, string userName, string connectionId = HubHarness.DefaultConnectionId)
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
