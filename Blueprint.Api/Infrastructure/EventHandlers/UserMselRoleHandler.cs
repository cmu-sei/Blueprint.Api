// Copyright 2023 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AutoMapper;
using MediatR;
using Microsoft.AspNetCore.SignalR;
using Blueprint.Api.Data;
using Blueprint.Api.Data.Models;
using Blueprint.Api.Services;
using Blueprint.Api.Hubs;
using Blueprint.Api.Infrastructure.Extensions;

namespace Blueprint.Api.Infrastructure.EventHandlers
{
    public class UserMselRoleHandler
    {
        protected readonly BlueprintContext _db;
        protected readonly IMapper _mapper;
        protected readonly IUserMselRoleService _userMselRoleService;
        protected readonly IHubContext<MainHub> _mainHub;

        public UserMselRoleHandler(
            BlueprintContext db,
            IMapper mapper,
            IUserMselRoleService userMselRoleService,
            IHubContext<MainHub> mainHub)
        {
            _db = db;
            _mapper = mapper;
            _userMselRoleService = userMselRoleService;
            _mainHub = mainHub;
        }

        protected string[] GetGroups(UserMselRoleEntity userMselRoleEntity)
        {
            var groupIds = new List<string>();
            groupIds.Add(userMselRoleEntity.MselId.ToString());
            // the admin data group gets everything
            groupIds.Add(MainHub.ADMIN_DATA_GROUP);

            return groupIds.ToArray();
        }

        protected async Task HandleCreateOrUpdate(
            UserMselRoleEntity userMselRoleEntity,
            string method,
            string[] modifiedProperties,
            CancellationToken cancellationToken)
        {
            var groupIds = GetGroups(userMselRoleEntity);
            var userMselRole = _mapper.Map<ViewModels.UserMselRole>(userMselRoleEntity);
            var tasks = new List<Task>();

            foreach (var groupId in groupIds)
            {
                tasks.Add(_mainHub.Clients.Group(groupId).SendAsync(method, userMselRole, modifiedProperties, cancellationToken));
            }

            await Task.WhenAll(tasks);
        }
    }

    public class UserMselRoleCreatedSignalRHandler : UserMselRoleHandler, INotificationHandler<EntityCreated<UserMselRoleEntity>>
    {
        public UserMselRoleCreatedSignalRHandler(
            BlueprintContext db,
            IMapper mapper,
            IUserMselRoleService userMselRoleService,
            IHubContext<MainHub> mainHub) : base(db, mapper, userMselRoleService, mainHub) { }

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
            IHubContext<MainHub> mainHub) : base(db, mapper, userMselRoleService, mainHub) { }

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
            IHubContext<MainHub> mainHub) : base(db, mapper, userMselRoleService, mainHub)
        {
        }

        public async Task Handle(EntityDeleted<UserMselRoleEntity> notification, CancellationToken cancellationToken)
        {
            var groupIds = base.GetGroups(notification.Entity);
            var tasks = new List<Task>();

            foreach (var groupId in groupIds)
            {
                tasks.Add(_mainHub.Clients.Group(groupId).SendAsync(MainHubMethods.UserMselRoleDeleted, notification.Entity.Id, cancellationToken));
            }

            await Task.WhenAll(tasks);
        }
    }
}
