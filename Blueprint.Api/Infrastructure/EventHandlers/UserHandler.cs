// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AutoMapper;
using MediatR;
using Microsoft.AspNetCore.SignalR;
using Blueprint.Api.Data.Models;
using Blueprint.Api.Hubs;
using Blueprint.Api.Infrastructure.Extensions;
using Blueprint.Api.Infrastructure.SignalR;
using Crucible.Common.EntityEvents.Events;

namespace Blueprint.Api.Infrastructure.EventHandlers
{
    public class UserHandler
    {
        protected readonly IMapper _mapper;
        protected readonly IHubBroadcaster _broadcaster;

        public UserHandler(
            IMapper mapper,
            IHubBroadcaster broadcaster)
        {
            _mapper = mapper;
            _broadcaster = broadcaster;
        }

        protected string[] GetGroups(UserEntity userEntity)
        {
            var groupIds = new List<string>();
            groupIds.Add(userEntity.CreatedBy.ToString());
            // the admin data group gets everything
            groupIds.Add(MainHub.ADMIN_DATA_GROUP);

            return groupIds.ToArray();
        }

        protected void HandleCreateOrUpdate(
            UserEntity userEntity,
            string method,
            string[] modifiedProperties)
        {
            var groupIds = this.GetGroups(userEntity);
            var user = _mapper.Map<ViewModels.User>(userEntity);

            _broadcaster.Broadcast(groupIds, method, user, modifiedProperties);
        }
    }

    public class UserCreatedSignalRHandler : UserHandler, INotificationHandler<EntityCreated<UserEntity>>
    {
        public UserCreatedSignalRHandler(
            IMapper mapper,
            IHubBroadcaster broadcaster) : base(mapper, broadcaster) { }

        public Task Handle(EntityCreated<UserEntity> notification, CancellationToken cancellationToken)
        {
            base.HandleCreateOrUpdate(notification.Entity, MainHubMethods.UserCreated, null);
            return Task.CompletedTask;
        }
    }

    public class UserUpdatedSignalRHandler : UserHandler, INotificationHandler<EntityUpdated<UserEntity>>
    {
        public UserUpdatedSignalRHandler(
            IMapper mapper,
            IHubBroadcaster broadcaster) : base(mapper, broadcaster) { }

        public Task Handle(EntityUpdated<UserEntity> notification, CancellationToken cancellationToken)
        {
            base.HandleCreateOrUpdate(
                notification.Entity,
                MainHubMethods.UserUpdated,
                notification.ModifiedProperties.Select(x => x.TitleCaseToCamelCase()).ToArray());
            return Task.CompletedTask;
        }
    }

    public class UserDeletedSignalRHandler : UserHandler, INotificationHandler<EntityDeleted<UserEntity>>
    {
        public UserDeletedSignalRHandler(
            IMapper mapper,
            IHubBroadcaster broadcaster) : base(mapper, broadcaster)
        {
        }

        public Task Handle(EntityDeleted<UserEntity> notification, CancellationToken cancellationToken)
        {
            _broadcaster.Broadcast(base.GetGroups(notification.Entity), MainHubMethods.UserDeleted, notification.Entity.Id);
            return Task.CompletedTask;
        }
    }
}
