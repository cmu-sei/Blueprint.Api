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
    public class ScenarioEventHandler
    {
        protected readonly BlueprintContext _db;
        protected readonly IMapper _mapper;
        protected readonly IScenarioEventService _scenarioEventService;
        protected readonly IHubContext<MainHub> _mainHub;

        public ScenarioEventHandler(
            BlueprintContext db,
            IMapper mapper,
            IScenarioEventService scenarioEventService,
            IHubContext<MainHub> mainHub)
        {
            _db = db;
            _mapper = mapper;
            _scenarioEventService = scenarioEventService;
            _mainHub = mainHub;
        }

        protected string[] GetGroups(ScenarioEventEntity scenarioEventEntity)
        {
            var groupIds = new List<string>();
            groupIds.Add(scenarioEventEntity.MselId.ToString());
            // the admin data group gets everything
            groupIds.Add(MainHub.ADMIN_DATA_GROUP);

            return groupIds.ToArray();
        }

        protected async Task HandleCreateOrUpdate(
            ScenarioEventEntity scenarioEventEntity,
            string method,
            string[] modifiedProperties,
            CancellationToken cancellationToken)
        {
            var groupIds = GetGroups(scenarioEventEntity);
            var scenarioEvent = _mapper.Map<ViewModels.ScenarioEvent>(scenarioEventEntity);
            var tasks = new List<Task>();

            foreach (var groupId in groupIds)
            {
                tasks.Add(_mainHub.Clients.Group(groupId).SendAsync(method, scenarioEvent, modifiedProperties, cancellationToken));
            }

            await Task.WhenAll(tasks);
        }
    }

    public class ScenarioEventCreatedSignalRHandler : ScenarioEventHandler, INotificationHandler<EntityCreated<ScenarioEventEntity>>
    {
        public ScenarioEventCreatedSignalRHandler(
            BlueprintContext db,
            IMapper mapper,
            IScenarioEventService scenarioEventService,
            IHubContext<MainHub> mainHub) : base(db, mapper, scenarioEventService, mainHub) { }

        public async Task Handle(EntityCreated<ScenarioEventEntity> notification, CancellationToken cancellationToken)
        {
            await base.HandleCreateOrUpdate(notification.Entity, MainHubMethods.ScenarioEventCreated, null, cancellationToken);
        }
    }

    public class ScenarioEventUpdatedSignalRHandler : ScenarioEventHandler, INotificationHandler<EntityUpdated<ScenarioEventEntity>>
    {
        public ScenarioEventUpdatedSignalRHandler(
            BlueprintContext db,
            IMapper mapper,
            IScenarioEventService scenarioEventService,
            IHubContext<MainHub> mainHub) : base(db, mapper, scenarioEventService, mainHub) { }

        public async Task Handle(EntityUpdated<ScenarioEventEntity> notification, CancellationToken cancellationToken)
        {
            await base.HandleCreateOrUpdate(
                notification.Entity,
                MainHubMethods.ScenarioEventUpdated,
                notification.ModifiedProperties.Select(x => x.TitleCaseToCamelCase()).ToArray(),
                cancellationToken);
        }
    }

    public class ScenarioEventDeletedSignalRHandler : ScenarioEventHandler, INotificationHandler<EntityDeleted<ScenarioEventEntity>>
    {
        public ScenarioEventDeletedSignalRHandler(
            BlueprintContext db,
            IMapper mapper,
            IScenarioEventService scenarioEventService,
            IHubContext<MainHub> mainHub) : base(db, mapper, scenarioEventService, mainHub)
        {
        }

        public async Task Handle(EntityDeleted<ScenarioEventEntity> notification, CancellationToken cancellationToken)
        {
            var groupIds = base.GetGroups(notification.Entity);
            var tasks = new List<Task>();

            foreach (var groupId in groupIds)
            {
                tasks.Add(_mainHub.Clients.Group(groupId).SendAsync(MainHubMethods.ScenarioEventDeleted, notification.Entity.Id, cancellationToken));
            }

            await Task.WhenAll(tasks);
        }
    }
}
