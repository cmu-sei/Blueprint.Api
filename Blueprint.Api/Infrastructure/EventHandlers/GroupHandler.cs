// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using System.Threading;
using System.Threading.Tasks;
using MediatR;
using Microsoft.AspNetCore.SignalR;
using Blueprint.Api.Data.Models;
using Blueprint.Api.Hubs;
using Crucible.Common.EntityEvents.Events;
using Blueprint.Api.Infrastructure.Mappings;

namespace Blueprint.Api.Infrastructure.EventHandlers
{
    public class GroupCreatedSignalRHandler : INotificationHandler<EntityCreated<GroupEntity>>
    {
        private readonly IHubContext<MainHub> _mainHub;

        public GroupCreatedSignalRHandler(
            IHubContext<MainHub> mainHub)
        {
            _mainHub = mainHub;
        }

        public async Task Handle(EntityCreated<GroupEntity> notification, CancellationToken cancellationToken)
        {
            var group = notification.Entity.ToGroup();
            await _mainHub.Clients
                .Groups(MainHub.GROUP_GROUP)
                .SendAsync(MainHubMethods.GroupCreated, group, cancellationToken);
        }
    }

    public class GroupUpdatedSignalRHandler : INotificationHandler<EntityUpdated<GroupEntity>>
    {
        private readonly IHubContext<MainHub> _mainHub;

        public GroupUpdatedSignalRHandler(
            IHubContext<MainHub> mainHub)
        {
            _mainHub = mainHub;
        }

        public async Task Handle(EntityUpdated<GroupEntity> notification, CancellationToken cancellationToken)
        {
            var group = notification.Entity.ToGroup();
            await _mainHub.Clients
                .Groups(MainHub.GROUP_GROUP)
                .SendAsync(MainHubMethods.GroupUpdated, group, cancellationToken);
        }
    }

    public class GroupDeletedSignalRHandler : INotificationHandler<EntityDeleted<GroupEntity>>
    {
        private readonly IHubContext<MainHub> _mainHub;

        public GroupDeletedSignalRHandler(
            IHubContext<MainHub> mainHub)
        {
            _mainHub = mainHub;
        }

        public async Task Handle(EntityDeleted<GroupEntity> notification, CancellationToken cancellationToken)
        {
            await _mainHub.Clients
                .Groups(MainHub.GROUP_GROUP)
                .SendAsync(MainHubMethods.GroupDeleted, notification.Entity.Id, cancellationToken);
        }
    }
}
