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
    public class CardTeamHandler
    {
        protected readonly BlueprintContext _db;
        protected readonly IMapper _mapper;
        protected readonly ICardTeamService _CardTeamService;
        protected readonly IHubContext<MainHub> _mainHub;

        public CardTeamHandler(
            BlueprintContext db,
            IMapper mapper,
            ICardTeamService CardTeamService,
            IHubContext<MainHub> mainHub)
        {
            _db = db;
            _mapper = mapper;
            _CardTeamService = CardTeamService;
            _mainHub = mainHub;
        }

        protected async Task<string[]> GetGroups(CardTeamEntity cardTeamEntity)
        {
            var groupIds = new List<string>();
            var mselId = await _db.Cards
                .Where(c => c.Id == cardTeamEntity.CardId)
                .Select(c => c.MselId)
                .SingleOrDefaultAsync();
            groupIds.Add(mselId.ToString());
            // the admin data group gets everything
            groupIds.Add(MainHub.ADMIN_DATA_GROUP);

            return groupIds.ToArray();
        }

        protected async Task HandleCreateOrUpdate(
            CardTeamEntity CardTeamEntity,
            string method,
            string[] modifiedProperties,
            CancellationToken cancellationToken)
        {
            var groupIds = await GetGroups(CardTeamEntity);
            var CardTeam = _mapper.Map<ViewModels.CardTeam>(CardTeamEntity);
            var tasks = new List<Task>();

            foreach (var groupId in groupIds)
            {
                tasks.Add(_mainHub.Clients.Group(groupId).SendAsync(method, CardTeam, modifiedProperties, cancellationToken));
            }

            await Task.WhenAll(tasks);
        }
    }

    public class CardTeamCreatedSignalRHandler : CardTeamHandler, INotificationHandler<EntityCreated<CardTeamEntity>>
    {
        public CardTeamCreatedSignalRHandler(
            BlueprintContext db,
            IMapper mapper,
            ICardTeamService CardTeamService,
            IHubContext<MainHub> mainHub) : base(db, mapper, CardTeamService, mainHub) { }

        public async Task Handle(EntityCreated<CardTeamEntity> notification, CancellationToken cancellationToken)
        {
            await base.HandleCreateOrUpdate(notification.Entity, MainHubMethods.CardTeamCreated, null, cancellationToken);
        }
    }

    public class CardTeamUpdatedSignalRHandler : CardTeamHandler, INotificationHandler<EntityUpdated<CardTeamEntity>>
    {
        public CardTeamUpdatedSignalRHandler(
            BlueprintContext db,
            IMapper mapper,
            ICardTeamService CardTeamService,
            IHubContext<MainHub> mainHub) : base(db, mapper, CardTeamService, mainHub) { }

        public async Task Handle(EntityUpdated<CardTeamEntity> notification, CancellationToken cancellationToken)
        {
            await base.HandleCreateOrUpdate(
                notification.Entity,
                MainHubMethods.CardTeamUpdated,
                notification.ModifiedProperties.Select(x => x.TitleCaseToCamelCase()).ToArray(),
                cancellationToken);
        }
    }

    public class CardTeamDeletedSignalRHandler : CardTeamHandler, INotificationHandler<EntityDeleted<CardTeamEntity>>
    {
        public CardTeamDeletedSignalRHandler(
            BlueprintContext db,
            IMapper mapper,
            ICardTeamService CardTeamService,
            IHubContext<MainHub> mainHub) : base(db, mapper, CardTeamService, mainHub)
        {
        }

        public async Task Handle(EntityDeleted<CardTeamEntity> notification, CancellationToken cancellationToken)
        {
            var groupIds = await base.GetGroups(notification.Entity);
            var tasks = new List<Task>();

            foreach (var groupId in groupIds)
            {
                tasks.Add(_mainHub.Clients.Group(groupId).SendAsync(MainHubMethods.CardTeamDeleted, notification.Entity.Id, cancellationToken));
            }

            await Task.WhenAll(tasks);
        }
    }
}
