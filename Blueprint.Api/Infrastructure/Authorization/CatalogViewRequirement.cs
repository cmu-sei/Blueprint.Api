// Copyright 2024 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Blueprint.Api.Data;

namespace Blueprint.Api.Infrastructure.Authorization
{
    public static class CatalogViewRequirement
    {
        public static async Task<Boolean> IsMet(Guid userId, Guid? catalogId, BlueprintContext blueprintContext)
        {
            var catalog = await blueprintContext.Catalogs.FirstOrDefaultAsync(m => m.Id == catalogId);
            if (catalog == null)
            {
                return false;
            }
            if (catalog.IsPublic)
            {
                return true;
            }
            if (catalog.CreatedBy == userId)
            {
                return true;
            }
            var unitIdList = await blueprintContext.UnitUsers
                .Where(m => m.UserId == userId)
                .Select(m => m.UnitId)
                .ToListAsync();
            var isSuccess = await blueprintContext.CatalogUnits
                .Where(m => m.CatalogId == catalogId && unitIdList.Contains(m.UnitId))
                .AnyAsync();
            return isSuccess;
        }

        /// <summary>
        /// The injects of <paramref name="injectIds"/> that the user can see through at least one of the
        /// catalogs they are in, by the same rule as <see cref="IsMet"/>, in one query.
        /// </summary>
        public static async Task<HashSet<Guid>> ViewableInjectIdsAsync(Guid userId, IEnumerable<Guid> injectIds, BlueprintContext blueprintContext, CancellationToken ct)
        {
            var idList = injectIds.Distinct().ToList();
            var unitIdList = blueprintContext.UnitUsers
                .Where(m => m.UserId == userId)
                .Select(m => m.UnitId);
            var viewable = await blueprintContext.CatalogInjects
                .Where(ci => idList.Contains(ci.InjectId) &&
                    (ci.Catalog.IsPublic ||
                     ci.Catalog.CreatedBy == userId ||
                     ci.Catalog.CatalogUnits.Any(cu => unitIdList.Contains(cu.UnitId))))
                .Select(ci => ci.InjectId)
                .Distinct()
                .ToListAsync(ct);
            return viewable.ToHashSet();
        }
    }
}
