// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
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

/// <summary>
/// <c>MainHub</c>'s MSEL presence: selecting a MSEL, leaving it or disconnecting tells the MSEL's group who
/// is there now, as a state with a version, once the set of users has changed; selecting also answers with
/// that state.
/// </summary>
/// <remarks>
/// Each connection is a hub over a harness of its own, all sharing one tracker, which is how SignalR runs
/// them: a hub per invocation, one singleton tracker.
/// </remarks>
public class MainHubPresenceTests(DatabaseFixture fixture) : DatabaseTestBase(fixture)
{
    private readonly PresenceTracker _presence = new();

    [Fact]
    public async Task SelectMsel_tells_the_group_who_is_there_and_answers_with_the_same_state()
    {
        var msel = await SeedMsel();
        var alice = Connect("Alice");

        var answer = await alice.Hub.SelectMsel([msel.Id]);

        var sent = Assert.Single(Sent(alice.Harness.Group(msel.Id.ToString())));
        Assert.Equal(answer, sent);
        Assert.Equal(msel.Id, answer.MselId);
        Assert.Equal([new PresentUser(alice.Harness.UserId, "Alice")], answer.Users);
    }

    [Fact]
    public async Task SelectMsel_answers_with_everyone_present_the_caller_included()
    {
        var msel = await SeedMsel();
        var alice = Connect("Alice");
        var bob = Connect("Bob");
        await alice.Hub.SelectMsel([msel.Id]);

        var answer = await bob.Hub.SelectMsel([msel.Id]);

        Assert.Equal(
            [new PresentUser(alice.Harness.UserId, "Alice"), new PresentUser(bob.Harness.UserId, "Bob")],
            answer.Users);
    }

    [Fact]
    public async Task Each_change_is_sent_with_a_higher_version()
    {
        var msel = await SeedMsel();
        var alice = Connect("Alice");
        var bob = Connect("Bob");

        var first = await alice.Hub.SelectMsel([msel.Id]);
        var second = await bob.Hub.SelectMsel([msel.Id]);
        await bob.Hub.SelectMsel([]);
        var third = await Connect("Carol").Hub.SelectMsel([msel.Id]);

        Assert.True(second.Version > first.Version);
        Assert.True(third.Version > second.Version);
    }

    /// <summary>
    /// Verifies: a user with the MSEL open in two tabs is announced once and is still present when one tab
    /// closes. The earlier cache sent a departure for the user as soon as either tab closed.
    /// </summary>
    [Fact]
    public async Task Closing_one_of_two_tabs_keeps_the_user_present_and_sends_nothing()
    {
        var msel = await SeedMsel();
        var alice = Guid.NewGuid();
        var firstTab = Connect("Alice", alice, "alice-tab-1");
        var secondTab = Connect("Alice", alice, "alice-tab-2");
        await firstTab.Hub.SelectMsel([msel.Id]);
        var second = await secondTab.Hub.SelectMsel([msel.Id]);

        await firstTab.Hub.OnDisconnectedAsync(null);

        // The first tab's join was the only announcement.
        Assert.Single(Sent(firstTab.Harness.Group(msel.Id.ToString())));
        Assert.Equal([new PresentUser(alice, "Alice")], second.Users);
        var bobs = await Connect("Bob").Hub.SelectMsel([msel.Id]);
        Assert.Contains(new PresentUser(alice, "Alice"), bobs.Users);
    }

    [Fact]
    public async Task SelectMsel_again_on_the_same_connection_sends_nothing_and_answers_with_the_same_state()
    {
        var msel = await SeedMsel();
        var alice = Connect("Alice");
        var first = await alice.Hub.SelectMsel([msel.Id]);

        var again = await alice.Hub.SelectMsel([msel.Id]);

        // Only the first selection announced anyone: the others never saw Alice leave and come back.
        Assert.Single(Sent(alice.Harness.Group(msel.Id.ToString())));
        Assert.Equal(first.Version, again.Version);
        Assert.Equal(first.Users, again.Users);
    }

    [Fact]
    public async Task SelectMsel_for_a_msel_the_caller_cannot_reach_joins_nothing_and_answers_nothing()
    {
        var strangers = await SeedMsel();
        var actor = Guid.NewGuid();
        var alice = Connect("Alice", actor, access: false);

        var answer = await alice.Hub.SelectMsel([strangers.Id]);

        Assert.Null(answer);
        alice.Harness.NothingAddressed();
        Assert.Empty(alice.Harness.JoinedGroups);
    }

    [Fact]
    public async Task SelectMsel_with_two_ids_joins_neither()
    {
        var first = await SeedMsel();
        var second = await SeedMsel();
        var alice = Connect("Alice");

        var answer = await alice.Hub.SelectMsel([first.Id, second.Id]);

        Assert.Null(answer);
        Assert.Empty(alice.Harness.JoinedGroups);
        alice.Harness.NothingAddressed();
    }

    [Fact]
    public async Task SelectMsel_with_no_ids_leaves_the_msel_and_tells_the_group()
    {
        var msel = await SeedMsel();
        var alice = Connect("Alice");
        var bob = Connect("Bob");
        await alice.Hub.SelectMsel([msel.Id]);
        await bob.Hub.SelectMsel([msel.Id]);

        var answer = await alice.Hub.SelectMsel([]);

        Assert.Null(answer);
        var last = Sent(alice.Harness.Group(msel.Id.ToString())).Last();
        Assert.Equal([new PresentUser(bob.Harness.UserId, "Bob")], last.Users);
    }

    [Fact]
    public async Task SelectMsel_switching_msels_tells_the_old_one_and_the_new_one()
    {
        var was = await SeedMsel();
        var now = await SeedMsel();
        var alice = Connect("Alice");
        await alice.Hub.SelectMsel([was.Id]);

        await alice.Hub.SelectMsel([now.Id]);

        Assert.Empty(Sent(alice.Harness.Group(was.Id.ToString())).Last().Users);
        Assert.Equal(
            [new PresentUser(alice.Harness.UserId, "Alice")],
            Sent(alice.Harness.Group(now.Id.ToString())).Single().Users);
    }

    [Fact]
    public async Task Leave_tells_the_group_who_is_left()
    {
        var msel = await SeedMsel();
        var alice = Connect("Alice");
        var bob = Connect("Bob");
        await alice.Hub.SelectMsel([msel.Id]);
        await bob.Hub.SelectMsel([msel.Id]);

        await alice.Hub.Leave();

        Assert.Equal(
            [new PresentUser(bob.Harness.UserId, "Bob")],
            Sent(alice.Harness.Group(msel.Id.ToString())).Last().Users);
    }

    [Fact]
    public async Task Leave_without_having_selected_a_msel_sends_nothing()
    {
        var alice = Connect("Alice");

        await alice.Hub.Leave();

        Assert.All(alice.Harness.Clients.ReceivedCalls(), x => Assert.NotEqual(nameof(IHubCallerClients.Group), x.GetMethodInfo().Name));
    }

    [Fact]
    public async Task Disconnecting_tells_the_msel_the_connection_was_the_users_last_one_to()
    {
        var msel = await SeedMsel();
        var alice = Connect("Alice");
        await alice.Hub.SelectMsel([msel.Id]);

        await alice.Hub.OnDisconnectedAsync(null);

        Assert.Empty(Sent(alice.Harness.Group(msel.Id.ToString())).Last().Users);
    }

    [Fact]
    public async Task Disconnecting_without_having_selected_a_msel_sends_nothing()
    {
        var alice = Connect("Alice");

        await alice.Hub.OnDisconnectedAsync(null);

        alice.Harness.NothingAddressed();
    }

    /// <summary>
    /// Verifies: a send that fails on disconnect doesn't leave the connection listed. Presence keeps
    /// one MSEL per connection, so there is no later send to stop.
    /// </summary>
    [Fact]
    public async Task Disconnecting_forgets_the_connection_when_a_send_fails()
    {
        var msel = await SeedMsel();
        var alice = Connect("Alice", connectionId: "alice-tab-1");
        await alice.Hub.SelectMsel([msel.Id]);
        alice.Harness.Group(msel.Id.ToString())
            .SendCoreAsync(MainHubMethods.PresenceChanged, Arg.Any<object[]>(), Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new InvalidOperationException("send failed"));

        await alice.Hub.OnDisconnectedAsync(null);

        Assert.Empty(_presence.LeaveAll("alice-tab-1"));
    }

    /// <summary>
    /// Verifies: a select that was still waiting when the connection closed leaves no one registered, so
    /// the others never see a user who is gone.
    /// </summary>
    [Fact]
    public async Task SelectMsel_on_a_connection_that_closed_meanwhile_leaves_no_one_present()
    {
        var msel = await SeedMsel();
        var alice = Connect("Alice");
        var bob = Connect("Bob");
        using var aborted = new CancellationTokenSource();
        alice.Harness.Context.ConnectionAborted.Returns(aborted.Token);
        alice.Harness.Groups
            .AddToGroupAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                aborted.Cancel();
                return Task.CompletedTask;
            });

        var answer = await alice.Hub.SelectMsel([msel.Id]);
        var bobs = await bob.Hub.SelectMsel([msel.Id]);

        Assert.Null(answer);
        Assert.Equal([new PresentUser(bob.Harness.UserId, "Bob")], bobs.Users);
    }

    /// <summary>
    /// Verifies: the presence methods that took any MSEL id from any caller, <c>GetPresence</c> and
    /// <c>Greet</c>, are gone; presence is only ever sent for a MSEL the caller selected.
    /// </summary>
    [Fact]
    public void The_hub_has_no_method_that_names_a_msel_to_read_or_announce_presence_in()
    {
        Assert.Null(typeof(MainHub).GetMethod("GetPresence"));
        Assert.Null(typeof(MainHub).GetMethod("Greet"));
    }

    [Fact]
    public async Task A_caller_with_no_name_claim_is_listed_as_Unknown()
    {
        var msel = await SeedMsel();
        var alice = Connect(null);

        var answer = await alice.Hub.SelectMsel([msel.Id]);

        Assert.Equal("Unknown", Assert.Single(answer.Users).Name);
    }

    // ---- helpers --------------------------------------------------------------------------------

    private sealed record Connection(MainHub Hub, HubHarness Harness);

    /// <summary>Every presence state sent to <paramref name="group"/>, oldest first.</summary>
    private static IReadOnlyList<MselPresence> Sent(IClientProxy group) =>
        [.. group.ReceivedCalls()
            .Where(x => x.GetMethodInfo().Name == nameof(IClientProxy.SendCoreAsync))
            .Select(x => x.GetArguments())
            .Where(x => (string)x[0] == MainHubMethods.PresenceChanged)
            .Select(x => (MselPresence)((object[])x[1])[0])];

    private async Task<Blueprint.Api.Data.Models.MselEntity> SeedMsel()
    {
        var msel = TestData.Msel();
        await Seed(msel);
        return msel;
    }

    /// <summary>
    /// A connection of a user who can reach every MSEL (a holder of ViewMsels), which is all selecting a
    /// MSEL needs; <paramref name="access"/> false makes it one who can reach none.
    /// </summary>
    private Connection Connect(string name, Guid? userId = null, string connectionId = null, bool access = true)
    {
        var id = userId ?? Guid.NewGuid();
        var claims = new List<Claim> { new("sub", id.ToString()) };
        if (name is not null)
            claims.Add(new Claim("name", name));
        var harness = new HubHarness(id, new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")), connectionId ?? Guid.NewGuid().ToString());

        var principal = new ClaimsPrincipalBuilder();
        if (access)
            principal.WithSystemPermissions(SystemPermission.ViewMsels);
        var userClaims = Substitute.For<IUserClaimsService>();
        userClaims.GetCurrentClaimsPrincipal().Returns((ClaimsPrincipal)null);
        var identity = Substitute.For<IIdentityResolver>();
        identity.GetClaimsPrincipal().Returns(principal.Build());
        var authorization = AuthorizationHarness.CreateBlueprintAuthorizationService(userClaims, identity);

        var hub = harness.Attach(new MainHub(null, null, Db, null, authorization, _presence, NullLogger<MainHub>.Instance));

        return new Connection(hub, harness);
    }
}
