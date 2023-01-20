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
    public class DataFieldHandler
    {
        protected readonly BlueprintContext _db;
        protected readonly IMapper _mapper;
        protected readonly IDataFieldService _dataFieldService;
        protected readonly IHubContext<MainHub> _mainHub;

        public DataFieldHandler(
            BlueprintContext db,
            IMapper mapper,
            IDataFieldService dataFieldService,
            IHubContext<MainHub> mainHub)
        {
            _db = db;
            _mapper = mapper;
            _dataFieldService = dataFieldService;
            _mainHub = mainHub;
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
            var dataField = _mapper.Map<ViewModels.DataField>(dataFieldEntity);
            var tasks = new List<Task>();

            foreach (var groupId in groupIds)
            {
                tasks.Add(_mainHub.Clients.Group(groupId).SendAsync(method, dataField, modifiedProperties, cancellationToken));
            }

            await Task.WhenAll(tasks);
        }
    }

    public class DataFieldCreatedSignalRHandler : DataFieldHandler, INotificationHandler<EntityCreated<DataFieldEntity>>
    {
        public DataFieldCreatedSignalRHandler(
            BlueprintContext db,
            IMapper mapper,
            IDataFieldService dataFieldService,
            IHubContext<MainHub> mainHub) : base(db, mapper, dataFieldService, mainHub) { }

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
            IHubContext<MainHub> mainHub) : base(db, mapper, dataFieldService, mainHub) { }

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
            IHubContext<MainHub> mainHub) : base(db, mapper, dataFieldService, mainHub)
        {
        }

        public async Task Handle(EntityDeleted<DataFieldEntity> notification, CancellationToken cancellationToken)
        {
            var groupIds = base.GetGroups(notification.Entity);
            var tasks = new List<Task>();

            foreach (var groupId in groupIds)
            {
                tasks.Add(_mainHub.Clients.Group(groupId).SendAsync(MainHubMethods.DataFieldDeleted, notification.Entity.Id, cancellationToken));
            }

            await Task.WhenAll(tasks);
        }
    }
}
