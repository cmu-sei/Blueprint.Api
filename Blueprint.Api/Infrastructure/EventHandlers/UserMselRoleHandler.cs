// Copyright 2023 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using System;
using System.Data;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AutoMapper;
using MediatR;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
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
        protected readonly IMselService _mselService;
        protected readonly IHubContext<MainHub> _mainHub;

        public UserMselRoleHandler(
            BlueprintContext db,
            IMapper mapper,
            IMselService mselService,
            IHubContext<MainHub> mainHub)
        {
            _db = db;
            _mapper = mapper;
            _mselService = mselService;
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

        protected async Task HandleCreateOrDelete(
            UserMselRoleEntity userMselRoleEntity,
            string method,
            string[] modifiedProperties,
            CancellationToken cancellationToken)
        {
            var groupIds = GetGroups(userMselRoleEntity);
            var tasks = new List<Task>();
            var userMselRole = _mapper.Map<ViewModels.UserMselRole>(userMselRoleEntity);

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
            IMselService mselService,
            IHubContext<MainHub> mainHub) : base(db, mapper, mselService, mainHub) { }

        public async Task Handle(EntityCreated<UserMselRoleEntity> notification, CancellationToken cancellationToken)
        {
            await base.HandleCreateOrDelete(notification.Entity, MainHubMethods.UserMselRoleCreated, null, cancellationToken);
        }
    }

    public class UserMselRoleDeletedSignalRHandler : UserMselRoleHandler, INotificationHandler<EntityDeleted<UserMselRoleEntity>>
    {
        public UserMselRoleDeletedSignalRHandler(
            BlueprintContext db,
            IMapper mapper,
            IMselService mselService,
            IHubContext<MainHub> mainHub) : base(db, mapper, mselService, mainHub)
        {
        }

        public async Task Handle(EntityDeleted<UserMselRoleEntity> notification, CancellationToken cancellationToken)
        {
            await base.HandleCreateOrDelete(notification.Entity, MainHubMethods.UserMselRoleDeleted, null, cancellationToken);
        }
    }
}
