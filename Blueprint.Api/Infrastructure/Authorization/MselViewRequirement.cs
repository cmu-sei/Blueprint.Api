// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using System;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Blueprint.Api.Data;

namespace Blueprint.Api.Infrastructure.Authorization
{
    public static class MselViewRequirement
    {
        public static async Task<Boolean> IsMet(Guid userId, Guid? mselId, BlueprintContext blueprintContext)
        {
            var createdBy = (await blueprintContext.Msels.FirstOrDefaultAsync(m => m.Id == mselId)).CreatedBy;
            if (createdBy == userId)
            {
                return true;
            }
            else
            {
                // Check if user is on a team in the MSEL and has a UserMselRole
                var isOnTeam = await blueprintContext.TeamUsers
                    .Where(tu => tu.UserId == userId && tu.Team.MselId == mselId)
                    .AnyAsync();

                if (isOnTeam)
                {
                    var hasRole = await blueprintContext.UserMselRoles
                        .Where(umr => umr.UserId == userId &&
                            umr.MselId == mselId &&
                            (
                                umr.Role == Data.Enumerations.MselRole.Viewer ||
                                umr.Role == Data.Enumerations.MselRole.Editor ||
                                umr.Role == Data.Enumerations.MselRole.Approver ||
                                umr.Role == Data.Enumerations.MselRole.MoveEditor ||
                                umr.Role == Data.Enumerations.MselRole.Owner
                            )
                        )
                        .AnyAsync();
                    if (hasRole)
                    {
                        return true;
                    }
                }

                // Fallback to original unit-based authorization
                var mselUnitIdList = await blueprintContext.MselUnits
                    .Where(t => t.MselId == mselId)
                    .Select(t => t.UnitId)
                    .ToListAsync();
                var isSuccess = await blueprintContext.UnitUsers
                    .Where(tu => tu.UserId == userId && mselUnitIdList.Contains(tu.UnitId))
                    .AnyAsync();
                if (isSuccess)
                {
                    isSuccess = await blueprintContext.UserMselRoles
                        .Where(umr => umr.UserId == userId &&
                            umr.MselId == mselId &&
                            (
                                umr.Role == Data.Enumerations.MselRole.Viewer ||
                                umr.Role == Data.Enumerations.MselRole.Editor ||
                                umr.Role == Data.Enumerations.MselRole.Approver ||
                                umr.Role == Data.Enumerations.MselRole.MoveEditor ||
                                umr.Role == Data.Enumerations.MselRole.Owner
                            )
                        )
                        .AnyAsync();
                }
                return isSuccess;
            }
        }
    }
}
