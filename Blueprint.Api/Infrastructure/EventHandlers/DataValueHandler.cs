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
    public class DataValueHandler
    {
        protected readonly BlueprintContext _db;
        protected readonly IMapper _mapper;
        protected readonly IDataValueService _dataValueService;
        protected readonly IHubContext<MainHub> _mainHub;

        public DataValueHandler(
            BlueprintContext db,
            IMapper mapper,
            IDataValueService dataValueService,
            IHubContext<MainHub> mainHub)
        {
            _db = db;
            _mapper = mapper;
            _dataValueService = dataValueService;
            _mainHub = mainHub;
        }

        protected string[] GetGroups(DataValueEntity dataValueEntity)
        {
            var groupIds = new List<string>();
            var mselId = _db.DataFields
                .SingleOrDefault(df => df.Id == dataValueEntity.DataFieldId)
                .MselId;
            if (mselId != null)
            {
                groupIds.Add(mselId.ToString());
            }
            // the admin data group gets everything
            groupIds.Add(MainHub.ADMIN_DATA_GROUP);

            return groupIds.ToArray();
        }

        protected async Task HandleCreateOrUpdate(
            DataValueEntity dataValueEntity,
            string method,
            string[] modifiedProperties,
            CancellationToken cancellationToken)
        {
            var groupIds = GetGroups(dataValueEntity);
            var dataValue = _mapper.Map<ViewModels.DataValue>(dataValueEntity);
            var tasks = new List<Task>();

            foreach (var groupId in groupIds)
            {
                tasks.Add(_mainHub.Clients.Group(groupId).SendAsync(method, dataValue, modifiedProperties, cancellationToken));
            }

            await Task.WhenAll(tasks);
        }
    }

    public class DataValueCreatedSignalRHandler : DataValueHandler, INotificationHandler<EntityCreated<DataValueEntity>>
    {
        public DataValueCreatedSignalRHandler(
            BlueprintContext db,
            IMapper mapper,
            IDataValueService dataValueService,
            IHubContext<MainHub> mainHub) : base(db, mapper, dataValueService, mainHub) { }

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
            IHubContext<MainHub> mainHub) : base(db, mapper, dataValueService, mainHub) { }

        public async Task Handle(EntityUpdated<DataValueEntity> notification, CancellationToken cancellationToken)
        {
            await base.HandleCreateOrUpdate(
                notification.Entity,
                MainHubMethods.DataValueUpdated,
                notification.ModifiedProperties.Select(x => x.TitleCaseToCamelCase()).ToArray(),
                cancellationToken);
        }
    }

    public class DataValueDeletedSignalRHandler : DataValueHandler, INotificationHandler<EntityDeleted<DataValueEntity>>
    {
        public DataValueDeletedSignalRHandler(
            BlueprintContext db,
            IMapper mapper,
            IDataValueService dataValueService,
            IHubContext<MainHub> mainHub) : base(db, mapper, dataValueService, mainHub)
        {
        }

        public async Task Handle(EntityDeleted<DataValueEntity> notification, CancellationToken cancellationToken)
        {
            var groupIds = base.GetGroups(notification.Entity);
            var tasks = new List<Task>();

            foreach (var groupId in groupIds)
            {
                tasks.Add(_mainHub.Clients.Group(groupId).SendAsync(MainHubMethods.DataValueDeleted, notification.Entity.Id, cancellationToken));
            }

            await Task.WhenAll(tasks);
        }
    }
}
