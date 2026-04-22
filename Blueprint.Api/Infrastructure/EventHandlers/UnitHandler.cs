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
using Blueprint.Api.Hubs;
using Blueprint.Api.Infrastructure.Extensions;
using Microsoft.EntityFrameworkCore;
using Crucible.Common.EntityEvents.Events;

namespace Blueprint.Api.Infrastructure.EventHandlers
{
    public class UnitHandler
    {
        protected readonly BlueprintContext _db;
        protected readonly IMapper _mapper;
        protected readonly IHubContext<MainHub> _mainHub;

        public UnitHandler(
            BlueprintContext db,
            IMapper mapper,
            IHubContext<MainHub> mainHub)
        {
            _db = db;
            _mapper = mapper;
            _mainHub = mainHub;
        }

        protected async Task<string[]> GetGroupsAsync(UnitEntity unitEntity, CancellationToken cancellationToken)
        {
            var groupIds = new List<string>();
            // add the unit itself
            groupIds.Add(unitEntity.Id.ToString());
            // the admin data group gets everything
            groupIds.Add(MainHub.ADMIN_DATA_GROUP);
            // also notify all MSELs that have this unit as a contributor
            var mselIds = await _db.MselUnits
                .Where(mu => mu.UnitId == unitEntity.Id)
                .Select(mu => mu.MselId.ToString())
                .ToListAsync(cancellationToken);
            groupIds.AddRange(mselIds);

            return groupIds.ToArray();
        }

        protected async Task HandleCreateOrUpdate(
            UnitEntity unitEntity,
            string method,
            string[] modifiedProperties,
            CancellationToken cancellationToken)
        {
            var groupIds = await GetGroupsAsync(unitEntity, cancellationToken);
            var unit = _mapper.Map<ViewModels.Unit>(unitEntity);
            var tasks = new List<Task>();

            foreach (var groupId in groupIds)
            {
                tasks.Add(_mainHub.Clients.Group(groupId).SendAsync(method, unit, modifiedProperties, cancellationToken));
            }

            await Task.WhenAll(tasks);
        }
    }

    public class UnitCreatedSignalRHandler : UnitHandler, INotificationHandler<EntityCreated<UnitEntity>>
    {
        public UnitCreatedSignalRHandler(
            BlueprintContext db,
            IMapper mapper,
            IHubContext<MainHub> mainHub) : base(db, mapper, mainHub) { }

        public async Task Handle(EntityCreated<UnitEntity> notification, CancellationToken cancellationToken)
        {
            await base.HandleCreateOrUpdate(notification.Entity, MainHubMethods.UnitCreated, null, cancellationToken);
        }
    }

    public class UnitUpdatedSignalRHandler : UnitHandler, INotificationHandler<EntityUpdated<UnitEntity>>
    {
        public UnitUpdatedSignalRHandler(
            BlueprintContext db,
            IMapper mapper,
            IHubContext<MainHub> mainHub) : base(db, mapper, mainHub) { }

        public async Task Handle(EntityUpdated<UnitEntity> notification, CancellationToken cancellationToken)
        {
            await base.HandleCreateOrUpdate(
                notification.Entity,
                MainHubMethods.UnitUpdated,
                notification.ModifiedProperties.Select(x => x.TitleCaseToCamelCase()).ToArray(),
                cancellationToken);
        }
    }

    public class UnitDeletedSignalRHandler : UnitHandler, INotificationHandler<EntityDeleted<UnitEntity>>
    {
        public UnitDeletedSignalRHandler(
            BlueprintContext db,
            IMapper mapper,
            IHubContext<MainHub> mainHub) : base(db, mapper, mainHub)
        {
        }

        public async Task Handle(EntityDeleted<UnitEntity> notification, CancellationToken cancellationToken)
        {
            var groupIds = await base.GetGroupsAsync(notification.Entity, CancellationToken.None);
            var tasks = new List<Task>();

            foreach (var groupId in groupIds)
            {
                tasks.Add(_mainHub.Clients.Group(groupId).SendAsync(MainHubMethods.UnitDeleted, notification.Entity.Id, cancellationToken));
            }

            await Task.WhenAll(tasks);
        }
    }
}
