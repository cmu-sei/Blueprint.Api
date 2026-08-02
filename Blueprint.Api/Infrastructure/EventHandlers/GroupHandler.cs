// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using System.Threading;
using System.Threading.Tasks;
using AutoMapper;
using MediatR;
using Blueprint.Api.Data.Models;
using Blueprint.Api.Hubs;
using Blueprint.Api.Infrastructure.SignalR;
using Crucible.Common.EntityEvents.Events;

namespace Blueprint.Api.Infrastructure.EventHandlers
{
    public class GroupCreatedSignalRHandler : INotificationHandler<EntityCreated<GroupEntity>>
    {
        private readonly IHubBroadcaster _broadcaster;
        private readonly IMapper _mapper;

        public GroupCreatedSignalRHandler(
            IHubBroadcaster broadcaster,
            IMapper mapper)
        {
            _broadcaster = broadcaster;
            _mapper = mapper;
        }

        public Task Handle(EntityCreated<GroupEntity> notification, CancellationToken cancellationToken)
        {
            var group = _mapper.Map<ViewModels.Group>(notification.Entity);
            _broadcaster.Broadcast(new[] { MainHub.GROUP_GROUP }, MainHubMethods.GroupCreated, group);
            return Task.CompletedTask;
        }
    }

    public class GroupUpdatedSignalRHandler : INotificationHandler<EntityUpdated<GroupEntity>>
    {
        private readonly IHubBroadcaster _broadcaster;
        private readonly IMapper _mapper;

        public GroupUpdatedSignalRHandler(
            IHubBroadcaster broadcaster,
            IMapper mapper)
        {
            _broadcaster = broadcaster;
            _mapper = mapper;
        }

        public Task Handle(EntityUpdated<GroupEntity> notification, CancellationToken cancellationToken)
        {
            var group = _mapper.Map<ViewModels.Group>(notification.Entity);
            _broadcaster.Broadcast(new[] { MainHub.GROUP_GROUP }, MainHubMethods.GroupUpdated, group);
            return Task.CompletedTask;
        }
    }

    public class GroupDeletedSignalRHandler : INotificationHandler<EntityDeleted<GroupEntity>>
    {
        private readonly IHubBroadcaster _broadcaster;

        public GroupDeletedSignalRHandler(
            IHubBroadcaster broadcaster)
        {
            _broadcaster = broadcaster;
        }

        public Task Handle(EntityDeleted<GroupEntity> notification, CancellationToken cancellationToken)
        {
            _broadcaster.Broadcast(new[] { MainHub.GROUP_GROUP }, MainHubMethods.GroupDeleted, notification.Entity.Id);
            return Task.CompletedTask;
        }
    }
}
