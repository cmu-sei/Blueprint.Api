// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using MediatR;

namespace Blueprint.Api.Infrastructure.EventHandlers
{
    public class EntityCreated<TEntity> : INotification
    {
        public TEntity Entity { get; set; }

        public EntityCreated(TEntity entity)
        {
            Entity = entity;
        }
    }

    public class EntityUpdated<TEntity> : INotification
    {
        public TEntity Entity { get; set; }
        public string[] ModifiedProperties { get; set; }

        public EntityUpdated(TEntity entity, string[] modifiedProperties)
        {
            Entity = entity;
            ModifiedProperties = modifiedProperties;
        }
    }

    public class EntityDeleted<TEntity> : INotification
    {
        public TEntity Entity { get; set; }

        public EntityDeleted(TEntity entity)
        {
            Entity = entity;
        }
    }
}
