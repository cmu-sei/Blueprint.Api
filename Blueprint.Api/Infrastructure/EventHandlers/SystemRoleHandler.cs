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
    public class SystemRoleCreatedSignalRHandler : INotificationHandler<EntityCreated<SystemRoleEntity>>
    {
        private readonly IHubBroadcaster _broadcaster;
        private readonly IMapper _mapper;

        public SystemRoleCreatedSignalRHandler(
            IHubBroadcaster broadcaster,
            IMapper mapper)
        {
            _broadcaster = broadcaster;
            _mapper = mapper;
        }

        public Task Handle(EntityCreated<SystemRoleEntity> notification, CancellationToken cancellationToken)
        {
            var systemRole = _mapper.Map<ViewModels.SystemRole>(notification.Entity);
            _broadcaster.Broadcast(new[] { MainHub.ROLE_GROUP }, MainHubMethods.SystemRoleCreated, systemRole);
            return Task.CompletedTask;
        }
    }

    public class SystemRoleUpdatedSignalRHandler : INotificationHandler<EntityUpdated<SystemRoleEntity>>
    {
        private readonly IHubBroadcaster _broadcaster;
        private readonly IMapper _mapper;

        public SystemRoleUpdatedSignalRHandler(
            IHubBroadcaster broadcaster,
            IMapper mapper)
        {
            _broadcaster = broadcaster;
            _mapper = mapper;
        }

        public Task Handle(EntityUpdated<SystemRoleEntity> notification, CancellationToken cancellationToken)
        {
            var systemRole = _mapper.Map<ViewModels.SystemRole>(notification.Entity);
            _broadcaster.Broadcast(new[] { MainHub.ROLE_GROUP }, MainHubMethods.SystemRoleUpdated, systemRole);
            return Task.CompletedTask;
        }
    }

    public class SystemRoleDeletedSignalRHandler : INotificationHandler<EntityDeleted<SystemRoleEntity>>
    {
        private readonly IHubBroadcaster _broadcaster;

        public SystemRoleDeletedSignalRHandler(
            IHubBroadcaster broadcaster)
        {
            _broadcaster = broadcaster;
        }

        public Task Handle(EntityDeleted<SystemRoleEntity> notification, CancellationToken cancellationToken)
        {
            _broadcaster.Broadcast(new[] { MainHub.ROLE_GROUP }, MainHubMethods.SystemRoleDeleted, notification.Entity.Id);
            return Task.CompletedTask;
        }
    }
}
