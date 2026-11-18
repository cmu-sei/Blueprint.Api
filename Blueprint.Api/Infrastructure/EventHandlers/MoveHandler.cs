// Copyright 2023 Carnegie Mellon University. All Rights Reserved.
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
using Crucible.Common.EntityEvents.Events;
using Blueprint.Api.Infrastructure.Mappings;

namespace Blueprint.Api.Infrastructure.EventHandlers
{
    public class MoveHandler
    {
        protected readonly BlueprintContext _db;
        protected readonly IMoveService _moveService;
        protected readonly IHubContext<MainHub> _mainHub;

        public MoveHandler(
            BlueprintContext db,
            IMoveService moveService,
            IHubContext<MainHub> mainHub)
        {
            _db = db;
            _moveService = moveService;
            _mainHub = mainHub;
        }

        protected string[] GetGroups(MoveEntity moveEntity)
        {
            var groupIds = new List<string>();
            groupIds.Add(moveEntity.MselId.ToString());
            // the admin data group gets everything
            groupIds.Add(MainHub.ADMIN_DATA_GROUP);

            return groupIds.ToArray();
        }

        protected async Task HandleCreateOrUpdate(
            MoveEntity moveEntity,
            string method,
            string[] modifiedProperties,
            CancellationToken cancellationToken)
        {
            var groupIds = GetGroups(moveEntity);
            var move = moveEntity.ToMove();
            var tasks = new List<Task>();

            foreach (var groupId in groupIds)
            {
                tasks.Add(_mainHub.Clients.Group(groupId).SendAsync(method, move, modifiedProperties, cancellationToken));
            }

            await Task.WhenAll(tasks);
        }
    }

    public class MoveCreatedSignalRHandler : MoveHandler, INotificationHandler<EntityCreated<MoveEntity>>
    {
        public MoveCreatedSignalRHandler(
            BlueprintContext db,
            IMoveService moveService,
            IHubContext<MainHub> mainHub) : base(db, moveService, mainHub) { }

        public async Task Handle(EntityCreated<MoveEntity> notification, CancellationToken cancellationToken)
        {
            await base.HandleCreateOrUpdate(notification.Entity, MainHubMethods.MoveCreated, null, cancellationToken);
        }
    }

    public class MoveUpdatedSignalRHandler : MoveHandler, INotificationHandler<EntityUpdated<MoveEntity>>
    {
        public MoveUpdatedSignalRHandler(
            BlueprintContext db,
            IMoveService moveService,
            IHubContext<MainHub> mainHub) : base(db, moveService, mainHub) { }

        public async Task Handle(EntityUpdated<MoveEntity> notification, CancellationToken cancellationToken)
        {
            await base.HandleCreateOrUpdate(
                notification.Entity,
                MainHubMethods.MoveUpdated,
                notification.ModifiedProperties.Select(x => x.TitleCaseToCamelCase()).ToArray(),
                cancellationToken);
        }
    }

    public class MoveDeletedSignalRHandler : MoveHandler, INotificationHandler<EntityDeleted<MoveEntity>>
    {
        public MoveDeletedSignalRHandler(
            BlueprintContext db,
            IMoveService moveService,
            IHubContext<MainHub> mainHub) : base(db, moveService, mainHub)
        {
        }

        public async Task Handle(EntityDeleted<MoveEntity> notification, CancellationToken cancellationToken)
        {
            var groupIds = base.GetGroups(notification.Entity);
            var tasks = new List<Task>();

            foreach (var groupId in groupIds)
            {
                tasks.Add(_mainHub.Clients.Group(groupId).SendAsync(MainHubMethods.MoveDeleted, notification.Entity.Id, cancellationToken));
            }

            await Task.WhenAll(tasks);
        }
    }
}
