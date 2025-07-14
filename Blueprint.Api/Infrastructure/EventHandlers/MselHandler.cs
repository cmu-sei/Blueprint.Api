// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AutoMapper;
using MediatR;
using Microsoft.AspNetCore.SignalR;
using Blueprint.Api.Data;
using Blueprint.Api.Data.Enumerations;
using Blueprint.Api.Data.Models;
using Blueprint.Api.Services;
using Blueprint.Api.Hubs;
using Blueprint.Api.Infrastructure.Extensions;

namespace Blueprint.Api.Infrastructure.EventHandlers
{
    public class BaseMselHandler
    {
        protected readonly BlueprintContext _db;
        protected readonly IMapper _mapper;
        protected readonly IMselService _mselService;
        protected readonly IHubContext<MainHub> _mainHub;

        public BaseMselHandler(
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

        protected string[] GetGroups(MselEntity mselEntity)
        {
            var groupIds = new List<string>();
            groupIds.Add(mselEntity.Id.ToString());
            // the admin data group gets everything
            groupIds.Add(MainHub.ADMIN_DATA_GROUP);

            return groupIds.ToArray();
        }

        protected async Task HandleCreateOrUpdate(
            MselEntity mselEntity,
            string method,
            string[] modifiedProperties,
            CancellationToken cancellationToken)
        {
            var groupIds = GetGroups(mselEntity);
            var msel = await _mselService.GetAsync(mselEntity.Id, cancellationToken);
            if (msel != null)
            {
                if (msel.UseGallery)
                {
                    msel.GalleryArticleParameters = Enum.GetNames(typeof(GalleryArticleParameter)).ToList();
                    msel.GallerySourceTypes = Enum.GetNames(typeof(GallerySourceType)).ToList();
                }
                var tasks = new List<Task>();

                foreach (var groupId in groupIds)
                {
                    tasks.Add(_mainHub.Clients.Group(groupId).SendAsync(method, msel, modifiedProperties, cancellationToken));
                }

                await Task.WhenAll(tasks);
            }
        }
    }

    public class MselCreatedSignalRHandler : BaseMselHandler, INotificationHandler<EntityCreated<MselEntity>>
    {
        public MselCreatedSignalRHandler(
            BlueprintContext db,
            IMapper mapper,
            IMselService mselService,
            IHubContext<MainHub> mainHub) : base(db, mapper, mselService, mainHub) { }

        public async Task Handle(EntityCreated<MselEntity> notification, CancellationToken cancellationToken)
        {
            await base.HandleCreateOrUpdate(notification.Entity, MainHubMethods.MselCreated, null, cancellationToken);
        }
    }

    public class MselUpdatedSignalRHandler : BaseMselHandler, INotificationHandler<EntityUpdated<MselEntity>>
    {
        public MselUpdatedSignalRHandler(
            BlueprintContext db,
            IMapper mapper,
            IMselService mselService,
            IHubContext<MainHub> mainHub) : base(db, mapper, mselService, mainHub) { }

        public async Task Handle(EntityUpdated<MselEntity> notification, CancellationToken cancellationToken)
        {
            await base.HandleCreateOrUpdate(
                notification.Entity,
                MainHubMethods.MselUpdated,
                notification.ModifiedProperties.Select(x => x.TitleCaseToCamelCase()).ToArray(),
                cancellationToken);
        }
    }

    public class MselDeletedSignalRHandler : BaseMselHandler, INotificationHandler<EntityDeleted<MselEntity>>
    {
        public MselDeletedSignalRHandler(
            BlueprintContext db,
            IMapper mapper,
            IMselService mselService,
            IHubContext<MainHub> mainHub) : base(db, mapper, mselService, mainHub)
        {
        }

        public async Task Handle(EntityDeleted<MselEntity> notification, CancellationToken cancellationToken)
        {
            var groupIds = base.GetGroups(notification.Entity);
            var tasks = new List<Task>();

            foreach (var groupId in groupIds)
            {
                tasks.Add(_mainHub.Clients.Group(groupId).SendAsync(MainHubMethods.MselDeleted, notification.Entity.Id, cancellationToken));
            }

            await Task.WhenAll(tasks);
        }
    }
}
