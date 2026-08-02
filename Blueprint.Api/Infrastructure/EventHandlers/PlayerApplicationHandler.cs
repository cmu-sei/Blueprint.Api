// Copyright 2023 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AutoMapper;
using MediatR;
using Blueprint.Api.Data;
using Blueprint.Api.Data.Models;
using Blueprint.Api.Services;
using Blueprint.Api.Hubs;
using Blueprint.Api.Infrastructure.SignalR;
using Blueprint.Api.Infrastructure.Extensions;
using Crucible.Common.EntityEvents.Events;

namespace Blueprint.Api.Infrastructure.EventHandlers
{
    public class PlayerApplicationHandler
    {
        protected readonly BlueprintContext _db;
        protected readonly IMapper _mapper;
        protected readonly IPlayerApplicationService _PlayerApplicationService;
        protected readonly IHubBroadcaster _broadcaster;

        public PlayerApplicationHandler(
            BlueprintContext db,
            IMapper mapper,
            IPlayerApplicationService PlayerApplicationService,
            IHubBroadcaster broadcaster)
        {
            _db = db;
            _mapper = mapper;
            _PlayerApplicationService = PlayerApplicationService;
            _broadcaster = broadcaster;
        }

        protected string[] GetGroups(PlayerApplicationEntity PlayerApplicationEntity)
        {
            var groupIds = new List<string>();
            groupIds.Add(PlayerApplicationEntity.MselId.ToString());
            // the admin data group gets everything
            groupIds.Add(MainHub.ADMIN_DATA_GROUP);

            return groupIds.ToArray();
        }

        protected Task HandleCreateOrUpdate(
            PlayerApplicationEntity PlayerApplicationEntity,
            string method,
            string[] modifiedProperties,
            CancellationToken cancellationToken)
        {
            var groupIds = GetGroups(PlayerApplicationEntity);
            var PlayerApplication = _mapper.Map<ViewModels.PlayerApplication>(PlayerApplicationEntity);
            _broadcaster.Broadcast(groupIds, method, PlayerApplication, modifiedProperties);
            return Task.CompletedTask;
        }
    }

    public class PlayerApplicationCreatedSignalRHandler : PlayerApplicationHandler, INotificationHandler<EntityCreated<PlayerApplicationEntity>>
    {
        public PlayerApplicationCreatedSignalRHandler(
            BlueprintContext db,
            IMapper mapper,
            IPlayerApplicationService PlayerApplicationService,
            IHubBroadcaster broadcaster) : base(db, mapper, PlayerApplicationService, broadcaster) { }

        public async Task Handle(EntityCreated<PlayerApplicationEntity> notification, CancellationToken cancellationToken)
        {
            await base.HandleCreateOrUpdate(notification.Entity, MainHubMethods.PlayerApplicationCreated, null, cancellationToken);
        }
    }

    public class PlayerApplicationUpdatedSignalRHandler : PlayerApplicationHandler, INotificationHandler<EntityUpdated<PlayerApplicationEntity>>
    {
        public PlayerApplicationUpdatedSignalRHandler(
            BlueprintContext db,
            IMapper mapper,
            IPlayerApplicationService PlayerApplicationService,
            IHubBroadcaster broadcaster) : base(db, mapper, PlayerApplicationService, broadcaster) { }

        public async Task Handle(EntityUpdated<PlayerApplicationEntity> notification, CancellationToken cancellationToken)
        {
            await base.HandleCreateOrUpdate(
                notification.Entity,
                MainHubMethods.PlayerApplicationUpdated,
                notification.ModifiedProperties.Select(x => x.TitleCaseToCamelCase()).ToArray(),
                cancellationToken);
        }
    }

    public class PlayerApplicationDeletedSignalRHandler : PlayerApplicationHandler, INotificationHandler<EntityDeleted<PlayerApplicationEntity>>
    {
        public PlayerApplicationDeletedSignalRHandler(
            BlueprintContext db,
            IMapper mapper,
            IPlayerApplicationService PlayerApplicationService,
            IHubBroadcaster broadcaster) : base(db, mapper, PlayerApplicationService, broadcaster)
        {
        }

        public Task Handle(EntityDeleted<PlayerApplicationEntity> notification, CancellationToken cancellationToken)
        {
            var groupIds = base.GetGroups(notification.Entity);
            _broadcaster.Broadcast(groupIds, MainHubMethods.PlayerApplicationDeleted, notification.Entity.Id);
            return Task.CompletedTask;
        }
    }
}
