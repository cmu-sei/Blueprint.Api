// Copyright 2024 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;

namespace Blueprint.Api.ViewModels
{
    public class CatalogInject
    {
        public CatalogInject() {
        }

        public CatalogInject(Guid catalogId, Guid injectId)
        {
            CatalogId = catalogId;
            InjectId = injectId;
        }

        public Guid Id { get; set; }
        public Guid CatalogId { get; set; }
        public Guid InjectId { get; set; }
        public virtual Injectm Inject { get; set; }
        public bool IsNew { get; set; }
        public int DisplayOrder { get; set; }
    }

}
