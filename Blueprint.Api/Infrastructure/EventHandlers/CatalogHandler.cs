// Copyright 2024 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AutoMapper;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Blueprint.Api.Data;
using Blueprint.Api.Data.Models;
using Blueprint.Api.Services;
using Blueprint.Api.Hubs;
using Blueprint.Api.Infrastructure.SignalR;
using Blueprint.Api.Infrastructure.Extensions;
using Crucible.Common.EntityEvents.Events;

namespace Blueprint.Api.Infrastructure.EventHandlers
{
    public class CatalogHandler
    {
        protected readonly BlueprintContext _db;
        protected readonly IMapper _mapper;
        protected readonly ICatalogService _CatalogService;
        protected readonly IHubBroadcaster _broadcaster;

        public CatalogHandler(
            BlueprintContext db,
            IMapper mapper,
            ICatalogService CatalogService,
            IHubBroadcaster broadcaster)
        {
            _db = db;
            _mapper = mapper;
            _CatalogService = CatalogService;
            _broadcaster = broadcaster;
        }

        protected async Task<string[]> GetGroups(CatalogEntity CatalogEntity)
        {
            var groupIds = await _db.CatalogUnits
                .Where(m => m.CatalogId == CatalogEntity.Id)
                .Select(m => m.UnitId.ToString())
                .ToListAsync();
            // the admin data group gets everything
            groupIds.Add(MainHub.ADMIN_DATA_GROUP);

            return groupIds.ToArray();
        }

        protected async Task HandleCreateOrUpdate(
            CatalogEntity CatalogEntity,
            string method,
            string[] modifiedProperties,
            CancellationToken cancellationToken)
        {
            var groupIds = await GetGroups(CatalogEntity);
            var Catalog = _mapper.Map<ViewModels.Catalog>(CatalogEntity);
            _broadcaster.Broadcast(groupIds, method, Catalog, modifiedProperties);
        }
    }

    public class CatalogCreatedSignalRHandler : CatalogHandler, INotificationHandler<EntityCreated<CatalogEntity>>
    {
        public CatalogCreatedSignalRHandler(
            BlueprintContext db,
            IMapper mapper,
            ICatalogService CatalogService,
            IHubBroadcaster broadcaster) : base(db, mapper, CatalogService, broadcaster) { }

        public async Task Handle(EntityCreated<CatalogEntity> notification, CancellationToken cancellationToken)
        {
            await base.HandleCreateOrUpdate(notification.Entity, MainHubMethods.CatalogCreated, null, cancellationToken);
        }
    }

    public class CatalogUpdatedSignalRHandler : CatalogHandler, INotificationHandler<EntityUpdated<CatalogEntity>>
    {
        public CatalogUpdatedSignalRHandler(
            BlueprintContext db,
            IMapper mapper,
            ICatalogService CatalogService,
            IHubBroadcaster broadcaster) : base(db, mapper, CatalogService, broadcaster) { }

        public async Task Handle(EntityUpdated<CatalogEntity> notification, CancellationToken cancellationToken)
        {
            await base.HandleCreateOrUpdate(
                notification.Entity,
                MainHubMethods.CatalogUpdated,
                notification.ModifiedProperties.Select(x => x.TitleCaseToCamelCase()).ToArray(),
                cancellationToken);
        }
    }

    public class CatalogDeletedSignalRHandler : CatalogHandler, INotificationHandler<EntityDeleted<CatalogEntity>>
    {
        public CatalogDeletedSignalRHandler(
            BlueprintContext db,
            IMapper mapper,
            ICatalogService CatalogService,
            IHubBroadcaster broadcaster) : base(db, mapper, CatalogService, broadcaster)
        {
        }

        public async Task Handle(EntityDeleted<CatalogEntity> notification, CancellationToken cancellationToken)
        {
            var groupIds = await base.GetGroups(notification.Entity);
            _broadcaster.Broadcast(groupIds, MainHubMethods.CatalogDeleted, notification.Entity.Id);
        }
    }
}
