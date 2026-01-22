// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using System.Threading;
using System.Threading.Tasks;
using AutoMapper;
using MediatR;
using Microsoft.AspNetCore.SignalR;
using Blueprint.Api.Data.Models;
using Blueprint.Api.Hubs;

namespace Blueprint.Api.Infrastructure.EventHandlers
{
    public class GroupMembershipCreatedSignalRHandler : INotificationHandler<EntityCreated<GroupMembershipEntity>>
    {
        private readonly IHubContext<MainHub> _mainHub;
        private readonly IMapper _mapper;

        public GroupMembershipCreatedSignalRHandler(
            IHubContext<MainHub> mainHub,
            IMapper mapper)
        {
            _mainHub = mainHub;
            _mapper = mapper;
        }

        public async Task Handle(EntityCreated<GroupMembershipEntity> notification, CancellationToken cancellationToken)
        {
            var groupMembership = _mapper.Map<ViewModels.GroupMembership>(notification.Entity);
            await _mainHub.Clients
                .Groups(MainHub.GROUP_GROUP)
                .SendAsync(MainHubMethods.GroupMembershipCreated, groupMembership, cancellationToken);
        }
    }

    public class GroupMembershipUpdatedSignalRHandler : INotificationHandler<EntityUpdated<GroupMembershipEntity>>
    {
        private readonly IHubContext<MainHub> _mainHub;
        private readonly IMapper _mapper;

        public GroupMembershipUpdatedSignalRHandler(
            IHubContext<MainHub> mainHub,
            IMapper mapper)
        {
            _mainHub = mainHub;
            _mapper = mapper;
        }

        public async Task Handle(EntityUpdated<GroupMembershipEntity> notification, CancellationToken cancellationToken)
        {
            var groupMembership = _mapper.Map<ViewModels.GroupMembership>(notification.Entity);
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
