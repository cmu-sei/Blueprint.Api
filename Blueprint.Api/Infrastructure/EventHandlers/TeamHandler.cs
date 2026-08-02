// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
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
    public class TeamHandler
    {
        protected readonly BlueprintContext _db;
        protected readonly IMapper _mapper;
        protected readonly ITeamService _TeamService;
        protected readonly IHubBroadcaster _broadcaster;

        public TeamHandler(
            BlueprintContext db,
            IMapper mapper,
            ITeamService TeamService,
            IHubBroadcaster broadcaster)
        {
            _db = db;
            _mapper = mapper;
            _TeamService = TeamService;
            _broadcaster = broadcaster;
        }

        protected string[] GetGroups(TeamEntity teamEntity)
        {
            var groupIds = new List<string>();
            // add the team
            groupIds.Add(teamEntity.Id.ToString());
            // add the msel group
            groupIds.Add(teamEntity.MselId.ToString());
            // the admin data group gets everything
            groupIds.Add(MainHub.ADMIN_DATA_GROUP);

            return groupIds.ToArray();
        }

        protected Task HandleCreateOrUpdate(
            TeamEntity teamEntity,
            string method,
            string[] modifiedProperties,
            CancellationToken cancellationToken)
        {
            var groupIds = GetGroups(teamEntity);
            var team = _mapper.Map<ViewModels.Team>(teamEntity);
            _broadcaster.Broadcast(groupIds, method, team, modifiedProperties);
            return Task.CompletedTask;
        }
    }

    public class TeamCreatedSignalRHandler : TeamHandler, INotificationHandler<EntityCreated<TeamEntity>>
    {
        public TeamCreatedSignalRHandler(
            BlueprintContext db,
            IMapper mapper,
            ITeamService teamService,
            IHubBroadcaster broadcaster) : base(db, mapper, teamService, broadcaster) { }

        public async Task Handle(EntityCreated<TeamEntity> notification, CancellationToken cancellationToken)
        {
            await base.HandleCreateOrUpdate(notification.Entity, MainHubMethods.TeamCreated, null, cancellationToken);
        }
    }

    public class TeamUpdatedSignalRHandler : TeamHandler, INotificationHandler<EntityUpdated<TeamEntity>>
    {
        public TeamUpdatedSignalRHandler(
            BlueprintContext db,
            IMapper mapper,
            ITeamService teamService,
            IHubBroadcaster broadcaster) : base(db, mapper, teamService, broadcaster) { }

        public async Task Handle(EntityUpdated<TeamEntity> notification, CancellationToken cancellationToken)
        {
            await base.HandleCreateOrUpdate(
                notification.Entity,
                MainHubMethods.TeamUpdated,
                notification.ModifiedProperties.Select(x => x.TitleCaseToCamelCase()).ToArray(),
                cancellationToken);
        }
    }

    public class TeamDeletedSignalRHandler : TeamHandler, INotificationHandler<EntityDeleted<TeamEntity>>
    {
        public TeamDeletedSignalRHandler(
            BlueprintContext db,
            IMapper mapper,
            ITeamService teamService,
            IHubBroadcaster broadcaster) : base(db, mapper, teamService, broadcaster)
        {
        }

        public Task Handle(EntityDeleted<TeamEntity> notification, CancellationToken cancellationToken)
        {
            var groupIds = base.GetGroups(notification.Entity);
            _broadcaster.Broadcast(groupIds, MainHubMethods.TeamDeleted, notification.Entity.Id);
            return Task.CompletedTask;
        }
    }
}
