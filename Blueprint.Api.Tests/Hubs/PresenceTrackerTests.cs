// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the scope root for license information.

using System;
using System.Linq;
using System.Threading.Tasks;
using Xunit;
using Blueprint.Api.Hubs;

namespace Blueprint.Api.Tests.Hubs;

/// <summary>
/// <see cref="PresenceTracker"/>: a user is present in a scope while any of their connections has
/// it open, and every change to who is present comes back as a state with a higher version.
/// </summary>
public class PresenceTrackerTests
{
    private readonly PresenceTracker _tracker = new();
    private readonly Guid _scope = Guid.NewGuid();
    private readonly PresentUser _alice = new(Guid.NewGuid(), "Alice");
    private readonly PresentUser _bob = new(Guid.NewGuid(), "bob");

    [Fact]
    public void Join_changes_who_is_present_for_the_first_connection_only()
    {
        Assert.True(_tracker.Join(_scope, "a1", _alice).Changed);
        Assert.False(_tracker.Join(_scope, "a2", _alice).Changed);
        Assert.False(_tracker.Join(_scope, "a1", _alice).Changed);
        Assert.True(_tracker.Join(_scope, "b1", _bob).Changed);
    }

    [Fact]
    public void Join_to_another_scope_changes_who_is_present_there()
    {
        _tracker.Join(_scope, "a1", _alice);

        Assert.True(_tracker.Join(Guid.NewGuid(), "a1", _alice).Changed);
    }

    [Fact]
    public void The_state_lists_each_user_once_ordered_by_name_ignoring_case_the_caller_included()
    {
        _tracker.Join(_scope, "b1", _bob);
        _tracker.Join(_scope, "a1", _alice);
        var state = _tracker.Join(_scope, "a2", _alice).State;

        Assert.Equal(_scope, state.ScopeId);
        Assert.Equal([_alice, _bob], state.Users);
    }

    [Fact]
    public void Each_change_raises_the_version_and_a_repeat_join_leaves_it()
    {
        var first = _tracker.Join(_scope, "a1", _alice).State.Version;
        var second = _tracker.Join(_scope, "b1", _bob).State.Version;
        var repeat = _tracker.Join(_scope, "b1", _bob).State.Version;
        var secondTab = _tracker.Join(_scope, "a2", _alice).State.Version;

        Assert.True(second > first);
        Assert.Equal(second, repeat);
        Assert.Equal(second, secondTab);
    }

    [Fact]
    public void Leave_changes_who_is_present_for_the_last_connection_only()
    {
        _tracker.Join(_scope, "a1", _alice);
        _tracker.Join(_scope, "a2", _alice);

        var firstTab = _tracker.Leave(_scope, "a1");
        var lastTab = _tracker.Leave(_scope, "a2");

        Assert.False(firstTab.Changed);
        Assert.Equal([_alice], firstTab.State.Users);
        Assert.True(lastTab.Changed);
        Assert.Empty(lastTab.State.Users);
        Assert.True(lastTab.State.Version > firstTab.State.Version);
    }

    [Fact]
    public void Leave_of_a_connection_that_never_joined_is_nothing()
    {
        _tracker.Join(_scope, "a1", _alice);

        Assert.Null(_tracker.Leave(_scope, "b1"));
        Assert.Null(_tracker.Leave(Guid.NewGuid(), "a1"));
    }

    [Fact]
    public void A_version_never_goes_back_when_a_scope_empties_and_fills_again()
    {
        var before = _tracker.Join(_scope, "a1", _alice).State.Version;
        var emptied = _tracker.Leave(_scope, "a1").State.Version;

        var after = _tracker.Join(_scope, "a2", _alice).State.Version;

        Assert.True(emptied > before);
        Assert.True(after > emptied);
    }

    [Fact]
    public void LeaveAll_reports_each_scope_the_connection_had_open()
    {
        var other = Guid.NewGuid();
        _tracker.Join(_scope, "a1", _alice);
        _tracker.Join(other, "a1", _alice);
        _tracker.Join(other, "a2", _alice);

        var changes = _tracker.LeaveAll("a1");

        var gone = Assert.Single(changes, x => x.State.ScopeId == _scope);
        Assert.True(gone.Changed);
        Assert.Empty(gone.State.Users);
        var kept = Assert.Single(changes, x => x.State.ScopeId == other);
        Assert.False(kept.Changed);
        Assert.Equal([_alice], kept.State.Users);
        Assert.Empty(_tracker.LeaveAll("a1"));
    }

    [Fact]
    public async Task Concurrent_tabs_of_one_user_arrive_once_and_depart_once()
    {
        var connections = Enumerable.Range(0, 64).Select(i => $"a{i}").ToArray();

        var arrivals = await Task.WhenAll(connections.Select(c => Task.Run(() => _tracker.Join(_scope, c, _alice))));
        var departures = await Task.WhenAll(connections.Select(c => Task.Run(() => _tracker.Leave(_scope, c))));

        Assert.Single(arrivals, x => x.Changed);
        Assert.Single(departures, x => x.Changed);
        Assert.Empty(departures.Single(x => x.Changed).State.Users);
    }

    [Fact]
    public void ScopesOf_lists_the_scopes_a_connection_is_present_in()
    {
        var other = Guid.NewGuid();
        _tracker.Join(_scope, "a1", _alice);
        _tracker.Join(other, "a1", _alice);
        _tracker.Join(other, "b1", _bob);

        Assert.Equal(new[] { _scope, other }.Order(), _tracker.ScopesOf("a1").Order());
        Assert.Equal([other], _tracker.ScopesOf("b1"));
        Assert.Empty(_tracker.ScopesOf("nobody"));
    }
}
