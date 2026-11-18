// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Blueprint.Api.Data;
using Blueprint.Api.Data.Models;
using Blueprint.Api.Hubs;
using Blueprint.Api.Infrastructure.Extensions;
using Blueprint.Api.Infrastructure.Mappings;
using Crucible.Common.EntityEvents.Events;
using MediatR;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace Blueprint.Api.Infrastructure.EventHandlers
{
    /// <summary>
    /// The broadcasts of automation tasks, runs and results. A task, secrets masked, goes to the MSEL's group, as its
    /// scenario events do. A run or a result goes to the MSEL's facilitation group only, since a result can hold what a
    /// guest printed; its output is cut to <see cref="BroadcastOutputLength"/> characters there, and read in full by id.
    /// </summary>
    public abstract class AutomationHandler
    {
        public const int BroadcastOutputLength = 4096;
        protected readonly BlueprintContext _db;
        protected readonly IHubContext<MainHub> _mainHub;

        protected AutomationHandler(BlueprintContext db, IHubContext<MainHub> mainHub)
        {
            _db = db;
            _mainHub = mainHub;
        }

        protected Task SendAsync(IEnumerable<string> groups, string method, object[] args, CancellationToken ct) =>
            Task.WhenAll(groups.Select(g => _mainHub.Clients.Group(g).SendCoreAsync(method, args, ct)));

        protected static string[] TaskGroups(Guid mselId) => [mselId.ToString(), MainHub.ADMIN_DATA_GROUP];

        protected static string[] RunGroups(Guid mselId) => [MainHub.FacilitationGroup(mselId), MainHub.ADMIN_DATA_GROUP];

        protected static string[] Modified(IEnumerable<string> properties) =>
            properties?.Select(x => x.TitleCaseToCamelCase()).ToArray();

        protected static ViewModels.AutomationResult ForBroadcast(AutomationResultEntity entity)
        {
            var result = entity.ToAutomationResult();
            if (result.ActualOutput?.Length > BroadcastOutputLength)
            {
                result.ActualOutput = result.ActualOutput.Substring(0, BroadcastOutputLength);
                result.OutputTruncated = true;
            }
            return result;
        }

        protected async Task<Guid?> MselOfRunAsync(Guid runId, CancellationToken ct) =>
            await _db.AutomationRuns.AsNoTracking().Where(r => r.Id == runId).Select(r => (Guid?)r.MselId).FirstOrDefaultAsync(ct);
    }

    public class AutomationTaskCreatedSignalRHandler(BlueprintContext db, IHubContext<MainHub> hub)
        : AutomationHandler(db, hub), INotificationHandler<EntityCreated<AutomationTaskEntity>>
    {
        public Task Handle(EntityCreated<AutomationTaskEntity> notification, CancellationToken ct) =>
            SendAsync(TaskGroups(notification.Entity.MselId), MainHubMethods.AutomationTaskCreated,
                [notification.Entity.ToAutomationTask(), null], ct);
    }

    public class AutomationTaskUpdatedSignalRHandler(BlueprintContext db, IHubContext<MainHub> hub)
        : AutomationHandler(db, hub), INotificationHandler<EntityUpdated<AutomationTaskEntity>>
    {
        public Task Handle(EntityUpdated<AutomationTaskEntity> notification, CancellationToken ct) =>
            SendAsync(TaskGroups(notification.Entity.MselId), MainHubMethods.AutomationTaskUpdated,
                [notification.Entity.ToAutomationTask(), Modified(notification.ModifiedProperties)], ct);
    }

    public class AutomationTaskDeletedSignalRHandler(BlueprintContext db, IHubContext<MainHub> hub)
        : AutomationHandler(db, hub), INotificationHandler<EntityDeleted<AutomationTaskEntity>>
    {
        public Task Handle(EntityDeleted<AutomationTaskEntity> notification, CancellationToken ct) =>
            SendAsync(TaskGroups(notification.Entity.MselId), MainHubMethods.AutomationTaskDeleted, [notification.Entity.Id], ct);
    }

    public class AutomationRunCreatedSignalRHandler(BlueprintContext db, IHubContext<MainHub> hub)
        : AutomationHandler(db, hub), INotificationHandler<EntityCreated<AutomationRunEntity>>
    {
        public Task Handle(EntityCreated<AutomationRunEntity> notification, CancellationToken ct) =>
            SendAsync(RunGroups(notification.Entity.MselId), MainHubMethods.AutomationRunCreated, [WithoutResults(notification.Entity), null], ct);

        internal static ViewModels.AutomationRun WithoutResults(AutomationRunEntity entity)
        {
            var run = entity.ToAutomationRun();
            run.Results = new();
            return run;
        }
    }

    public class AutomationRunUpdatedSignalRHandler(BlueprintContext db, IHubContext<MainHub> hub)
        : AutomationHandler(db, hub), INotificationHandler<EntityUpdated<AutomationRunEntity>>
    {
        public Task Handle(EntityUpdated<AutomationRunEntity> notification, CancellationToken ct) =>
            SendAsync(RunGroups(notification.Entity.MselId), MainHubMethods.AutomationRunUpdated,
                [AutomationRunCreatedSignalRHandler.WithoutResults(notification.Entity), Modified(notification.ModifiedProperties)], ct);
    }

    public class AutomationRunDeletedSignalRHandler(BlueprintContext db, IHubContext<MainHub> hub)
        : AutomationHandler(db, hub), INotificationHandler<EntityDeleted<AutomationRunEntity>>
    {
        public Task Handle(EntityDeleted<AutomationRunEntity> notification, CancellationToken ct) =>
            SendAsync(RunGroups(notification.Entity.MselId), MainHubMethods.AutomationRunDeleted, [notification.Entity.Id], ct);
    }

    public class AutomationResultCreatedSignalRHandler(BlueprintContext db, IHubContext<MainHub> hub)
        : AutomationHandler(db, hub), INotificationHandler<EntityCreated<AutomationResultEntity>>
    {
        public async Task Handle(EntityCreated<AutomationResultEntity> notification, CancellationToken ct)
        {
            var mselId = await MselOfRunAsync(notification.Entity.RunId, ct);
            if (mselId != null)
                await SendAsync(RunGroups(mselId.Value), MainHubMethods.AutomationResultCreated, [ForBroadcast(notification.Entity), null], ct);
        }
    }

    public class AutomationResultUpdatedSignalRHandler(BlueprintContext db, IHubContext<MainHub> hub)
        : AutomationHandler(db, hub), INotificationHandler<EntityUpdated<AutomationResultEntity>>
    {
        public async Task Handle(EntityUpdated<AutomationResultEntity> notification, CancellationToken ct)
        {
            var mselId = await MselOfRunAsync(notification.Entity.RunId, ct);
            if (mselId != null)
                await SendAsync(RunGroups(mselId.Value), MainHubMethods.AutomationResultUpdated,
                    [ForBroadcast(notification.Entity), Modified(notification.ModifiedProperties)], ct);
        }
    }

    public class AutomationResultDeletedSignalRHandler(BlueprintContext db, IHubContext<MainHub> hub)
        : AutomationHandler(db, hub), INotificationHandler<EntityDeleted<AutomationResultEntity>>
    {
        public async Task Handle(EntityDeleted<AutomationResultEntity> notification, CancellationToken ct)
        {
            var mselId = await MselOfRunAsync(notification.Entity.RunId, ct);
            if (mselId != null)
                await SendAsync(RunGroups(mselId.Value), MainHubMethods.AutomationResultDeleted, [notification.Entity.Id], ct);
        }
    }
}
