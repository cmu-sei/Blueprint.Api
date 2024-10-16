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
    public class CiteRoleHandler
    {
        protected readonly BlueprintContext _db;
        protected readonly IMapper _mapper;
        protected readonly ICiteRoleService _citeRoleService;
        protected readonly IHubContext<MainHub> _mainHub;

        public CiteRoleHandler(
            BlueprintContext db,
            IMapper mapper,
            ICiteRoleService citeRoleService,
            IHubContext<MainHub> mainHub)
        {
            _db = db;
            _mapper = mapper;
            _citeRoleService = citeRoleService;
            _mainHub = mainHub;
        }

        protected string[] GetGroups(CiteRoleEntity citeRoleEntity)
        {
            var groupIds = new List<string>();
            groupIds.Add(citeRoleEntity.MselId.ToString());
            // the admin data group gets everything
            groupIds.Add(MainHub.ADMIN_DATA_GROUP);

            return groupIds.ToArray();
        }

        protected async Task HandleCreateOrUpdate(
            CiteRoleEntity citeRoleEntity,
            string method,
            string[] modifiedProperties,
            CancellationToken cancellationToken)
        {
            var groupIds = GetGroups(citeRoleEntity);
            citeRoleEntity = await _db.CiteRoles
                .Include(cr => cr.Team)
                .SingleOrDefaultAsync(cr => cr.Id == citeRoleEntity.Id, cancellationToken);
            citeRoleEntity.Msel = null;
            var CiteRole = _mapper.Map<ViewModels.CiteRole>(citeRoleEntity);
            var tasks = new List<Task>();

            foreach (var groupId in groupIds)
            {
                tasks.Add(_mainHub.Clients.Group(groupId).SendAsync(method, CiteRole, modifiedProperties, cancellationToken));
            }

            await Task.WhenAll(tasks);
        }
    }

    public class CiteRoleCreatedSignalRHandler : CiteRoleHandler, INotificationHandler<EntityCreated<CiteRoleEntity>>
    {
        public CiteRoleCreatedSignalRHandler(
            BlueprintContext db,
            IMapper mapper,
            ICiteRoleService citeRoleService,
            IHubContext<MainHub> mainHub) : base(db, mapper, citeRoleService, mainHub) { }

        public async Task Handle(EntityCreated<CiteRoleEntity> notification, CancellationToken cancellationToken)
        {
            await base.HandleCreateOrUpdate(notification.Entity, MainHubMethods.CiteRoleCreated, null, cancellationToken);
        }
    }

    public class CiteRoleUpdatedSignalRHandler : CiteRoleHandler, INotificationHandler<EntityUpdated<CiteRoleEntity>>
    {
        public CiteRoleUpdatedSignalRHandler(
            BlueprintContext db,
            IMapper mapper,
            ICiteRoleService citeRoleService,
            IHubContext<MainHub> mainHub) : base(db, mapper, citeRoleService, mainHub) { }

        public async Task Handle(EntityUpdated<CiteRoleEntity> notification, CancellationToken cancellationToken)
        {
            await base.HandleCreateOrUpdate(
                notification.Entity,
                MainHubMethods.CiteRoleUpdated,
                notification.ModifiedProperties.Select(x => x.TitleCaseToCamelCase()).ToArray(),
                cancellationToken);
        }
    }

    public class CiteRoleDeletedSignalRHandler : CiteRoleHandler, INotificationHandler<EntityDeleted<CiteRoleEntity>>
    {
        public CiteRoleDeletedSignalRHandler(
            BlueprintContext db,
            IMapper mapper,
            ICiteRoleService citeRoleService,
            IHubContext<MainHub> mainHub) : base(db, mapper, citeRoleService, mainHub)
        {
        }

        public async Task Handle(EntityDeleted<CiteRoleEntity> notification, CancellationToken cancellationToken)
        {
            var groupIds = base.GetGroups(notification.Entity);
            var tasks = new List<Task>();

            foreach (var groupId in groupIds)
            {
                tasks.Add(_mainHub.Clients.Group(groupId).SendAsync(MainHubMethods.CiteRoleDeleted, notification.Entity.Id, cancellationToken));
            }

            await Task.WhenAll(tasks);
        }
    }
}
