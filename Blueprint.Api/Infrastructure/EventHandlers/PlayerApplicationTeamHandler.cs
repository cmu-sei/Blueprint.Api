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
using Microsoft.EntityFrameworkCore;

namespace Blueprint.Api.Infrastructure.EventHandlers
{
    public class PlayerApplicationTeamHandler
    {
        protected readonly BlueprintContext _db;
        protected readonly IMapper _mapper;
        protected readonly IPlayerApplicationTeamService _PlayerApplicationTeamService;
        protected readonly IHubContext<MainHub> _mainHub;

        public PlayerApplicationTeamHandler(
            BlueprintContext db,
            IMapper mapper,
            IPlayerApplicationTeamService PlayerApplicationTeamService,
            IHubContext<MainHub> mainHub)
        {
            _db = db;
            _mapper = mapper;
            _PlayerApplicationTeamService = PlayerApplicationTeamService;
            _mainHub = mainHub;
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
            var tasks = new List<Task>();

            foreach (var groupId in groupIds)
            {
                tasks.Add(_mainHub.Clients.Group(groupId).SendAsync(method, PlayerApplicationTeam, modifiedProperties, cancellationToken));
            }

            await Task.WhenAll(tasks);
        }
    }

    public class PlayerApplicationTeamCreatedSignalRHandler : PlayerApplicationTeamHandler, INotificationHandler<EntityCreated<PlayerApplicationTeamEntity>>
    {
        public PlayerApplicationTeamCreatedSignalRHandler(
            BlueprintContext db,
            IMapper mapper,
            IPlayerApplicationTeamService PlayerApplicationTeamService,
            IHubContext<MainHub> mainHub) : base(db, mapper, PlayerApplicationTeamService, mainHub) { }

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
            IHubContext<MainHub> mainHub) : base(db, mapper, PlayerApplicationTeamService, mainHub) { }

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
            IHubContext<MainHub> mainHub) : base(db, mapper, PlayerApplicationTeamService, mainHub)
        {
        }

        public async Task Handle(EntityDeleted<PlayerApplicationTeamEntity> notification, CancellationToken cancellationToken)
        {
            var groupIds = await base.GetGroups(notification.Entity);
            var tasks = new List<Task>();

            foreach (var groupId in groupIds)
            {
                tasks.Add(_mainHub.Clients.Group(groupId).SendAsync(MainHubMethods.PlayerApplicationTeamDeleted, notification.Entity.Id, cancellationToken));
            }

            await Task.WhenAll(tasks);
        }
    }
}
