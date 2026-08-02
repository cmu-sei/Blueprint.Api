// Copyright 2024 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

namespace Blueprint.Api.Infrastructure.Options
{
    public class SignalROptions
    {
        public bool EnableStatefulReconnect { get; set; } = true;
        public long StatefulReconnectBufferSizeBytes { get; set; } = 100000;

        /// <summary>
        /// How long a single queued broadcast may spend sending to one group before it is
        /// abandoned. This is what stops a client that has stopped reading its socket from
        /// parking a broadcast worker indefinitely.
        /// </summary>
        public int BroadcastSendTimeoutSeconds { get; set; } = 10;

        /// <summary>
        /// Maximum number of queued broadcasts. Bounded so that clients which cannot keep up
        /// cause dropped notifications (logged, and self-healing because the UI re-reads on
        /// navigation) rather than unbounded memory growth.
        /// </summary>
        public int BroadcastQueueCapacity { get; set; } = 10000;

        /// <summary>
        /// Maximum number of broadcast fan-outs dispatched concurrently.
        /// </summary>
        public int BroadcastMaxConcurrency { get; set; } = 64;
    }
}