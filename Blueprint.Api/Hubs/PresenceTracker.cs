// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

// This file deliberately uses nothing from Blueprint, so that it can move to a shared library unchanged.
using System;
using System.Collections.Generic;
using System.Linq;

namespace Blueprint.Api.Hubs;

/// <summary>A user present in a scope, as the others in it see them.</summary>
public record PresentUser(Guid Id, string Name);

/// <summary>
/// Everyone present in a scope, each user once, as of <see cref="Version"/>. Versions only increase, so a
/// client that has seen one state can tell that another is older and ignore it.
/// </summary>
public record PresenceState(Guid ScopeId, long Version, IReadOnlyList<PresentUser> Users);

/// <summary>
/// What a change to a scope's presence left behind. <see cref="Changed"/> is true only when the set of
/// users changed, the only time the others need telling.
/// </summary>
public record PresenceChange(PresenceState State, bool Changed);

/// <summary>
/// Who is present in each scope (a MSEL, a project, a view), by SignalR connection, so that a user with the
/// scope open in several tabs arrives with the first and departs with the last.
/// </summary>
/// <remarks>
/// In memory, like the hub groups it shadows: with more than one API instance each knows only the
/// connections made to it. Each change returns the state it produced, so the caller can send it after the
/// tracker has let go of its lock; the version is what keeps states that cross on the way in order.
/// </remarks>
public interface IPresenceTracker
{
    /// <summary>Records <paramref name="connectionId"/> as present in <paramref name="scopeId"/>.</summary>
    PresenceChange Join(Guid scopeId, string connectionId, PresentUser user);

    /// <summary>Forgets that <paramref name="connectionId"/> is present in <paramref name="scopeId"/>.</summary>
    /// <returns>The result, or null when the connection was not present in the scope.</returns>
    PresenceChange Leave(Guid scopeId, string connectionId);

    /// <summary>Forgets every scope <paramref name="connectionId"/> was present in, for a disconnect.</summary>
    /// <returns>The result for each scope the connection was present in.</returns>
    IReadOnlyList<PresenceChange> LeaveAll(string connectionId);
}

public class PresenceTracker : IPresenceTracker
{
    private readonly object _lock = new();

    // Counts every change to any scope, so a version never goes back when a scope empties and fills
    // again, and nothing is kept per scope once nobody is present in it.
    private long _version;

    // scopeId -> connectionId -> user
    private readonly Dictionary<Guid, Dictionary<string, PresentUser>> _scopes = [];

    public PresenceChange Join(Guid scopeId, string connectionId, PresentUser user)
    {
        lock (_lock)
        {
            if (!_scopes.TryGetValue(scopeId, out var connections))
            {
                connections = [];
                _scopes[scopeId] = connections;
            }

            if (connections.ContainsKey(connectionId))
                return new PresenceChange(StateLocked(scopeId), false);

            var alreadyPresent = connections.Values.Any(x => x.Id == user.Id);
            connections[connectionId] = user;

            return Result(scopeId, changed: !alreadyPresent);
        }
    }

    public PresenceChange Leave(Guid scopeId, string connectionId)
    {
        lock (_lock)
        {
            return LeaveLocked(scopeId, connectionId);
        }
    }

    public IReadOnlyList<PresenceChange> LeaveAll(string connectionId)
    {
        lock (_lock)
        {
            return [.. _scopes
                .Where(x => x.Value.ContainsKey(connectionId))
                .Select(x => x.Key)
                .ToList()
                .Select(scopeId => LeaveLocked(scopeId, connectionId))];
        }
    }

    private PresenceChange LeaveLocked(Guid scopeId, string connectionId)
    {
        if (!_scopes.TryGetValue(scopeId, out var connections) ||
            !connections.Remove(connectionId, out var user))
            return null;

        var stillPresent = connections.Values.Any(x => x.Id == user.Id);
        var result = Result(scopeId, changed: !stillPresent);

        if (connections.Count == 0)
            _scopes.Remove(scopeId);

        return result;
    }

    // A change that leaves the users as they were keeps the version, so only real changes are sent on.
    private PresenceChange Result(Guid scopeId, bool changed)
    {
        if (changed)
            _version++;

        return new PresenceChange(StateLocked(scopeId), changed);
    }

    private PresenceState StateLocked(Guid scopeId)
    {
        var users = _scopes.TryGetValue(scopeId, out var connections)
            ? connections.Values
                .GroupBy(x => x.Id)
                .Select(x => x.First())
                .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.Id)
                .ToList()
            : [];

        return new PresenceState(scopeId, _version, users);
    }
}
