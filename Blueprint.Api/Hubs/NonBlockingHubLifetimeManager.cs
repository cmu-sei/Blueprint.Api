// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Protocol;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Blueprint.Api.Hubs
{
    /// <summary>
    /// SignalR's lifetime manager, with sends that return without waiting for the clients they go to.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every send in the application, from an <see cref="IHubContext{THub}"/> or from <c>Clients</c> in a
    /// hub method, goes through this class. The entity event handlers send from inside
    /// <c>SaveChangesAsync</c>, so an HTTP write used to wait until every connection in the audience had
    /// accepted the broadcast. With stateful reconnect, a connection whose replay buffer is full (a closed
    /// tab inside SignalR's reconnect window, or a client that has stopped acknowledging) makes each write
    /// to it wait up to SignalR's fixed 5-second acknowledgement timeout, after which SignalR aborts that
    /// connection. A request that published to two such audiences waited twice.
    /// </para>
    /// <para>
    /// Sends are started here and not awaited. SignalR still writes to every connection whose write lock
    /// is free during the call, so healthy clients have the message before the request returns, and each
    /// connection receives messages in the order they were sent. Only writes to a connection that is busy
    /// or full carry on in the background, including during a reconnect replay, and a failure among them
    /// is logged. Because those writes may serialize the arguments later, a payload must be a snapshot
    /// that nothing changes afterwards; every send here builds a fresh view model.
    /// </para>
    /// <para>
    /// The caller's cancellation token is deliberately not passed on: a broadcast reports a write that
    /// has already happened, so the request ending is no reason to withhold it. Sends stop only when the
    /// application does.
    /// </para>
    /// <para>
    /// This wraps the in-memory manager, which is right for a single instance. With a backplane such as
    /// Redis, wrap the registered manager instead.
    /// </para>
    /// </remarks>
    public sealed class NonBlockingHubLifetimeManager<THub> : HubLifetimeManager<THub> where THub : Hub
    {
        private readonly HubLifetimeManager<THub> _inner;
        private readonly CancellationToken _stopping;
        private readonly ILogger _logger;

        public NonBlockingHubLifetimeManager(
            HubLifetimeManager<THub> inner,
            IHostApplicationLifetime lifetime,
            ILogger<NonBlockingHubLifetimeManager<THub>> logger)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _stopping = (lifetime ?? throw new ArgumentNullException(nameof(lifetime))).ApplicationStopping;
            _logger = logger;
        }

        // Membership and client results go straight to SignalR.

        public override Task OnConnectedAsync(HubConnectionContext connection) =>
            _inner.OnConnectedAsync(connection);

        public override Task OnDisconnectedAsync(HubConnectionContext connection) =>
            _inner.OnDisconnectedAsync(connection);

        public override Task AddToGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default) =>
            _inner.AddToGroupAsync(connectionId, groupName, cancellationToken);

        public override Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default) =>
            _inner.RemoveFromGroupAsync(connectionId, groupName, cancellationToken);

        public override Task<T> InvokeConnectionAsync<T>(string connectionId, string methodName, object[] args, CancellationToken cancellationToken) =>
            _inner.InvokeConnectionAsync<T>(connectionId, methodName, args, cancellationToken);

        public override Task SetConnectionResultAsync(string connectionId, CompletionMessage result) =>
            _inner.SetConnectionResultAsync(connectionId, result);

        public override bool TryGetReturnType(string invocationId, out Type type) =>
            _inner.TryGetReturnType(invocationId, out type);

        // Sends are started, not awaited.

        public override Task SendAllAsync(string methodName, object[] args, CancellationToken cancellationToken = default) =>
            Started(_inner.SendAllAsync(methodName, args, _stopping), methodName);

        public override Task SendAllExceptAsync(string methodName, object[] args, IReadOnlyList<string> excludedConnectionIds, CancellationToken cancellationToken = default) =>
            Started(_inner.SendAllExceptAsync(methodName, args, excludedConnectionIds, _stopping), methodName);

        public override Task SendConnectionAsync(string connectionId, string methodName, object[] args, CancellationToken cancellationToken = default) =>
            Started(_inner.SendConnectionAsync(connectionId, methodName, args, _stopping), methodName);

        public override Task SendConnectionsAsync(IReadOnlyList<string> connectionIds, string methodName, object[] args, CancellationToken cancellationToken = default) =>
            Started(_inner.SendConnectionsAsync(connectionIds, methodName, args, _stopping), methodName);

        public override Task SendGroupAsync(string groupName, string methodName, object[] args, CancellationToken cancellationToken = default) =>
            Started(_inner.SendGroupAsync(groupName, methodName, args, _stopping), methodName);

        public override Task SendGroupsAsync(IReadOnlyList<string> groupNames, string methodName, object[] args, CancellationToken cancellationToken = default) =>
            Started(_inner.SendGroupsAsync(groupNames, methodName, args, _stopping), methodName);

        public override Task SendGroupExceptAsync(string groupName, string methodName, object[] args, IReadOnlyList<string> excludedConnectionIds, CancellationToken cancellationToken = default) =>
            Started(_inner.SendGroupExceptAsync(groupName, methodName, args, excludedConnectionIds, _stopping), methodName);

        public override Task SendUserAsync(string userId, string methodName, object[] args, CancellationToken cancellationToken = default) =>
            Started(_inner.SendUserAsync(userId, methodName, args, _stopping), methodName);

        public override Task SendUsersAsync(IReadOnlyList<string> userIds, string methodName, object[] args, CancellationToken cancellationToken = default) =>
            Started(_inner.SendUsersAsync(userIds, methodName, args, _stopping), methodName);

        private Task Started(Task send, string methodName)
        {
            if (!send.IsCompletedSuccessfully)
            {
                _ = LogFailureAsync(send, methodName);
            }

            return Task.CompletedTask;
        }

        private async Task LogFailureAsync(Task send, string methodName)
        {
            try
            {
                await send;
            }
            catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
            {
                // Shutting down.
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "SignalR could not deliver {Method} to every client.", methodName);
            }
        }
    }
}
