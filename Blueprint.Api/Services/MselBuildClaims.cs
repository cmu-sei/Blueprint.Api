// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Blueprint.Api.Infrastructure.Exceptions;

namespace Blueprint.Api.Services
{
    /// <summary>The browser tab building a MSEL with AI: its hub connection, and who it belongs to</summary>
    public record MselBuildClaim(string ConnectionId, Guid UserId, string UserName);

    /// <summary>
    /// Which tab is building each MSEL with AI, so two tabs, or two people, never run the same build at once. A tab is its
    /// hub connection, so closing it frees the build at once. Kept in memory, as the hub's presence is: a restart ends every
    /// build request too, and the next request from a tab claims the build again.
    /// </summary>
    public class MselBuildClaims
    {
        private readonly ConcurrentDictionary<Guid, MselBuildClaim> _claims = new();
        // the hub connections open now: a claim is only ever held by one of them
        private readonly ConcurrentDictionary<string, byte> _open = new();

        public void Connected(string connectionId) => _open[connectionId] = 0;

        private bool IsOpen(string connectionId) => connectionId != null && _open.ContainsKey(connectionId);
        // the steps running now: a stage, or one row, article or profile
        private readonly ConcurrentDictionary<(Guid MselId, string Key), byte> _running = new();

        // a claim whose tab's connection closed is no one's
        public MselBuildClaim Holder(Guid mselId) => _claims.TryGetValue(mselId, out var claim) && IsOpen(claim.ConnectionId) ? claim : null;

        /// <summary>
        /// Claims the build for a tab when no tab has it or this one does; with takeOver, from another tab too. Returns the
        /// claim it replaced, null when there was none or it was this tab's; throws when another tab has it.
        /// </summary>
        public MselBuildClaim Claim(Guid mselId, MselBuildClaim claim, bool takeOver)
        {
            // a request can carry a connection that has just closed, as when its tab reconnects under a new one; it writes
            // while no open tab has the build, but claims nothing, so no claim is left behind that nothing would free
            if (!IsOpen(claim.ConnectionId))
            {
                RequireUnclaimed(mselId, claim.UserId);
                return null;
            }
            while (true)
            {
                if (_claims.TryAdd(mselId, claim))
                    return null;
                if (!_claims.TryGetValue(mselId, out var holder))
                    continue;
                // a claim whose tab's connection closed is free
                if (!IsOpen(holder.ConnectionId))
                {
                    if (_claims.TryUpdate(mselId, claim, holder))
                        return null;
                    continue;
                }
                if (holder.ConnectionId == claim.ConnectionId)
                {
                    // the same tab, maybe after its user's name changed
                    if (holder == claim || _claims.TryUpdate(mselId, claim, holder))
                        return null;
                    continue;
                }
                if (!takeOver)
                    throw new ConflictException(Building(holder, claim));
                if (_claims.TryUpdate(mselId, claim, holder))
                    return holder;
            }
        }

        /// <summary>
        /// A request from a tab that did not say which it is: it may write only while no tab has the build, and claims nothing.
        /// </summary>
        public void RequireUnclaimed(Guid mselId, Guid userId)
        {
            if (Holder(mselId) is MselBuildClaim holder && IsOpen(holder.ConnectionId))
                throw new ConflictException(Building(holder, new MselBuildClaim(null, userId, null)));
        }

        /// <summary>Frees the build when this tab has it; true when it did</summary>
        public bool Release(Guid mselId, string connectionId) =>
            connectionId != null && _claims.TryGetValue(mselId, out var holder) && holder.ConnectionId == connectionId
                && _claims.TryRemove(new KeyValuePair<Guid, MselBuildClaim>(mselId, holder));

        /// <summary>Frees every build a closed tab had; returns their MSELs</summary>
        public List<Guid> ReleaseConnection(string connectionId)
        {
            _open.TryRemove(connectionId, out _);
            return _claims.Where(c => c.Value.ConnectionId == connectionId)
                .Where(c => _claims.TryRemove(c))
                .Select(c => c.Key)
                .ToList();
        }

        /// <summary>
        /// Marks a step as running until the result is disposed; throws when it already is, as when a tab that was taken
        /// over is still writing it.
        /// </summary>
        public IDisposable Run(Guid mselId, string key, string what)
        {
            if (!_running.TryAdd((mselId, key), 0))
                throw new ConflictException($"{what} already being written. Wait for it to finish.");
            return new Step(() => _running.TryRemove((mselId, key), out _));
        }

        private static string Building(MselBuildClaim holder, MselBuildClaim caller) =>
            holder.UserId == caller.UserId
                ? "You are building this MSEL with AI in another tab. Take over to build it here."
                : $"{holder.UserName ?? "Someone else"} is building this MSEL with AI. Take over to build it here.";

        private sealed class Step(Action done) : IDisposable
        {
            private Action _done = done;

            public void Dispose()
            {
                _done?.Invoke();
                _done = null;
            }
        }
    }
}
