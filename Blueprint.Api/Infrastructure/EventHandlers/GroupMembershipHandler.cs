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
    public class GroupMembershipCreatedSignalRHandler : INotificationHandler<EntityCreated<GroupMembershipEntity>>
    {
        private readonly IHubContext<MainHub> _mainHub;

        public GroupMembershipCreatedSignalRHandler(
            IHubContext<MainHub> mainHub)
        {
            _mainHub = mainHub;
        }

        public async Task Handle(EntityCreated<GroupMembershipEntity> notification, CancellationToken cancellationToken)
        {
            var groupMembership = notification.Entity.ToGroupMembership();
            await _mainHub.Clients
                .Groups(MainHub.GROUP_GROUP)
                .SendAsync(MainHubMethods.GroupMembershipCreated, groupMembership, cancellationToken);
        }
    }

    public class GroupMembershipUpdatedSignalRHandler : INotificationHandler<EntityUpdated<GroupMembershipEntity>>
    {
        private readonly IHubContext<MainHub> _mainHub;

        public GroupMembershipUpdatedSignalRHandler(
            IHubContext<MainHub> mainHub)
        {
            _mainHub = mainHub;
        }

        public async Task Handle(EntityUpdated<GroupMembershipEntity> notification, CancellationToken cancellationToken)
        {
            var groupMembership = notification.Entity.ToGroupMembership();
            await _mainHub.Clients
                .Groups(MainHub.GROUP_GROUP)
                .SendAsync(MainHubMethods.GroupMembershipUpdated, groupMembership, cancellationToken);
        }
    }

    public class GroupMembershipDeletedSignalRHandler : INotificationHandler<EntityDeleted<GroupMembershipEntity>>
    {
        private readonly IHubContext<MainHub> _mainHub;

        public GroupMembershipDeletedSignalRHandler(
            IHubContext<MainHub> mainHub)
        {
            _mainHub = mainHub;
        }

        public async Task Handle(EntityDeleted<GroupMembershipEntity> notification, CancellationToken cancellationToken)
        {
            await _mainHub.Clients
                .Groups(MainHub.GROUP_GROUP)
                .SendAsync(MainHubMethods.GroupMembershipDeleted, notification.Entity.Id, cancellationToken);
        }
    }
}
