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
    public class CardHandler
    {
        protected readonly BlueprintContext _db;
        protected readonly IMapper _mapper;
        protected readonly ICardService _CardService;
        protected readonly IHubBroadcaster _broadcaster;

        public CardHandler(
            BlueprintContext db,
            IMapper mapper,
            ICardService CardService,
            IHubBroadcaster broadcaster)
        {
            _db = db;
            _mapper = mapper;
            _CardService = CardService;
            _broadcaster = broadcaster;
        }

        protected string[] GetGroups(CardEntity CardEntity)
        {
            var groupIds = new List<string>();
            groupIds.Add(CardEntity.MselId.ToString());
            // the admin data group gets everything
            groupIds.Add(MainHub.ADMIN_DATA_GROUP);

            return groupIds.ToArray();
        }

        protected Task HandleCreateOrUpdate(
            CardEntity CardEntity,
            string method,
            string[] modifiedProperties,
            CancellationToken cancellationToken)
        {
            var groupIds = GetGroups(CardEntity);
            var Card = _mapper.Map<ViewModels.Card>(CardEntity);
            _broadcaster.Broadcast(groupIds, method, Card, modifiedProperties);
            return Task.CompletedTask;
        }
    }

    public class CardCreatedSignalRHandler : CardHandler, INotificationHandler<EntityCreated<CardEntity>>
    {
        public CardCreatedSignalRHandler(
            BlueprintContext db,
            IMapper mapper,
            ICardService CardService,
            IHubBroadcaster broadcaster) : base(db, mapper, CardService, broadcaster) { }

        public async Task Handle(EntityCreated<CardEntity> notification, CancellationToken cancellationToken)
        {
            await base.HandleCreateOrUpdate(notification.Entity, MainHubMethods.CardCreated, null, cancellationToken);
        }
    }

    public class CardUpdatedSignalRHandler : CardHandler, INotificationHandler<EntityUpdated<CardEntity>>
    {
        public CardUpdatedSignalRHandler(
            BlueprintContext db,
            IMapper mapper,
            ICardService CardService,
            IHubBroadcaster broadcaster) : base(db, mapper, CardService, broadcaster) { }

        public async Task Handle(EntityUpdated<CardEntity> notification, CancellationToken cancellationToken)
        {
            await base.HandleCreateOrUpdate(
                notification.Entity,
                MainHubMethods.CardUpdated,
                notification.ModifiedProperties.Select(x => x.TitleCaseToCamelCase()).ToArray(),
                cancellationToken);
        }
    }

    public class CardDeletedSignalRHandler : CardHandler, INotificationHandler<EntityDeleted<CardEntity>>
    {
        public CardDeletedSignalRHandler(
            BlueprintContext db,
            IMapper mapper,
            ICardService CardService,
            IHubBroadcaster broadcaster) : base(db, mapper, CardService, broadcaster)
        {
        }

        public Task Handle(EntityDeleted<CardEntity> notification, CancellationToken cancellationToken)
        {
            var groupIds = base.GetGroups(notification.Entity);
            _broadcaster.Broadcast(groupIds, MainHubMethods.CardDeleted, notification.Entity.Id);
            return Task.CompletedTask;
        }
    }
}
