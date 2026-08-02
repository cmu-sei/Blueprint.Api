// Copyright 2023 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AutoMapper;
using MediatR;
using Blueprint.Api.Data;
using Blueprint.Api.Data.Models;
using Blueprint.Api.Services;
using Blueprint.Api.Hubs;
using Blueprint.Api.Infrastructure.SignalR;
using Blueprint.Api.Infrastructure.Extensions;
using Crucible.Common.EntityEvents.Events;

namespace Blueprint.Api.Infrastructure.EventHandlers
{
    public class UserMselRoleHandler
    {
        protected readonly BlueprintContext _db;
        protected readonly IMapper _mapper;
        protected readonly IUserMselRoleService _userMselRoleService;
        protected readonly IHubBroadcaster _broadcaster;

        public UserMselRoleHandler(
            BlueprintContext db,
            IMapper mapper,
            IUserMselRoleService userMselRoleService,
            IHubBroadcaster broadcaster)
        {
            _db = db;
            _mapper = mapper;
            _userMselRoleService = userMselRoleService;
            _broadcaster = broadcaster;
        }

        protected string[] GetGroups(UserMselRoleEntity userMselRoleEntity)
        {
            var groupIds = new List<string>();
            groupIds.Add(userMselRoleEntity.MselId.ToString());
            // the admin data group gets everything
            groupIds.Add(MainHub.ADMIN_DATA_GROUP);

            return groupIds.ToArray();
        }

        protected Task HandleCreateOrUpdate(
            UserMselRoleEntity userMselRoleEntity,
            string method,
            string[] modifiedProperties,
            CancellationToken cancellationToken)
        {
            var groupIds = GetGroups(userMselRoleEntity);
            var userMselRole = _mapper.Map<ViewModels.UserMselRole>(userMselRoleEntity);
            _broadcaster.Broadcast(groupIds, method, userMselRole, modifiedProperties);
            return Task.CompletedTask;
        }
    }

    public class UserMselRoleCreatedSignalRHandler : UserMselRoleHandler, INotificationHandler<EntityCreated<UserMselRoleEntity>>
    {
        public UserMselRoleCreatedSignalRHandler(
            BlueprintContext db,
            IMapper mapper,
            IUserMselRoleService userMselRoleService,
            IHubBroadcaster broadcaster) : base(db, mapper, userMselRoleService, broadcaster) { }

        public async Task Handle(EntityCreated<UserMselRoleEntity> notification, CancellationToken cancellationToken)
        {
            await base.HandleCreateOrUpdate(notification.Entity, MainHubMethods.UserMselRoleCreated, null, cancellationToken);
        }
    }

    public class UserMselRoleUpdatedSignalRHandler : UserMselRoleHandler, INotificationHandler<EntityUpdated<UserMselRoleEntity>>
    {
        public UserMselRoleUpdatedSignalRHandler(
            BlueprintContext db,
            IMapper mapper,
            IUserMselRoleService userMselRoleService,
            IHubBroadcaster broadcaster) : base(db, mapper, userMselRoleService, broadcaster) { }

        public async Task Handle(EntityUpdated<UserMselRoleEntity> notification, CancellationToken cancellationToken)
        {
            await base.HandleCreateOrUpdate(
                notification.Entity,
                MainHubMethods.UserMselRoleUpdated,
                notification.ModifiedProperties.Select(x => x.TitleCaseToCamelCase()).ToArray(),
                cancellationToken);
        }
    }

    public class UserMselRoleDeletedSignalRHandler : UserMselRoleHandler, INotificationHandler<EntityDeleted<UserMselRoleEntity>>
    {
        public UserMselRoleDeletedSignalRHandler(
            BlueprintContext db,
            IMapper mapper,
            IUserMselRoleService userMselRoleService,
            IHubBroadcaster broadcaster) : base(db, mapper, userMselRoleService, broadcaster)
        {
        }

        public Task Handle(EntityDeleted<UserMselRoleEntity> notification, CancellationToken cancellationToken)
        {
            var groupIds = base.GetGroups(notification.Entity);
            _broadcaster.Broadcast(groupIds, MainHubMethods.UserMselRoleDeleted, notification.Entity.Id);
            return Task.CompletedTask;
        }
    }
}
