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
    public class OrganizationHandler
    {
        protected readonly BlueprintContext _db;
        protected readonly IMapper _mapper;
        protected readonly IOrganizationService _organizationService;
        protected readonly IHubBroadcaster _broadcaster;

        public OrganizationHandler(
            BlueprintContext db,
            IMapper mapper,
            IOrganizationService organizationService,
            IHubBroadcaster broadcaster)
        {
            _db = db;
            _mapper = mapper;
            _organizationService = organizationService;
            _broadcaster = broadcaster;
        }

        protected string[] GetGroups(OrganizationEntity organizationEntity)
        {
            var groupIds = new List<string>();
            groupIds.Add(organizationEntity.MselId.ToString());
            // the admin data group gets everything
            groupIds.Add(MainHub.ADMIN_DATA_GROUP);

            return groupIds.ToArray();
        }

        protected Task HandleCreateOrUpdate(
            OrganizationEntity organizationEntity,
            string method,
            string[] modifiedProperties,
            CancellationToken cancellationToken)
        {
            var groupIds = GetGroups(organizationEntity);
            var organization = _mapper.Map<ViewModels.Organization>(organizationEntity);
            _broadcaster.Broadcast(groupIds, method, organization, modifiedProperties);
            return Task.CompletedTask;
        }
    }

    public class OrganizationCreatedSignalRHandler : OrganizationHandler, INotificationHandler<EntityCreated<OrganizationEntity>>
    {
        public OrganizationCreatedSignalRHandler(
            BlueprintContext db,
            IMapper mapper,
            IOrganizationService organizationService,
            IHubBroadcaster broadcaster) : base(db, mapper, organizationService, broadcaster) { }

        public async Task Handle(EntityCreated<OrganizationEntity> notification, CancellationToken cancellationToken)
        {
            await base.HandleCreateOrUpdate(notification.Entity, MainHubMethods.OrganizationCreated, null, cancellationToken);
        }
    }

    public class OrganizationUpdatedSignalRHandler : OrganizationHandler, INotificationHandler<EntityUpdated<OrganizationEntity>>
    {
        public OrganizationUpdatedSignalRHandler(
            BlueprintContext db,
            IMapper mapper,
            IOrganizationService organizationService,
            IHubBroadcaster broadcaster) : base(db, mapper, organizationService, broadcaster) { }

        public async Task Handle(EntityUpdated<OrganizationEntity> notification, CancellationToken cancellationToken)
        {
            await base.HandleCreateOrUpdate(
                notification.Entity,
                MainHubMethods.OrganizationUpdated,
                notification.ModifiedProperties.Select(x => x.TitleCaseToCamelCase()).ToArray(),
                cancellationToken);
        }
    }

    public class OrganizationDeletedSignalRHandler : OrganizationHandler, INotificationHandler<EntityDeleted<OrganizationEntity>>
    {
        public OrganizationDeletedSignalRHandler(
            BlueprintContext db,
            IMapper mapper,
            IOrganizationService organizationService,
            IHubBroadcaster broadcaster) : base(db, mapper, organizationService, broadcaster)
        {
        }

        public Task Handle(EntityDeleted<OrganizationEntity> notification, CancellationToken cancellationToken)
        {
            var groupIds = base.GetGroups(notification.Entity);
            _broadcaster.Broadcast(groupIds, MainHubMethods.OrganizationDeleted, notification.Entity.Id);
            return Task.CompletedTask;
        }
    }
}
