// Copyright 2023 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AutoMapper;
using MediatR;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Blueprint.Api.Data;
using Blueprint.Api.Data.Models;
using Blueprint.Api.Services;
using Blueprint.Api.Hubs;
using Blueprint.Api.Infrastructure.Extensions;

namespace Blueprint.Api.Infrastructure.EventHandlers
{
    public class MselTeamHandler
    {
        protected readonly BlueprintContext _db;
        protected readonly IMapper _mapper;
        protected readonly IMselTeamService _mselTeamService;
        protected readonly IHubContext<MainHub> _mainHub;

        public MselTeamHandler(
            BlueprintContext db,
            IMapper mapper,
            IMselTeamService mselTeamService,
            IHubContext<MainHub> mainHub)
        {
            _db = db;
            _mapper = mapper;
            _mselTeamService = mselTeamService;
            _mainHub = mainHub;
        }

        protected string[] GetGroups(MselTeamEntity mselTeamEntity)
        {
            var groupIds = new List<string>();
            groupIds.Add(mselTeamEntity.MselId.ToString());
            // the admin data group gets everything
            groupIds.Add(MainHub.ADMIN_DATA_GROUP);

            return groupIds.ToArray();
        }

        protected async Task HandleCreateOrUpdate(
            MselTeamEntity mselTeamEntity,
            string method,
            string[] modifiedProperties,
            CancellationToken cancellationToken)
        {
            var groupIds = GetGroups(mselTeamEntity);
            mselTeamEntity = await _db.MselTeams
                .Include(mt => mt.Team)
                .ThenInclude(t => t.TeamUsers)
                .ThenInclude(tu => tu.User)
                .SingleOrDefaultAsync(o => o.Id == mselTeamEntity.Id, cancellationToken);
            var mselTeam = _mapper.Map<ViewModels.MselTeam>(mselTeamEntity);
            var tasks = new List<Task>();

            foreach (var groupId in groupIds)
            {
                tasks.Add(_mainHub.Clients.Group(groupId).SendAsync(method, mselTeam, modifiedProperties, cancellationToken));
            }

            await Task.WhenAll(tasks);
        }
    }

    public class MselTeamCreatedSignalRHandler : MselTeamHandler, INotificationHandler<EntityCreated<MselTeamEntity>>
    {
        public MselTeamCreatedSignalRHandler(
            BlueprintContext db,
            IMapper mapper,
            IMselTeamService mselTeamService,
            IHubContext<MainHub> mainHub) : base(db, mapper, mselTeamService, mainHub) { }

        public async Task Handle(EntityCreated<MselTeamEntity> notification, CancellationToken cancellationToken)
        {
            await base.HandleCreateOrUpdate(notification.Entity, MainHubMethods.MselTeamCreated, null, cancellationToken);
        }
    }

    public class MselTeamUpdatedSignalRHandler : MselTeamHandler, INotificationHandler<EntityUpdated<MselTeamEntity>>
    {
        public MselTeamUpdatedSignalRHandler(
            BlueprintContext db,
            IMapper mapper,
            IMselTeamService mselTeamService,
            IHubContext<MainHub> mainHub) : base(db, mapper, mselTeamService, mainHub) { }

        public async Task Handle(EntityUpdated<MselTeamEntity> notification, CancellationToken cancellationToken)
        {
            await base.HandleCreateOrUpdate(
                notification.Entity,
                MainHubMethods.MselTeamUpdated,
                notification.ModifiedProperties.Select(x => x.TitleCaseToCamelCase()).ToArray(),
                cancellationToken);
        }
    }

    public class MselTeamDeletedSignalRHandler : MselTeamHandler, INotificationHandler<EntityDeleted<MselTeamEntity>>
    {
        public MselTeamDeletedSignalRHandler(
            BlueprintContext db,
            IMapper mapper,
            IMselTeamService mselTeamService,
            IHubContext<MainHub> mainHub) : base(db, mapper, mselTeamService, mainHub)
        {
        }

        public async Task Handle(EntityDeleted<MselTeamEntity> notification, CancellationToken cancellationToken)
        {
            var groupIds = base.GetGroups(notification.Entity);
            var tasks = new List<Task>();

            foreach (var groupId in groupIds)
            {
                tasks.Add(_mainHub.Clients.Group(groupId).SendAsync(MainHubMethods.MselTeamDeleted, notification.Entity.Id, cancellationToken));
            }

            await Task.WhenAll(tasks);
        }
    }
}
