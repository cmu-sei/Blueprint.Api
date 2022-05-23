// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
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
    public class TeamHandler
    {
        protected readonly BlueprintContext _db;
        protected readonly IMapper _mapper;
        protected readonly ITeamService _TeamService;
        protected readonly IHubContext<MainHub> _mainHub;

        public TeamHandler(
            BlueprintContext db,
            IMapper mapper,
            ITeamService TeamService,
            IHubContext<MainHub> mainHub)
        {
            _db = db;
            _mapper = mapper;
            _TeamService = TeamService;
            _mainHub = mainHub;
        }

        protected string[] GetGroups(TeamEntity teamEntity)
        {
            var groupIds = new List<string>();
            // add the team
            groupIds.Add(teamEntity.Id.ToString());
            // the admin data group gets everything
            groupIds.Add(MainHub.ADMIN_DATA_GROUP);

            return groupIds.ToArray();
        }

        protected async Task HandleCreateOrUpdate(
            TeamEntity teamEntity,
            string method,
            string[] modifiedProperties,
            CancellationToken cancellationToken)
        {
            var groupIds = GetGroups(teamEntity);
            var team = _mapper.Map<ViewModels.Team>(teamEntity);
            var tasks = new List<Task>();

            foreach (var groupId in groupIds)
            {
                tasks.Add(_mainHub.Clients.Group(groupId).SendAsync(method, team, modifiedProperties, cancellationToken));
            }

            await Task.WhenAll(tasks);
        }
    }

    public class TeamCreatedSignalRHandler : TeamHandler, INotificationHandler<EntityCreated<TeamEntity>>
    {
        public TeamCreatedSignalRHandler(
            BlueprintContext db,
            IMapper mapper,
            ITeamService teamService,
            IHubContext<MainHub> mainHub) : base(db, mapper, teamService, mainHub) { }

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
            IHubContext<MainHub> mainHub) : base(db, mapper, teamService, mainHub) { }

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
            IHubContext<MainHub> mainHub) : base(db, mapper, teamService, mainHub)
        {
        }

        public async Task Handle(EntityDeleted<TeamEntity> notification, CancellationToken cancellationToken)
        {
            var groupIds = base.GetGroups(notification.Entity);
            var tasks = new List<Task>();

            foreach (var groupId in groupIds)
            {
                tasks.Add(_mainHub.Clients.Group(groupId).SendAsync(MainHubMethods.TeamDeleted, notification.Entity.Id, cancellationToken));
            }

            await Task.WhenAll(tasks);
        }
    }
}
