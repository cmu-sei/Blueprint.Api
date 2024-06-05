// Copyright 2024 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using System;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Blueprint.Api.Data;
using Microsoft.CodeAnalysis.VisualBasic.Syntax;

namespace Blueprint.Api.Infrastructure.Authorization
{
    public static class CatalogViewRequirement
    {
        public static async Task<Boolean> IsMet(Guid userId, Guid? catalogId, BlueprintContext blueprintContext)
        {
            var createdBy = (await blueprintContext.Catalogs.FirstOrDefaultAsync(m => m.Id == catalogId)).CreatedBy;
            if (createdBy == userId)
            {
                return true;
            }
            else
            {
                var unitIdList = await blueprintContext.UnitUsers
                    .Where(m => m.UserId == userId)
                    .Select(m => m.UnitId)
                    .ToListAsync();
                var isSuccess = await blueprintContext.CatalogUnits
                    .Where(m => m.CatalogId == catalogId && unitIdList.Contains(m.UnitId))
                    .AnyAsync();
                return isSuccess;
            }
        }
    }
}

