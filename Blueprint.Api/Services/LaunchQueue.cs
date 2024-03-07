// Copyright 2024 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Concurrent;
using System.Threading;

namespace Blueprint.Api.Services
{

    public interface ILaunchQueue
    {
        void Add(LaunchInformation launchInformation);

        LaunchInformation Take(CancellationToken cancellationToken);
    }

    public class LaunchQueue : ILaunchQueue
    {
        private BlockingCollection<LaunchInformation> _launchQueue = new BlockingCollection<LaunchInformation>();

        public void Add(LaunchInformation launchInformation)
        {
            _launchQueue.Add(launchInformation);
        }

        public LaunchInformation Take(CancellationToken cancellationToken)
        {
            return _launchQueue.Take(cancellationToken);
        }
    }

    public class LaunchInformation
    {
        public Guid MselId { get; set; }
        public Guid PlayerViewId { get; set; }
    }

}
