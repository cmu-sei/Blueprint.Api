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
    public class GroupMembershipCreatedSignalRHandler : INotificationHandler<EntityCreated<GroupMembershipEntity>>
    {
        private readonly IHubBroadcaster _broadcaster;
        private readonly IMapper _mapper;

        public GroupMembershipCreatedSignalRHandler(
            IHubBroadcaster broadcaster,
            IMapper mapper)
        {
            _broadcaster = broadcaster;
            _mapper = mapper;
        }

        public Task Handle(EntityCreated<GroupMembershipEntity> notification, CancellationToken cancellationToken)
        {
            var groupMembership = _mapper.Map<ViewModels.GroupMembership>(notification.Entity);
            _broadcaster.Broadcast(new[] { MainHub.GROUP_GROUP }, MainHubMethods.GroupMembershipCreated, groupMembership);
            return Task.CompletedTask;
        }
    }

    public class GroupMembershipUpdatedSignalRHandler : INotificationHandler<EntityUpdated<GroupMembershipEntity>>
    {
        private readonly IHubBroadcaster _broadcaster;
        private readonly IMapper _mapper;

        public GroupMembershipUpdatedSignalRHandler(
            IHubBroadcaster broadcaster,
            IMapper mapper)
        {
            _broadcaster = broadcaster;
            _mapper = mapper;
        }

        public Task Handle(EntityUpdated<GroupMembershipEntity> notification, CancellationToken cancellationToken)
        {
            var groupMembership = _mapper.Map<ViewModels.GroupMembership>(notification.Entity);
            _broadcaster.Broadcast(new[] { MainHub.GROUP_GROUP }, MainHubMethods.GroupMembershipUpdated, groupMembership);
            return Task.CompletedTask;
        }
    }

    public class GroupMembershipDeletedSignalRHandler : INotificationHandler<EntityDeleted<GroupMembershipEntity>>
    {
        private readonly IHubBroadcaster _broadcaster;

        public GroupMembershipDeletedSignalRHandler(
            IHubBroadcaster broadcaster)
        {
            _broadcaster = broadcaster;
        }

        public Task Handle(EntityDeleted<GroupMembershipEntity> notification, CancellationToken cancellationToken)
        {
            _broadcaster.Broadcast(new[] { MainHub.GROUP_GROUP }, MainHubMethods.GroupMembershipDeleted, notification.Entity.Id);
            return Task.CompletedTask;
        }
    }
}
