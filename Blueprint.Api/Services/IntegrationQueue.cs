// Copyright 2024 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Concurrent;
using System.Threading;

namespace Blueprint.Api.Services
{

    public interface IIntegrationQueue
    {
        void Add(IntegrationInformation integrationInformation);

        IntegrationInformation Take(CancellationToken cancellationToken);
    }

    public class IntegrationQueue : IIntegrationQueue
    {
        private BlockingCollection<IntegrationInformation> _integrationQueue = new BlockingCollection<IntegrationInformation>();

        public void Add(IntegrationInformation integrationInformation)
        {
            _integrationQueue.Add(integrationInformation);
        }

        public IntegrationInformation Take(CancellationToken cancellationToken)
        {
            return _integrationQueue.Take(cancellationToken);
        }
    }

}
