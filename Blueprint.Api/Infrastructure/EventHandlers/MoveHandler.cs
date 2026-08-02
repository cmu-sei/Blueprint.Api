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
    public class MoveHandler
    {
        protected readonly BlueprintContext _db;
        protected readonly IMapper _mapper;
        protected readonly IMoveService _moveService;
        protected readonly IHubBroadcaster _broadcaster;

        public MoveHandler(
            BlueprintContext db,
            IMapper mapper,
            IMoveService moveService,
            IHubBroadcaster broadcaster)
        {
            _db = db;
            _mapper = mapper;
            _moveService = moveService;
            _broadcaster = broadcaster;
        }

        protected string[] GetGroups(MoveEntity moveEntity)
        {
            var groupIds = new List<string>();
            groupIds.Add(moveEntity.MselId.ToString());
            // the admin data group gets everything
            groupIds.Add(MainHub.ADMIN_DATA_GROUP);

            return groupIds.ToArray();
        }

        protected Task HandleCreateOrUpdate(
            MoveEntity moveEntity,
            string method,
            string[] modifiedProperties,
            CancellationToken cancellationToken)
        {
            var groupIds = GetGroups(moveEntity);
            var move = _mapper.Map<ViewModels.Move>(moveEntity);
            _broadcaster.Broadcast(groupIds, method, move, modifiedProperties);
            return Task.CompletedTask;
        }
    }

    public class MoveCreatedSignalRHandler : MoveHandler, INotificationHandler<EntityCreated<MoveEntity>>
    {
        public MoveCreatedSignalRHandler(
            BlueprintContext db,
            IMapper mapper,
            IMoveService moveService,
            IHubBroadcaster broadcaster) : base(db, mapper, moveService, broadcaster) { }

        public async Task Handle(EntityCreated<MoveEntity> notification, CancellationToken cancellationToken)
        {
            await base.HandleCreateOrUpdate(notification.Entity, MainHubMethods.MoveCreated, null, cancellationToken);
        }
    }

    public class MoveUpdatedSignalRHandler : MoveHandler, INotificationHandler<EntityUpdated<MoveEntity>>
    {
        public MoveUpdatedSignalRHandler(
            BlueprintContext db,
            IMapper mapper,
            IMoveService moveService,
            IHubBroadcaster broadcaster) : base(db, mapper, moveService, broadcaster) { }

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
            IMapper mapper,
            IMoveService moveService,
            IHubBroadcaster broadcaster) : base(db, mapper, moveService, broadcaster)
        {
        }

        public Task Handle(EntityDeleted<MoveEntity> notification, CancellationToken cancellationToken)
        {
            var groupIds = base.GetGroups(notification.Entity);
            _broadcaster.Broadcast(groupIds, MainHubMethods.MoveDeleted, notification.Entity.Id);
            return Task.CompletedTask;
        }
    }
}
