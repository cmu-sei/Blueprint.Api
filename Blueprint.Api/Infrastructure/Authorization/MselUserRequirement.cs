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
    public static class MselUserRequirement
    {
        public static async Task<Boolean> IsMet(Guid userId, Guid? mselId, BlueprintContext blueprintContext)
        {
            var msel = await blueprintContext.Msels.FirstOrDefaultAsync(m => m.Id == mselId);
            if (msel == null)
            {
                // a MSEL that is not there grants nothing; the caller answers 404 for it
                return false;
            }
            if (msel.CreatedBy == userId)
            {
                return true;
            }
            else
            {
                // Check if user is on a team in the MSEL
                var isOnTeam = await blueprintContext.TeamUsers
                    .Where(tu => tu.UserId == userId && tu.Team.MselId == mselId)
                    .AnyAsync();
                if (isOnTeam)
                {
                    return true;
                }

                // otherwise the user must be a contributor: a member of one of the MSEL's units who
                // holds a role on it. Unit membership alone grants nothing.
                return await MselContributorRequirement.IsMet(userId, mselId, false, blueprintContext);
            }
        }
    }
}
