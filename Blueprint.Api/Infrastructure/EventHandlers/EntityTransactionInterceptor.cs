// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Blueprint.Api.Infrastructure.EventHandlers
{
    public class EntityTransactionInterceptor : DbTransactionInterceptor
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger<EntityTransactionInterceptor> _logger;

        public EntityTransactionInterceptor(
            IServiceProvider serviceProvider,
            ILogger<EntityTransactionInterceptor> logger)
        {
            _serviceProvider = serviceProvider;
            _logger = logger;
        }

        public override async Task TransactionCommittedAsync(
            DbTransaction transaction,
            TransactionEndEventData eventData,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            try
            {
                await PublishEvents(eventData);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in EntityTransactionInterceptor");
            }
            finally
            {
                await base.TransactionCommittedAsync(transaction, eventData, cancellationToken);
            }
        }

        public override async void TransactionCommitted(
            DbTransaction transaction,
            TransactionEndEventData eventData)
        {
            try
            {
                await PublishEvents(eventData);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in EntityTransactionInterceptor");
            }
            finally
            {
                base.TransactionCommitted(transaction, eventData);
            }
        }

        private async Task PublishEvents(TransactionEndEventData eventData)
        {
            var entries = GetEntries(eventData.Context.ChangeTracker);

            using (var scope = _serviceProvider.CreateScope())
            {
                var events = new List<INotification>();
                var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

                foreach (var entry in entries)
                {
                    var entityType = entry.Entity.GetType();
                    Type eventType = null;

                    string[] modifiedProperties = null;

                    switch (entry.State)
                    {
                        case EntityState.Added:
                            eventType = typeof(EntityCreated<>).MakeGenericType(entityType);
                            break;
                        case EntityState.Modified:
                            eventType = typeof(EntityUpdated<>).MakeGenericType(entityType);
                            modifiedProperties = entry.Properties
                                .Where(x => x.IsModified)
                                .Select(x => x.Metadata.Name)
                                .ToArray();
                            break;
                        case EntityState.Deleted:
                            eventType = typeof(EntityDeleted<>).MakeGenericType(entityType);
                            break;
                    }

                    if (eventType != null)
                    {
                        INotification evt;

                        if (modifiedProperties != null)
                        {
                            evt = Activator.CreateInstance(eventType, new[] { entry.Entity, modifiedProperties }) as INotification;
                        }
                        else
                        {
                            evt = Activator.CreateInstance(eventType, new[] { entry.Entity }) as INotification;
                        }


                        if (evt != null)
                        {
                            events.Add(evt);
                        }
                    }
                }

                foreach (var evt in events)
                {
                    try
                    {
                        await mediator.Publish(evt);
                    }
                    catch (System.NullReferenceException)
                    {
                        _logger.LogWarning("Ignore this NullReferenceException IF it occurred during application startup.");
                    }
                }
            }
        }

        private EntityEntry[] GetEntries(ChangeTracker changeTracker)
        {
            var entries = changeTracker.Entries()
                .Where(x => x.State == EntityState.Added ||
                            x.State == EntityState.Modified ||
                            x.State == EntityState.Deleted)
                .ToList();

            // Remove children so we don't duplicate events
            foreach (var entry in entries.ToArray())
            {
                foreach (var collection in entry.Collections)
                {
                    foreach (var val in collection.CurrentValue)
                    {
                        var e = entries.FirstOrDefault(e => e.Entity == val);

                        if (e != null)
                        {
                            entries.Remove(e);
                        }
                    }
                }
            }

            return entries.ToArray();
        }
    }
}