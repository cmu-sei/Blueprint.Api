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
    public class DataValueHandler
    {
        protected readonly BlueprintContext _db;
        protected readonly IMapper _mapper;
        protected readonly IDataValueService _dataValueService;
        protected readonly IHubBroadcaster _broadcaster;

        public DataValueHandler(
            BlueprintContext db,
            IMapper mapper,
            IDataValueService dataValueService,
            IHubBroadcaster broadcaster)
        {
            _db = db;
            _mapper = mapper;
            _dataValueService = dataValueService;
            _broadcaster = broadcaster;
        }

        protected string[] GetGroups(DataValueEntity dataValueEntity)
        {
            var groupIds = new List<string>();
            var mselId = _db.ScenarioEvents
              .Where(se => se.Id == dataValueEntity.ScenarioEventId)
              .Select(se => se.MselId)
              .SingleOrDefault();
            groupIds.Add(mselId.ToString());
            // the admin data group gets everything
            groupIds.Add(MainHub.ADMIN_DATA_GROUP);

            return groupIds.ToArray();
        }

        protected Task HandleCreateOrUpdate(
            DataValueEntity dataValueEntity,
            string method,
            string[] modifiedProperties,
            CancellationToken cancellationToken)
        {
            var groupIds = GetGroups(dataValueEntity);
            var dataValue = _mapper.Map<ViewModels.DataValue>(dataValueEntity);
            _broadcaster.Broadcast(groupIds, method, dataValue, modifiedProperties);
            return Task.CompletedTask;
        }
    }

    public class DataValueCreatedSignalRHandler : DataValueHandler, INotificationHandler<EntityCreated<DataValueEntity>>
    {
        public DataValueCreatedSignalRHandler(
            BlueprintContext db,
            IMapper mapper,
            IDataValueService dataValueService,
            IHubBroadcaster broadcaster) : base(db, mapper, dataValueService, broadcaster) { }

        public async Task Handle(EntityCreated<DataValueEntity> notification, CancellationToken cancellationToken)
        {
            await base.HandleCreateOrUpdate(notification.Entity, MainHubMethods.DataValueCreated, null, cancellationToken);
        }
    }

    public class DataValueUpdatedSignalRHandler : DataValueHandler, INotificationHandler<EntityUpdated<DataValueEntity>>
    {
        public DataValueUpdatedSignalRHandler(
            BlueprintContext db,
            IMapper mapper,
            IDataValueService dataValueService,
            IHubBroadcaster broadcaster) : base(db, mapper, dataValueService, broadcaster) { }

        public async Task Handle(EntityUpdated<DataValueEntity> notification, CancellationToken cancellationToken)
        {
            await base.HandleCreateOrUpdate(
                notification.Entity,
                MainHubMethods.DataValueUpdated,
                null,
                cancellationToken);
        }
    }

    public class DataValueDeletedSignalRHandler : DataValueHandler, INotificationHandler<EntityDeleted<DataValueEntity>>
    {
        public DataValueDeletedSignalRHandler(
            BlueprintContext db,
            IMapper mapper,
            IDataValueService dataValueService,
            IHubBroadcaster broadcaster) : base(db, mapper, dataValueService, broadcaster)
        {
        }

        public Task Handle(EntityDeleted<DataValueEntity> notification, CancellationToken cancellationToken)
        {
            var groupIds = base.GetGroups(notification.Entity);
            _broadcaster.Broadcast(groupIds, MainHubMethods.DataValueDeleted, notification.Entity.Id);
            return Task.CompletedTask;
        }
    }
}
