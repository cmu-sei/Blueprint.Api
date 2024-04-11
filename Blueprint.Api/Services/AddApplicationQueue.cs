// Copyright 2024 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Concurrent;
using System.Threading;
using Player.Api.Client;

namespace Blueprint.Api.Services
{

    public interface IAddApplicationQueue
    {
        void Add(AddApplicationInformation addApplicationInformation);

        AddApplicationInformation Take(CancellationToken cancellationToken);
    }

    public class AddApplicationQueue : IAddApplicationQueue
    {
        private BlockingCollection<AddApplicationInformation> _addApplicationQueue = new BlockingCollection<AddApplicationInformation>();

        public void Add(AddApplicationInformation addApplicationInformation)
        {
            _addApplicationQueue.Add(addApplicationInformation);
        }

        public AddApplicationInformation Take(CancellationToken cancellationToken)
        {
            return _addApplicationQueue.Take(cancellationToken);
        }
    }

    public class AddApplicationInformation
    {
        public Application Application { get; set; }
        public Guid PlayerTeamId { get; set; }
        public int DisplayOrder { get; set; }

    }

}
