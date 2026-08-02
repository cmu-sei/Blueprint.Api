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
using Microsoft.EntityFrameworkCore;
using Crucible.Common.EntityEvents.Events;

namespace Blueprint.Api.Infrastructure.EventHandlers
{
    public class PlayerApplicationTeamHandler
    {
        protected readonly BlueprintContext _db;
        protected readonly IMapper _mapper;
        protected readonly IPlayerApplicationTeamService _PlayerApplicationTeamService;
        protected readonly IHubBroadcaster _broadcaster;

        public PlayerApplicationTeamHandler(
            BlueprintContext db,
            IMapper mapper,
            IPlayerApplicationTeamService PlayerApplicationTeamService,
            IHubBroadcaster broadcaster)
        {
            _db = db;
            _mapper = mapper;
            _PlayerApplicationTeamService = PlayerApplicationTeamService;
            _broadcaster = broadcaster;
        }

        protected async Task<string[]> GetGroups(PlayerApplicationTeamEntity playerApplicationTeamEntity)
        {
            var groupIds = new List<string>();
            var mselId = await _db.PlayerApplications
                .Where(c => c.Id == playerApplicationTeamEntity.PlayerApplicationId)
                .Select(c => c.MselId)
                .SingleOrDefaultAsync();
            groupIds.Add(mselId.ToString());
            // the admin data group gets everything
            groupIds.Add(MainHub.ADMIN_DATA_GROUP);

            return groupIds.ToArray();
        }

        protected async Task HandleCreateOrUpdate(
            PlayerApplicationTeamEntity PlayerApplicationTeamEntity,
            string method,
            string[] modifiedProperties,
            CancellationToken cancellationToken)
        {
            var groupIds = await GetGroups(PlayerApplicationTeamEntity);
            var PlayerApplicationTeam = _mapper.Map<ViewModels.PlayerApplicationTeam>(PlayerApplicationTeamEntity);
            _broadcaster.Broadcast(groupIds, method, PlayerApplicationTeam, modifiedProperties);
        }
    }

    public class PlayerApplicationTeamCreatedSignalRHandler : PlayerApplicationTeamHandler, INotificationHandler<EntityCreated<PlayerApplicationTeamEntity>>
    {
        public PlayerApplicationTeamCreatedSignalRHandler(
            BlueprintContext db,
            IMapper mapper,
            IPlayerApplicationTeamService PlayerApplicationTeamService,
            IHubBroadcaster broadcaster) : base(db, mapper, PlayerApplicationTeamService, broadcaster) { }

        public async Task Handle(EntityCreated<PlayerApplicationTeamEntity> notification, CancellationToken cancellationToken)
        {
            await base.HandleCreateOrUpdate(notification.Entity, MainHubMethods.PlayerApplicationTeamCreated, null, cancellationToken);
        }
    }

    public class PlayerApplicationTeamUpdatedSignalRHandler : PlayerApplicationTeamHandler, INotificationHandler<EntityUpdated<PlayerApplicationTeamEntity>>
    {
        public PlayerApplicationTeamUpdatedSignalRHandler(
            BlueprintContext db,
            IMapper mapper,
            IPlayerApplicationTeamService PlayerApplicationTeamService,
            IHubBroadcaster broadcaster) : base(db, mapper, PlayerApplicationTeamService, broadcaster) { }

        public async Task Handle(EntityUpdated<PlayerApplicationTeamEntity> notification, CancellationToken cancellationToken)
        {
            await base.HandleCreateOrUpdate(
                notification.Entity,
                MainHubMethods.PlayerApplicationTeamUpdated,
                notification.ModifiedProperties.Select(x => x.TitleCaseToCamelCase()).ToArray(),
                cancellationToken);
        }
    }

    public class PlayerApplicationTeamDeletedSignalRHandler : PlayerApplicationTeamHandler, INotificationHandler<EntityDeleted<PlayerApplicationTeamEntity>>
    {
        public PlayerApplicationTeamDeletedSignalRHandler(
            BlueprintContext db,
            IMapper mapper,
            IPlayerApplicationTeamService PlayerApplicationTeamService,
            IHubBroadcaster broadcaster) : base(db, mapper, PlayerApplicationTeamService, broadcaster)
        {
        }

        public async Task Handle(EntityDeleted<PlayerApplicationTeamEntity> notification, CancellationToken cancellationToken)
        {
            var groupIds = await base.GetGroups(notification.Entity);
            _broadcaster.Broadcast(groupIds, MainHubMethods.PlayerApplicationTeamDeleted, notification.Entity.Id);
        }
    }
}
