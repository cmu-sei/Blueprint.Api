// Copyright 2024 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Concurrent;
using System.Threading;

namespace Blueprint.Api.Services
{

    public interface IIntegrationQueue
    {
        void Add(Guid mselId);

        Guid Take(CancellationToken cancellationToken);
    }

    public class IntegrationQueue : IIntegrationQueue
    {
        private BlockingCollection<Guid> _integrationQueue = new BlockingCollection<Guid>();

        public void Add(Guid mselId)
        {
            _integrationQueue.Add(mselId);
        }

        public Guid Take(CancellationToken cancellationToken)
        {
            return _integrationQueue.Take(cancellationToken);
        }
    }

}
