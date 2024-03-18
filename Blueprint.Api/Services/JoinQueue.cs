// Copyright 2024 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Concurrent;
using System.Threading;

namespace Blueprint.Api.Services
{

    public interface IJoinQueue
    {
        void Add(JoinInformation joinInformation);

        JoinInformation Take(CancellationToken cancellationToken);
    }

    public class JoinQueue : IJoinQueue
    {
        private BlockingCollection<JoinInformation> _joinQueue = new BlockingCollection<JoinInformation>();

        public void Add(JoinInformation joinInformation)
        {
            _joinQueue.Add(joinInformation);
        }

        public JoinInformation Take(CancellationToken cancellationToken)
        {
            return _joinQueue.Take(cancellationToken);
        }
    }

    public class JoinInformation
    {
        public Guid UserId { get; set; }
        public Guid PlayerViewId { get; set; }
        public Guid PlayerTeamId { get; set; }
    }

}
