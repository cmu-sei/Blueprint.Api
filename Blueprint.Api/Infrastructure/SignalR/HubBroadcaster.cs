// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Blueprint.Api.Hubs;
using Blueprint.Api.Infrastructure.Options;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Blueprint.Api.Infrastructure.SignalR
{
    /// <summary>
    /// Queues SignalR broadcasts so that they are never awaited on the HTTP request path.
    /// </summary>
    /// <remarks>
    /// The entity event handlers used to build a list of <c>SendAsync</c> tasks and
    /// <c>await Task.WhenAll(tasks)</c>. Because those handlers run inside
    /// <c>BlueprintContext.PublishEventsAsync</c>, which is called from
    /// <c>SaveChangesAsync</c>, that await sat directly on the request path: an HTTP write
    /// could not return until every subscribed client had accepted the broadcast.
    ///
    /// A client that has stopped reading its socket (a frozen, suspended or
    /// network-partitioned browser tab) does not fail fast. Its transport buffer fills, TCP
    /// back-pressure stops the send from completing, and the write blocks behind it.
    /// Measured on this codebase before the fix: with silent subscribers attached, writes
    /// that normally take 9ms stalled for ~11s each while reads continued to answer in 3ms.
    ///
    /// Notifying clients is not part of the caller's unit of work, so it should not share
    /// the caller's lifetime. Broadcasts are handed to this queue and drained by a
    /// background worker, so a stuck client can only ever delay other broadcasts -- bounded
    /// by <see cref="SignalROptions.BroadcastSendTimeoutSeconds"/> -- and never a write.
    /// </remarks>
    public interface IHubBroadcaster
    {
        /// <summary>
        /// Queues <paramref name="method"/> for delivery to every group in
        /// <paramref name="groupIds"/>. Returns immediately; never throws for delivery
        /// failure, which is logged by the background worker instead.
        /// </summary>
        void Broadcast(IEnumerable<string> groupIds, string method, params object[] args);
    }

    public sealed class HubBroadcaster : BackgroundService, IHubBroadcaster
    {
        private readonly Channel<QueuedBroadcast> _queue;
        private readonly IHubContext<MainHub> _hub;
        private readonly ILogger<HubBroadcaster> _logger;
        private readonly SignalROptions _options;
        private readonly SemaphoreSlim _inFlight;
        private long _dropped;

        public HubBroadcaster(
            IHubContext<MainHub> hub,
            SignalROptions options,
            ILogger<HubBroadcaster> logger)
        {
            _hub = hub;
            _options = options;
            _logger = logger;
            _inFlight = new SemaphoreSlim(Math.Max(1, options.BroadcastMaxConcurrency));

            // Bounded on purpose. If clients cannot keep up, memory must not grow without
            // limit; dropping the newest broadcast and logging it is the lesser failure. A
            // dropped notification is self-healing because the UI re-reads on navigation.
            _queue = Channel.CreateBounded<QueuedBroadcast>(new BoundedChannelOptions(Math.Max(1, options.BroadcastQueueCapacity))
            {
                FullMode = BoundedChannelFullMode.DropWrite,
                SingleReader = true,
                SingleWriter = false,
            });
        }

        public void Broadcast(IEnumerable<string> groupIds, string method, params object[] args)
        {
            if (groupIds is null) return;

            // Materialise now: the caller's group list may be backed by a scoped DbContext
            // query, and the background worker runs after that scope is gone.
            var groups = groupIds.Where(g => !string.IsNullOrEmpty(g)).Distinct().ToArray();
            if (groups.Length == 0) return;

            if (!_queue.Writer.TryWrite(new QueuedBroadcast(groups, method, args ?? Array.Empty<object>())))
            {
                var dropped = Interlocked.Increment(ref _dropped);
                // Log the first drop and then every 100th, so a sustained overload does not
                // itself become the bottleneck.
                if (dropped == 1 || dropped % 100 == 0)
                {
                    _logger.LogWarning(
                        "SignalR broadcast queue is full (capacity {Capacity}); dropped {Method}. {Dropped} dropped so far.",
                        _options.BroadcastQueueCapacity, method, dropped);
                }
            }
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try
            {
                while (await _queue.Reader.WaitToReadAsync(stoppingToken))
                {
                    while (_queue.Reader.TryRead(out var broadcast))
                    {
                        // Bound how many fan-outs are in flight, then dispatch without
                        // awaiting so one unresponsive client cannot stall the queue.
                        await _inFlight.WaitAsync(stoppingToken);
                        _ = DispatchAsync(broadcast, stoppingToken);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Normal shutdown.
            }
        }

        private async Task DispatchAsync(QueuedBroadcast broadcast, CancellationToken stoppingToken)
        {
            try
            {
                // Every send is bounded. Without a timeout a non-draining client would park
                // this task forever, which is the defect this class exists to prevent --
                // just moved off the request path rather than removed.
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, _options.BroadcastSendTimeoutSeconds)));

                await Task.WhenAll(broadcast.Groups.Select(async groupId =>
                {
                    try
                    {
                        await _hub.Clients.Group(groupId).SendCoreAsync(broadcast.Method, broadcast.Args, timeout.Token);
                    }
                    catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
                    {
                        _logger.LogWarning(
                            "SignalR broadcast {Method} to group {GroupId} timed out after {Timeout}s; a client is not reading.",
                            broadcast.Method, groupId, _options.BroadcastSendTimeoutSeconds);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "SignalR broadcast {Method} to group {GroupId} failed.", broadcast.Method, groupId);
                    }
                }));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected failure dispatching SignalR broadcast {Method}.", broadcast.Method);
            }
            finally
            {
                _inFlight.Release();
            }
        }

        private readonly record struct QueuedBroadcast(string[] Groups, string Method, object[] Args);
    }
}
