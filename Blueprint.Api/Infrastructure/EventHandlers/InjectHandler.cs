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
    public class InjectHandler
    {
        protected readonly BlueprintContext _db;
        protected readonly IMapper _mapper;
        protected readonly IInjectService _InjectService;
        protected readonly IHubContext<MainHub> _mainHub;

        public InjectHandler(
            BlueprintContext db,
            IMapper mapper,
            IInjectService InjectService,
            IHubContext<MainHub> mainHub)
        {
            _db = db;
            _mapper = mapper;
            _InjectService = InjectService;
            _mainHub = mainHub;
        }

        protected string[] GetGroups(InjectEntity InjectEntity)
        {
            var groupIds = new List<string>();
            // the admin data group gets everything
            groupIds.Add(MainHub.ADMIN_DATA_GROUP);

            return groupIds.ToArray();
        }

        protected async Task HandleCreateOrUpdate(
            InjectEntity InjectEntity,
            string method,
            string[] modifiedProperties,
            CancellationToken cancellationToken)
        {
            var groupIds = GetGroups(InjectEntity);
            var Inject = _mapper.Map<ViewModels.Injectm>(InjectEntity);
            var tasks = new List<Task>();

            foreach (var groupId in groupIds)
            {
                tasks.Add(_mainHub.Clients.Group(groupId).SendAsync(method, Inject, modifiedProperties, cancellationToken));
            }

            await Task.WhenAll(tasks);
        }
    }

    public class InjectCreatedSignalRHandler : InjectHandler, INotificationHandler<EntityCreated<InjectEntity>>
    {
        public InjectCreatedSignalRHandler(
            BlueprintContext db,
            IMapper mapper,
            IInjectService InjectService,
            IHubContext<MainHub> mainHub) : base(db, mapper, InjectService, mainHub) { }

        public async Task Handle(EntityCreated<InjectEntity> notification, CancellationToken cancellationToken)
        {
            await base.HandleCreateOrUpdate(notification.Entity, MainHubMethods.InjectCreated, null, cancellationToken);
        }
    }

    public class InjectUpdatedSignalRHandler : InjectHandler, INotificationHandler<EntityUpdated<InjectEntity>>
    {
        public InjectUpdatedSignalRHandler(
            BlueprintContext db,
            IMapper mapper,
            IInjectService InjectService,
            IHubContext<MainHub> mainHub) : base(db, mapper, InjectService, mainHub) { }

        public async Task Handle(EntityUpdated<InjectEntity> notification, CancellationToken cancellationToken)
        {
            await base.HandleCreateOrUpdate(
                notification.Entity,
                MainHubMethods.InjectUpdated,
                notification.ModifiedProperties.Select(x => x.TitleCaseToCamelCase()).ToArray(),
                cancellationToken);
        }
    }

    public class InjectDeletedSignalRHandler : InjectHandler, INotificationHandler<EntityDeleted<InjectEntity>>
    {
        public InjectDeletedSignalRHandler(
            BlueprintContext db,
            IMapper mapper,
            IInjectService InjectService,
            IHubContext<MainHub> mainHub) : base(db, mapper, InjectService, mainHub)
        {
        }

        public async Task Handle(EntityDeleted<InjectEntity> notification, CancellationToken cancellationToken)
        {
            var groupIds = base.GetGroups(notification.Entity);
            var tasks = new List<Task>();

            foreach (var groupId in groupIds)
            {
                tasks.Add(_mainHub.Clients.Group(groupId).SendAsync(MainHubMethods.InjectDeleted, notification.Entity.Id, cancellationToken));
            }

            await Task.WhenAll(tasks);
        }
    }
}
