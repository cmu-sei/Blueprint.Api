// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediatR;
using Microsoft.AspNetCore.SignalR;
using Blueprint.Api.Data;
using Blueprint.Api.Data.Models;
using Blueprint.Api.Services;
using Blueprint.Api.Hubs;
using Blueprint.Api.Infrastructure.Extensions;
using Microsoft.EntityFrameworkCore;
using Crucible.Common.EntityEvents.Events;
using Blueprint.Api.Infrastructure.Mappings;

namespace Blueprint.Api.Infrastructure.EventHandlers
{
    public class TeamUserHandler
    {
        protected readonly BlueprintContext _db;
        protected readonly ITeamUserService _TeamUserService;
        protected readonly IHubContext<MainHub> _mainHub;

        public TeamUserHandler(
            BlueprintContext db,
            ITeamUserService TeamUserService,
            IHubContext<MainHub> mainHub)
        {
            _db = db;
            _TeamUserService = TeamUserService;
            _mainHub = mainHub;
        }

        protected string[] GetGroups(TeamUserEntity teamUserEntity)
        {
            var groupIds = new List<string>();
            // add the team
            groupIds.Add(teamUserEntity.TeamId.ToString());
            // add the user
            groupIds.Add(teamUserEntity.UserId.ToString());
            // the admin data group gets everything
            groupIds.Add(MainHub.ADMIN_DATA_GROUP);

            return groupIds.ToArray();
        }

        protected async Task HandleCreateOrUpdate(
            TeamUserEntity teamUserEntity,
            string method,
            string[] modifiedProperties,
            CancellationToken cancellationToken)
        {
            var groupIds = GetGroups(teamUserEntity);
            // the view model, not the entity: the entity's navigations reach whatever the request has loaded, a drafted MSEL
            // with its teams' competency trees, which serializes past SignalR's 2 GB message limit and drops the connection
            var teamUser = teamUserEntity.ToTeamUser();
            var tasks = new List<Task>();

            foreach (var groupId in groupIds)
            {
                tasks.Add(_mainHub.Clients.Group(groupId).SendAsync(method, teamUser, modifiedProperties, cancellationToken));
            }

            await Task.WhenAll(tasks);
        }
    }

    public class TeamUserCreatedSignalRHandler : TeamUserHandler, INotificationHandler<EntityCreated<TeamUserEntity>>
    {
        public TeamUserCreatedSignalRHandler(
            BlueprintContext db,
            ITeamUserService teamUserService,
            IHubContext<MainHub> mainHub) : base(db, teamUserService, mainHub) { }

        public async Task Handle(EntityCreated<TeamUserEntity> notification, CancellationToken cancellationToken)
        {
            await base.HandleCreateOrUpdate(notification.Entity, MainHubMethods.TeamUserCreated, null, cancellationToken);
        }
    }

    public class TeamUserUpdatedSignalRHandler : TeamUserHandler, INotificationHandler<EntityUpdated<TeamUserEntity>>
    {
        public TeamUserUpdatedSignalRHandler(
            BlueprintContext db,
            ITeamUserService teamUserService,
            IHubContext<MainHub> mainHub) : base(db, teamUserService, mainHub) { }

        public async Task Handle(EntityUpdated<TeamUserEntity> notification, CancellationToken cancellationToken)
        {
            await base.HandleCreateOrUpdate(
                notification.Entity,
                MainHubMethods.TeamUserUpdated,
                notification.ModifiedProperties.Select(x => x.TitleCaseToCamelCase()).ToArray(),
                cancellationToken);
        }
    }

    public class TeamUserDeletedSignalRHandler : TeamUserHandler, INotificationHandler<EntityDeleted<TeamUserEntity>>
    {
        public TeamUserDeletedSignalRHandler(
            BlueprintContext db,
            ITeamUserService teamUserService,
            IHubContext<MainHub> mainHub) : base(db, teamUserService, mainHub)
        {
        }

        public async Task Handle(EntityDeleted<TeamUserEntity> notification, CancellationToken cancellationToken)
        {
            var groupIds = base.GetGroups(notification.Entity);
            var tasks = new List<Task>();

            foreach (var groupId in groupIds)
            {
                tasks.Add(_mainHub.Clients.Group(groupId).SendAsync(MainHubMethods.TeamUserDeleted, notification.Entity.ToTeamUser(), cancellationToken));
            }

            await Task.WhenAll(tasks);
        }
    }
}
