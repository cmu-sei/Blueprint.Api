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
    public class PlayerApplicationHandler
    {
        protected readonly BlueprintContext _db;
        protected readonly IMapper _mapper;
        protected readonly IPlayerApplicationService _PlayerApplicationService;
        protected readonly IHubContext<MainHub> _mainHub;

        public PlayerApplicationHandler(
            BlueprintContext db,
            IMapper mapper,
            IPlayerApplicationService PlayerApplicationService,
            IHubContext<MainHub> mainHub)
        {
            _db = db;
            _mapper = mapper;
            _PlayerApplicationService = PlayerApplicationService;
            _mainHub = mainHub;
        }

        protected string[] GetGroups(PlayerApplicationEntity PlayerApplicationEntity)
        {
            var groupIds = new List<string>();
            groupIds.Add(PlayerApplicationEntity.MselId.ToString());
            // the admin data group gets everything
            groupIds.Add(MainHub.ADMIN_DATA_GROUP);

            return groupIds.ToArray();
        }

        protected async Task HandleCreateOrUpdate(
            PlayerApplicationEntity PlayerApplicationEntity,
            string method,
            string[] modifiedProperties,
            CancellationToken cancellationToken)
        {
            var groupIds = GetGroups(PlayerApplicationEntity);
            var PlayerApplication = _mapper.Map<ViewModels.PlayerApplication>(PlayerApplicationEntity);
            var tasks = new List<Task>();

            foreach (var groupId in groupIds)
            {
                tasks.Add(_mainHub.Clients.Group(groupId).SendAsync(method, PlayerApplication, modifiedProperties, cancellationToken));
            }

            await Task.WhenAll(tasks);
        }
    }

    public class PlayerApplicationCreatedSignalRHandler : PlayerApplicationHandler, INotificationHandler<EntityCreated<PlayerApplicationEntity>>
    {
        public PlayerApplicationCreatedSignalRHandler(
            BlueprintContext db,
            IMapper mapper,
            IPlayerApplicationService PlayerApplicationService,
            IHubContext<MainHub> mainHub) : base(db, mapper, PlayerApplicationService, mainHub) { }

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
            IHubContext<MainHub> mainHub) : base(db, mapper, PlayerApplicationService, mainHub) { }

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
            IHubContext<MainHub> mainHub) : base(db, mapper, PlayerApplicationService, mainHub)
        {
        }

        public async Task Handle(EntityDeleted<PlayerApplicationEntity> notification, CancellationToken cancellationToken)
        {
            var groupIds = base.GetGroups(notification.Entity);
            var tasks = new List<Task>();

            foreach (var groupId in groupIds)
            {
                tasks.Add(_mainHub.Clients.Group(groupId).SendAsync(MainHubMethods.PlayerApplicationDeleted, notification.Entity.Id, cancellationToken));
            }

            await Task.WhenAll(tasks);
        }
    }
}
