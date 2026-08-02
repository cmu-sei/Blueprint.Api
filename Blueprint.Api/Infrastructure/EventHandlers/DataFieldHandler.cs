// Copyright 2023 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using System.Collections.Generic;
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
    public class DataFieldHandler
    {
        protected readonly BlueprintContext _db;
        protected readonly IMapper _mapper;
        protected readonly IDataFieldService _dataFieldService;
        protected readonly IHubBroadcaster _broadcaster;

        public DataFieldHandler(
            BlueprintContext db,
            IMapper mapper,
            IDataFieldService dataFieldService,
            IHubBroadcaster broadcaster)
        {
            _db = db;
            _mapper = mapper;
            _dataFieldService = dataFieldService;
            _broadcaster = broadcaster;
        }

        protected string[] GetGroups(DataFieldEntity dataFieldEntity)
        {
            var groupIds = new List<string>();
            groupIds.Add(dataFieldEntity.MselId.ToString());
            // the admin data group gets everything
            groupIds.Add(MainHub.ADMIN_DATA_GROUP);

            return groupIds.ToArray();
        }

        protected async Task HandleCreateOrUpdate(
            DataFieldEntity dataFieldEntity,
            string method,
            string[] modifiedProperties,
            CancellationToken cancellationToken)
        {
            var groupIds = GetGroups(dataFieldEntity);
            dataFieldEntity = await _db.DataFields
                .Include(f => f.DataOptions)
                .SingleOrDefaultAsync(f => f.Id == dataFieldEntity.Id);
            var dataField = _mapper.Map<ViewModels.DataField>(dataFieldEntity);
            _broadcaster.Broadcast(groupIds, method, dataField, modifiedProperties);
        }
    }

    public class DataFieldCreatedSignalRHandler : DataFieldHandler, INotificationHandler<EntityCreated<DataFieldEntity>>
    {
        public DataFieldCreatedSignalRHandler(
            BlueprintContext db,
            IMapper mapper,
            IDataFieldService dataFieldService,
            IHubBroadcaster broadcaster) : base(db, mapper, dataFieldService, broadcaster) { }

        public async Task Handle(EntityCreated<DataFieldEntity> notification, CancellationToken cancellationToken)
        {
            await base.HandleCreateOrUpdate(notification.Entity, MainHubMethods.DataFieldCreated, null, cancellationToken);
        }
    }

    public class DataFieldUpdatedSignalRHandler : DataFieldHandler, INotificationHandler<EntityUpdated<DataFieldEntity>>
    {
        public DataFieldUpdatedSignalRHandler(
            BlueprintContext db,
            IMapper mapper,
            IDataFieldService dataFieldService,
            IHubBroadcaster broadcaster) : base(db, mapper, dataFieldService, broadcaster) { }

        public async Task Handle(EntityUpdated<DataFieldEntity> notification, CancellationToken cancellationToken)
        {
            await base.HandleCreateOrUpdate(
                notification.Entity,
                MainHubMethods.DataFieldUpdated,
                notification.ModifiedProperties.Select(x => x.TitleCaseToCamelCase()).ToArray(),
                cancellationToken);
        }
    }

    public class DataFieldDeletedSignalRHandler : DataFieldHandler, INotificationHandler<EntityDeleted<DataFieldEntity>>
    {
        public DataFieldDeletedSignalRHandler(
            BlueprintContext db,
            IMapper mapper,
            IDataFieldService dataFieldService,
            IHubBroadcaster broadcaster) : base(db, mapper, dataFieldService, broadcaster)
        {
        }

        public Task Handle(EntityDeleted<DataFieldEntity> notification, CancellationToken cancellationToken)
        {
            var groupIds = base.GetGroups(notification.Entity);
            _broadcaster.Broadcast(groupIds, MainHubMethods.DataFieldDeleted, notification.Entity.Id);
            return Task.CompletedTask;
        }
    }
}
