// Copyright 2024 Carnegie Mellon University. All Rights Reserved.
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
using Microsoft.EntityFrameworkCore;
using Crucible.Common.EntityEvents.Events;

namespace Blueprint.Api.Infrastructure.EventHandlers
{
    public class UnitUserHandler
    {
        protected readonly BlueprintContext _db;
        protected readonly IMapper _mapper;
        protected readonly IUnitUserService _unitUserService;
        protected readonly IHubContext<MainHub> _mainHub;

        public UnitUserHandler(
            BlueprintContext db,
            IMapper mapper,
            IUnitUserService unitUserService,
            IHubContext<MainHub> mainHub)
        {
            _db = db;
            _mapper = mapper;
            _unitUserService = unitUserService;
            _mainHub = mainHub;
        }

        protected async Task<string[]> GetGroupsAsync(UnitUserEntity unitUserEntity, CancellationToken cancellationToken)
        {
            var groupIds = new List<string>();
            // add the unit
            groupIds.Add(unitUserEntity.UnitId.ToString());
            // add the user
            groupIds.Add(unitUserEntity.UserId.ToString());
            // the admin data group gets everything
            groupIds.Add(MainHub.ADMIN_DATA_GROUP);
            // also notify all MSELs that have this unit as a contributor
            var mselIds = await _db.MselUnits
                .Where(mu => mu.UnitId == unitUserEntity.UnitId)
                .Select(mu => mu.MselId.ToString())
                .ToListAsync(cancellationToken);
            groupIds.AddRange(mselIds);

            return groupIds.ToArray();
        }

        protected async Task HandleCreateOrUpdate(
            UnitUserEntity unitUserEntity,
            string method,
            string[] modifiedProperties,
            CancellationToken cancellationToken)
        {
            var groupIds = await GetGroupsAsync(unitUserEntity, cancellationToken);
            // the view model of a row read on its own, not the tracked entity: the entity's navigations reach whatever the
            // request has loaded, which can serialize past SignalR's 2 GB message limit; the UI reads the unit user's user
            var row = await _db.UnitUsers
                .AsNoTracking()
                .Include(uu => uu.User)
                .SingleOrDefaultAsync(uu => uu.Id == unitUserEntity.Id, cancellationToken);
            // a row gone by now, removed by a later request, has nothing to tell
            if (row == null)
                return;
            var unitUser = _mapper.Map<ViewModels.UnitUser>(row);
            var tasks = new List<Task>();

            foreach (var groupId in groupIds)
            {
                tasks.Add(_mainHub.Clients.Group(groupId).SendAsync(method, unitUser, modifiedProperties, cancellationToken));
            }

            await Task.WhenAll(tasks);
        }
    }

    public class UnitUserCreatedSignalRHandler : UnitUserHandler, INotificationHandler<EntityCreated<UnitUserEntity>>
    {
        public UnitUserCreatedSignalRHandler(
            BlueprintContext db,
            IMapper mapper,
            IUnitUserService unitUserService,
            IHubContext<MainHub> mainHub) : base(db, mapper, unitUserService, mainHub) { }

        public async Task Handle(EntityCreated<UnitUserEntity> notification, CancellationToken cancellationToken)
        {
            await base.HandleCreateOrUpdate(notification.Entity, MainHubMethods.UnitUserCreated, null, cancellationToken);
        }
    }

    public class UnitUserUpdatedSignalRHandler : UnitUserHandler, INotificationHandler<EntityUpdated<UnitUserEntity>>
    {
        public UnitUserUpdatedSignalRHandler(
            BlueprintContext db,
            IMapper mapper,
            IUnitUserService unitUserService,
            IHubContext<MainHub> mainHub) : base(db, mapper, unitUserService, mainHub) { }

        public async Task Handle(EntityUpdated<UnitUserEntity> notification, CancellationToken cancellationToken)
        {
            await base.HandleCreateOrUpdate(
                notification.Entity,
                MainHubMethods.UnitUserUpdated,
                notification.ModifiedProperties.Select(x => x.TitleCaseToCamelCase()).ToArray(),
                cancellationToken);
        }
    }

    public class UnitUserDeletedSignalRHandler : UnitUserHandler, INotificationHandler<EntityDeleted<UnitUserEntity>>
    {
        public UnitUserDeletedSignalRHandler(
            BlueprintContext db,
            IMapper mapper,
            IUnitUserService unitUserService,
            IHubContext<MainHub> mainHub) : base(db, mapper, unitUserService, mainHub)
        {
        }

        public async Task Handle(EntityDeleted<UnitUserEntity> notification, CancellationToken cancellationToken)
        {
            var groupIds = await base.GetGroupsAsync(notification.Entity, CancellationToken.None);
            var tasks = new List<Task>();

            foreach (var groupId in groupIds)
            {
                tasks.Add(_mainHub.Clients.Group(groupId).SendAsync(MainHubMethods.UnitUserDeleted,
                    new ViewModels.UnitUser(notification.Entity.UserId, notification.Entity.UnitId) { Id = notification.Entity.Id }, cancellationToken));
            }

            await Task.WhenAll(tasks);
        }
    }
}
