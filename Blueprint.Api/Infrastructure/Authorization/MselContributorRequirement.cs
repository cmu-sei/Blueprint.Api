// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Blueprint.Api.Data;
using Blueprint.Api.Data.Enumerations;

namespace Blueprint.Api.Infrastructure.Authorization
{
    /// <summary>
    /// Whether the user contributes to the MSEL: its creator, a holder of CreateMsels for a template,
    /// or a member of one of its units holding a role from the Contributors tab. This is
    /// <see cref="MselViewRequirement"/> without its team path: being on one of the MSEL's teams
    /// makes a user a participant, not a contributor.
    /// </summary>
    public static class MselContributorRequirement
    {
        private static readonly MselRole[] ContributorRoles =
        {
            MselRole.Owner,
            MselRole.Editor,
            MselRole.Approver,
            MselRole.MoveEditor,
            MselRole.Evaluator,
            MselRole.Viewer,
        };

        public static async Task<bool> IsMet(Guid userId, Guid? mselId, bool hasCreateMselsPermission, BlueprintContext blueprintContext)
        {
            var msel = await blueprintContext.Msels.FirstOrDefaultAsync(m => m.Id == mselId);
            if (msel == null)
                return false;
            if (hasCreateMselsPermission && msel.IsTemplate)
                return true;
            if (msel.CreatedBy == userId)
                return true;

            var mselUnitIdList = await blueprintContext.MselUnits
                .Where(mu => mu.MselId == mselId)
                .Select(mu => mu.UnitId)
                .ToListAsync();
            var isInMselUnit = await blueprintContext.UnitUsers
                .AnyAsync(uu => uu.UserId == userId && mselUnitIdList.Contains(uu.UnitId));

            return isInMselUnit && await blueprintContext.UserMselRoles
                .AnyAsync(umr => umr.UserId == userId && umr.MselId == mselId && ContributorRoles.Contains(umr.Role));
        }
    }
}
