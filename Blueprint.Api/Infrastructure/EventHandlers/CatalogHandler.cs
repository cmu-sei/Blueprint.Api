// Copyright 2024 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using System.Collections.Generic;
using System.Data;
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
    public class CatalogHandler
    {
        protected readonly BlueprintContext _db;
        protected readonly IMapper _mapper;
        protected readonly ICatalogService _CatalogService;
        protected readonly IHubContext<MainHub> _mainHub;

        public CatalogHandler(
            BlueprintContext db,
            IMapper mapper,
            ICatalogService CatalogService,
            IHubContext<MainHub> mainHub)
        {
            _db = db;
            _mapper = mapper;
            _CatalogService = CatalogService;
            _mainHub = mainHub;
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
            var tasks = new List<Task>();

            foreach (var groupId in groupIds)
            {
                tasks.Add(_mainHub.Clients.Group(groupId).SendAsync(method, Catalog, modifiedProperties, cancellationToken));
            }

            await Task.WhenAll(tasks);
        }
    }

    public class CatalogCreatedSignalRHandler : CatalogHandler, INotificationHandler<EntityCreated<CatalogEntity>>
    {
        public CatalogCreatedSignalRHandler(
            BlueprintContext db,
            IMapper mapper,
            ICatalogService CatalogService,
            IHubContext<MainHub> mainHub) : base(db, mapper, CatalogService, mainHub) { }

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
            IHubContext<MainHub> mainHub) : base(db, mapper, CatalogService, mainHub) { }

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
            IHubContext<MainHub> mainHub) : base(db, mapper, CatalogService, mainHub)
        {
        }

        public async Task Handle(EntityDeleted<CatalogEntity> notification, CancellationToken cancellationToken)
        {
            var groupIds = await base.GetGroups(notification.Entity);
            var tasks = new List<Task>();

            foreach (var groupId in groupIds)
            {
                tasks.Add(_mainHub.Clients.Group(groupId).SendAsync(MainHubMethods.CatalogDeleted, notification.Entity.Id, cancellationToken));
            }

            await Task.WhenAll(tasks);
        }
    }
}
