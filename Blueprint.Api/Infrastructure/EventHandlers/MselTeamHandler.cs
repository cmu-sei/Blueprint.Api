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

namespace Blueprint.Api.Infrastructure.EventHandlers
{
    public class MselTeamHandler
    {
        protected readonly BlueprintContext _db;
        protected readonly IMapper _mapper;
        protected readonly IMselService _mselService;
        protected readonly IHubContext<MainHub> _mainHub;

        public MselTeamHandler(
            BlueprintContext db,
            IMapper mapper,
            IMselService mselService,
            IHubContext<MainHub> mainHub)
        {
            _db = db;
            _mapper = mapper;
            _mselService = mselService;
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

        protected async Task HandleCreateOrDelete(
            MselTeamEntity mselTeamEntity,
            string method,
            string[] modifiedProperties,
            CancellationToken cancellationToken)
        {
            var groupIds = GetGroups(mselTeamEntity);
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
            IMselService mselService,
            IHubContext<MainHub> mainHub) : base(db, mapper, mselService, mainHub) { }

        public async Task Handle(EntityCreated<MselTeamEntity> notification, CancellationToken cancellationToken)
        {
            await base.HandleCreateOrDelete(notification.Entity, MainHubMethods.MselTeamCreated, null, cancellationToken);
        }
    }

    public class MselTeamDeletedSignalRHandler : MselTeamHandler, INotificationHandler<EntityDeleted<MselTeamEntity>>
    {
        public MselTeamDeletedSignalRHandler(
            BlueprintContext db,
            IMapper mapper,
            IMselService mselService,
            IHubContext<MainHub> mainHub) : base(db, mapper, mselService, mainHub)
        {
        }

        public async Task Handle(EntityDeleted<MselTeamEntity> notification, CancellationToken cancellationToken)
        {
            await base.HandleCreateOrDelete(notification.Entity, MainHubMethods.MselTeamDeleted, null, cancellationToken);
        }
    }
}
