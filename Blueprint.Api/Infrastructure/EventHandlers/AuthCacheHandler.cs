// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediatR;
using Microsoft.Extensions.Caching.Memory;
using Blueprint.Api.Data.Models;
using Blueprint.Api.Services;
using Crucible.Common.EntityEvents.Events;

namespace Blueprint.Api.Infrastructure.EventHandlers
{
    // UserClaimsService caches each user's permission claims in IMemoryCache under the user's id. These
    // handlers drop that entry whenever what it was built from changes, so a promotion, a demotion or a
    // deletion is in force on the user's next request instead of when the entry expires.

    /// <summary>A user's system role is where their permissions come from.</summary>
    public class UserUpdatedAuthCacheHandler(IMemoryCache cache) : INotificationHandler<EntityUpdated<UserEntity>>
    {
        public Task Handle(EntityUpdated<UserEntity> notification, CancellationToken cancellationToken)
        {
            if (notification.ModifiedProperties.Contains(nameof(UserEntity.RoleId)))
                cache.Remove(notification.Entity.Id);

            return Task.CompletedTask;
        }
    }

    /// <summary>A deleted user's cached claims would otherwise keep answering for them.</summary>
    public class UserDeletedAuthCacheHandler(IMemoryCache cache) : INotificationHandler<EntityDeleted<UserEntity>>
    {
        public Task Handle(EntityDeleted<UserEntity> notification, CancellationToken cancellationToken)
        {
            cache.Remove(notification.Entity.Id);
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// A role's permissions are every holder's, and a user matched to it by an IdP role claim holds no row
    /// naming it, so any change to a role - a new one an IdP role name now matches included - makes every
    /// user's cached claims stale.
    /// </summary>
    public class SystemRoleCreatedAuthCacheHandler(IMemoryCache cache) : INotificationHandler<EntityCreated<SystemRoleEntity>>
    {
        public Task Handle(EntityCreated<SystemRoleEntity> notification, CancellationToken cancellationToken)
        {
            UserClaimsService.ChangeRolesVersion(cache);
            return Task.CompletedTask;
        }
    }

    public class SystemRoleUpdatedAuthCacheHandler(IMemoryCache cache) : INotificationHandler<EntityUpdated<SystemRoleEntity>>
    {
        public Task Handle(EntityUpdated<SystemRoleEntity> notification, CancellationToken cancellationToken)
        {
            UserClaimsService.ChangeRolesVersion(cache);
            return Task.CompletedTask;
        }
    }

    public class SystemRoleDeletedAuthCacheHandler(IMemoryCache cache) : INotificationHandler<EntityDeleted<SystemRoleEntity>>
    {
        public Task Handle(EntityDeleted<SystemRoleEntity> notification, CancellationToken cancellationToken)
        {
            UserClaimsService.ChangeRolesVersion(cache);
            return Task.CompletedTask;
        }
    }
}
