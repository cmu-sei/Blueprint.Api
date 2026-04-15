// Copyright 2024 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using System.Collections.Concurrent;

namespace Blueprint.Api.Hubs
{
    public class HubCache
    {
        public ConcurrentDictionary<string, CachedConnection> Connections { get; } = new();
    }

    public class CachedConnection
    {
        public string ConnectionId { get; set; }
        public string MselId { get; set; }
        public string UserId { get; set; }
        public string UserName { get; set; }
    }
}
