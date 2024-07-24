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

namespace Blueprint.Api.Infrastructure.EventHandlers
{
    public class InjectTypeHandler
    {
        protected readonly BlueprintContext _db;
        protected readonly IMapper _mapper;
        protected readonly IInjectTypeService _InjectTypeService;
        protected readonly IHubContext<MainHub> _mainHub;

        public InjectTypeHandler(
            BlueprintContext db,
            IMapper mapper,
            IInjectTypeService InjectTypeService,
            IHubContext<MainHub> mainHub)
        {
            _db = db;
            _mapper = mapper;
            _InjectTypeService = InjectTypeService;
            _mainHub = mainHub;
        }

        protected string[] GetGroups(InjectTypeEntity InjectTypeEntity)
        {
            var groupIds = new List<string>();
            // the admin data group gets everything
            groupIds.Add(MainHub.ADMIN_DATA_GROUP);

            return groupIds.ToArray();
        }

        protected async Task HandleCreateOrUpdate(
            InjectTypeEntity InjectTypeEntity,
            string method,
            string[] modifiedProperties,
            CancellationToken cancellationToken)
        {
            var groupIds = GetGroups(InjectTypeEntity);
            var InjectType = _mapper.Map<ViewModels.InjectType>(InjectTypeEntity);
            var tasks = new List<Task>();

            foreach (var groupId in groupIds)
            {
                tasks.Add(_mainHub.Clients.Group(groupId).SendAsync(method, InjectType, modifiedProperties, cancellationToken));
            }

            await Task.WhenAll(tasks);
        }
    }

    public class InjectTypeCreatedSignalRHandler : InjectTypeHandler, INotificationHandler<EntityCreated<InjectTypeEntity>>
    {
        public InjectTypeCreatedSignalRHandler(
            BlueprintContext db,
            IMapper mapper,
            IInjectTypeService InjectTypeService,
            IHubContext<MainHub> mainHub) : base(db, mapper, InjectTypeService, mainHub) { }

        public async Task Handle(EntityCreated<InjectTypeEntity> notification, CancellationToken cancellationToken)
        {
            await base.HandleCreateOrUpdate(notification.Entity, MainHubMethods.InjectTypeCreated, null, cancellationToken);
        }
    }

    public class InjectTypeUpdatedSignalRHandler : InjectTypeHandler, INotificationHandler<EntityUpdated<InjectTypeEntity>>
    {
        public InjectTypeUpdatedSignalRHandler(
            BlueprintContext db,
            IMapper mapper,
            IInjectTypeService InjectTypeService,
            IHubContext<MainHub> mainHub) : base(db, mapper, InjectTypeService, mainHub) { }

        public async Task Handle(EntityUpdated<InjectTypeEntity> notification, CancellationToken cancellationToken)
        {
            await base.HandleCreateOrUpdate(
                notification.Entity,
                MainHubMethods.InjectTypeUpdated,
                notification.ModifiedProperties.Select(x => x.TitleCaseToCamelCase()).ToArray(),
                cancellationToken);
        }
    }

    public class InjectTypeDeletedSignalRHandler : InjectTypeHandler, INotificationHandler<EntityDeleted<InjectTypeEntity>>
    {
        public InjectTypeDeletedSignalRHandler(
            BlueprintContext db,
            IMapper mapper,
            IInjectTypeService InjectTypeService,
            IHubContext<MainHub> mainHub) : base(db, mapper, InjectTypeService, mainHub)
        {
        }

        public async Task Handle(EntityDeleted<InjectTypeEntity> notification, CancellationToken cancellationToken)
        {
            var groupIds = base.GetGroups(notification.Entity);
            var tasks = new List<Task>();

            foreach (var groupId in groupIds)
            {
                tasks.Add(_mainHub.Clients.Group(groupId).SendAsync(MainHubMethods.InjectTypeDeleted, notification.Entity.Id, cancellationToken));
            }

            await Task.WhenAll(tasks);
        }
    }
}
