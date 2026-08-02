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
    public class CiteActionHandler
    {
        protected readonly BlueprintContext _db;
        protected readonly IMapper _mapper;
        protected readonly ICiteActionService _CiteActionService;
        protected readonly IHubBroadcaster _broadcaster;

        public CiteActionHandler(
            BlueprintContext db,
            IMapper mapper,
            ICiteActionService CiteActionService,
            IHubBroadcaster broadcaster)
        {
            _db = db;
            _mapper = mapper;
            _CiteActionService = CiteActionService;
            _broadcaster = broadcaster;
        }

        protected string[] GetGroups(CiteActionEntity citeActionEntity)
        {
            var groupIds = new List<string>();
            groupIds.Add(citeActionEntity.MselId.ToString());
            // the admin data group gets everything
            groupIds.Add(MainHub.ADMIN_DATA_GROUP);

            return groupIds.ToArray();
        }

        protected async Task HandleCreateOrUpdate(
            CiteActionEntity citeActionEntity,
            string method,
            string[] modifiedProperties,
            CancellationToken cancellationToken)
        {
            var groupIds = GetGroups(citeActionEntity);
            citeActionEntity = await _db.CiteActions
                .Include(ca => ca.Team)
                .SingleOrDefaultAsync(ca => ca.Id == citeActionEntity.Id, cancellationToken);
            citeActionEntity.Msel = null;
            var CiteAction = _mapper.Map<ViewModels.CiteAction>(citeActionEntity);
            _broadcaster.Broadcast(groupIds, method, CiteAction, modifiedProperties);
        }
    }

    public class CiteActionCreatedSignalRHandler : CiteActionHandler, INotificationHandler<EntityCreated<CiteActionEntity>>
    {
        public CiteActionCreatedSignalRHandler(
            BlueprintContext db,
            IMapper mapper,
            ICiteActionService citeActionService,
            IHubBroadcaster broadcaster) : base(db, mapper, citeActionService, broadcaster) { }

        public async Task Handle(EntityCreated<CiteActionEntity> notification, CancellationToken cancellationToken)
        {
            await base.HandleCreateOrUpdate(notification.Entity, MainHubMethods.CiteActionCreated, null, cancellationToken);
        }
    }

    public class CiteActionUpdatedSignalRHandler : CiteActionHandler, INotificationHandler<EntityUpdated<CiteActionEntity>>
    {
        public CiteActionUpdatedSignalRHandler(
            BlueprintContext db,
            IMapper mapper,
            ICiteActionService citeActionService,
            IHubBroadcaster broadcaster) : base(db, mapper, citeActionService, broadcaster) { }

        public async Task Handle(EntityUpdated<CiteActionEntity> notification, CancellationToken cancellationToken)
        {
            await base.HandleCreateOrUpdate(
                notification.Entity,
                MainHubMethods.CiteActionUpdated,
                notification.ModifiedProperties.Select(x => x.TitleCaseToCamelCase()).ToArray(),
                cancellationToken);
        }
    }

    public class CiteActionDeletedSignalRHandler : CiteActionHandler, INotificationHandler<EntityDeleted<CiteActionEntity>>
    {
        public CiteActionDeletedSignalRHandler(
            BlueprintContext db,
            IMapper mapper,
            ICiteActionService citeActionService,
            IHubBroadcaster broadcaster) : base(db, mapper, citeActionService, broadcaster)
        {
        }

        public Task Handle(EntityDeleted<CiteActionEntity> notification, CancellationToken cancellationToken)
        {
            var groupIds = base.GetGroups(notification.Entity);
            _broadcaster.Broadcast(groupIds, MainHubMethods.CiteActionDeleted, notification.Entity.Id);
            return Task.CompletedTask;
        }
    }
}
