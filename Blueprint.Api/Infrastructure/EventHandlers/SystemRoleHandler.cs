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
    public class SystemRoleCreatedSignalRHandler : INotificationHandler<EntityCreated<SystemRoleEntity>>
    {
        private readonly IHubContext<MainHub> _mainHub;

        public SystemRoleCreatedSignalRHandler(
            IHubContext<MainHub> mainHub)
        {
            _mainHub = mainHub;
        }

        public async Task Handle(EntityCreated<SystemRoleEntity> notification, CancellationToken cancellationToken)
        {
            var systemRole = notification.Entity.ToSystemRole();
            await _mainHub.Clients
                .Groups(MainHub.ROLE_GROUP)
                .SendAsync(MainHubMethods.SystemRoleCreated, systemRole, cancellationToken);
        }
    }

    public class SystemRoleUpdatedSignalRHandler : INotificationHandler<EntityUpdated<SystemRoleEntity>>
    {
        private readonly IHubContext<MainHub> _mainHub;

        public SystemRoleUpdatedSignalRHandler(
            IHubContext<MainHub> mainHub)
        {
            _mainHub = mainHub;
        }

        public async Task Handle(EntityUpdated<SystemRoleEntity> notification, CancellationToken cancellationToken)
        {
            var systemRole = notification.Entity.ToSystemRole();
            await _mainHub.Clients
                .Groups(MainHub.ROLE_GROUP)
                .SendAsync(MainHubMethods.SystemRoleUpdated, systemRole, cancellationToken);
        }
    }

    public class SystemRoleDeletedSignalRHandler : INotificationHandler<EntityDeleted<SystemRoleEntity>>
    {
        private readonly IHubContext<MainHub> _mainHub;

        public SystemRoleDeletedSignalRHandler(
            IHubContext<MainHub> mainHub)
        {
            _mainHub = mainHub;
        }

        public async Task Handle(EntityDeleted<SystemRoleEntity> notification, CancellationToken cancellationToken)
        {
            await _mainHub.Clients
                .Groups(MainHub.ROLE_GROUP)
                .SendAsync(MainHubMethods.SystemRoleDeleted, notification.Entity.Id, cancellationToken);
        }
    }
}
