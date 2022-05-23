// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AutoMapper;
using AutoMapper.QueryableExtensions;
using MediatR;
using Microsoft.AspNetCore.SignalR;
using Blueprint.Api.Data;
using Blueprint.Api.Data.Models;
using Blueprint.Api.Services;
using Blueprint.Api.Hubs;

namespace Blueprint.Api.Infrastructure.EventHandlers
{
    public class BaseUserPermissionHandler
    {
        protected readonly BlueprintContext _db;
        protected readonly IMapper _mapper;
        protected readonly IHubContext<MainHub> _mainHub;

        public BaseUserPermissionHandler(
            BlueprintContext db,
            IMapper mapper,
            IUserService userService,
            IHubContext<MainHub> mainHub)
        {
            _db = db;
            _mapper = mapper;
            _mainHub = mainHub;
        }

        protected string[] GetGroups(UserPermissionEntity userPermissionEntity)
        {
            var groupIds = new List<string>();
            groupIds.Add(userPermissionEntity.UserId.ToString());
            // the admin data group gets everything
            groupIds.Add(MainHub.ADMIN_DATA_GROUP);

            return groupIds.ToArray();
        }

        protected async Task HandleChange(
            UserPermissionEntity userPermissionEntity,
            string method,
            string[] modifiedProperties,
            CancellationToken cancellationToken)
        {
            var groupIds = this.GetGroups(userPermissionEntity);
            var tasks = new List<Task>();
            var user = _db.Users
                .ProjectTo<ViewModels.User>(_mapper.ConfigurationProvider, dest => dest.Permissions)
                .First(u => u.Id == userPermissionEntity.UserId);

            foreach (var groupId in groupIds)
            {
                tasks.Add(_mainHub.Clients.Group(groupId).SendAsync(method, user, modifiedProperties, cancellationToken));
            }

            await Task.WhenAll(tasks);
        }
    }

    public class UserPermissionCreatedSignalRHandler : BaseUserPermissionHandler, INotificationHandler<EntityCreated<UserPermissionEntity>>
    {
        public UserPermissionCreatedSignalRHandler(
            BlueprintContext db,
            IMapper mapper,
            IUserService userPermissionService,
            IHubContext<MainHub> mainHub) : base(db, mapper, userPermissionService, mainHub) { }

        public async Task Handle(EntityCreated<UserPermissionEntity> notification, CancellationToken cancellationToken)
        {
            await base.HandleChange(notification.Entity, MainHubMethods.UserUpdated, null, cancellationToken);
        }
    }

    public class UserPermissionDeletedSignalRHandler : BaseUserPermissionHandler, INotificationHandler<EntityDeleted<UserPermissionEntity>>
    {
        public UserPermissionDeletedSignalRHandler(
            BlueprintContext db,
            IMapper mapper,
            IUserService userPermissionService,
            IHubContext<MainHub> mainHub) : base(db, mapper, userPermissionService, mainHub)
        {
        }

        public async Task Handle(EntityDeleted<UserPermissionEntity> notification, CancellationToken cancellationToken)
        {
            await base.HandleChange(notification.Entity, MainHubMethods.UserUpdated, null, cancellationToken);
        }
    }
}
