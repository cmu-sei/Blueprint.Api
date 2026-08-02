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
    public class CiteDutyHandler
    {
        protected readonly BlueprintContext _db;
        protected readonly IMapper _mapper;
        protected readonly ICiteDutyService _citeDutyService;
        protected readonly IHubBroadcaster _broadcaster;

        public CiteDutyHandler(
            BlueprintContext db,
            IMapper mapper,
            ICiteDutyService citeDutyService,
            IHubBroadcaster broadcaster)
        {
            _db = db;
            _mapper = mapper;
            _citeDutyService = citeDutyService;
            _broadcaster = broadcaster;
        }

        protected string[] GetGroups(CiteDutyEntity citeDutyEntity)
        {
            var groupIds = new List<string>();
            groupIds.Add(citeDutyEntity.MselId.ToString());
            // the admin data group gets everything
            groupIds.Add(MainHub.ADMIN_DATA_GROUP);

            return groupIds.ToArray();
        }

        protected async Task HandleCreateOrUpdate(
            CiteDutyEntity citeDutyEntity,
            string method,
            string[] modifiedProperties,
            CancellationToken cancellationToken)
        {
            var groupIds = GetGroups(citeDutyEntity);
            citeDutyEntity = await _db.CiteDuties
                .Include(cr => cr.Team)
                .SingleOrDefaultAsync(cr => cr.Id == citeDutyEntity.Id, cancellationToken);
            citeDutyEntity.Msel = null;
            var citeDuty = _mapper.Map<ViewModels.CiteDuty>(citeDutyEntity);
            _broadcaster.Broadcast(groupIds, method, citeDuty, modifiedProperties);
        }
    }

    public class CiteDutyCreatedSignalRHandler : CiteDutyHandler, INotificationHandler<EntityCreated<CiteDutyEntity>>
    {
        public CiteDutyCreatedSignalRHandler(
            BlueprintContext db,
            IMapper mapper,
            ICiteDutyService citeDutyService,
            IHubBroadcaster broadcaster) : base(db, mapper, citeDutyService, broadcaster) { }

        public async Task Handle(EntityCreated<CiteDutyEntity> notification, CancellationToken cancellationToken)
        {
            await base.HandleCreateOrUpdate(notification.Entity, MainHubMethods.CiteDutyCreated, null, cancellationToken);
        }
    }

    public class CiteDutyUpdatedSignalRHandler : CiteDutyHandler, INotificationHandler<EntityUpdated<CiteDutyEntity>>
    {
        public CiteDutyUpdatedSignalRHandler(
            BlueprintContext db,
            IMapper mapper,
            ICiteDutyService citeDutyService,
            IHubBroadcaster broadcaster) : base(db, mapper, citeDutyService, broadcaster) { }

        public async Task Handle(EntityUpdated<CiteDutyEntity> notification, CancellationToken cancellationToken)
        {
            await base.HandleCreateOrUpdate(
                notification.Entity,
                MainHubMethods.CiteDutyUpdated,
                notification.ModifiedProperties.Select(x => x.TitleCaseToCamelCase()).ToArray(),
                cancellationToken);
        }
    }

    public class CiteDutyDeletedSignalRHandler : CiteDutyHandler, INotificationHandler<EntityDeleted<CiteDutyEntity>>
    {
        public CiteDutyDeletedSignalRHandler(
            BlueprintContext db,
            IMapper mapper,
            ICiteDutyService citeDutyService,
            IHubBroadcaster broadcaster) : base(db, mapper, citeDutyService, broadcaster)
        {
        }

        public Task Handle(EntityDeleted<CiteDutyEntity> notification, CancellationToken cancellationToken)
        {
            var groupIds = base.GetGroups(notification.Entity);
            _broadcaster.Broadcast(groupIds, MainHubMethods.CiteDutyDeleted, notification.Entity.Id);
            return Task.CompletedTask;
        }
    }
}
