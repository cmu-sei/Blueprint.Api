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
    /// Whether the user can run the deployed MSEL from the Facilitator View: start its moves and groups,
    /// and run, rerun and cancel its automation tasks. That is the MSEL's creator, or a member of one of its
    /// units who is its Owner or a Facilitator.
    /// </summary>
    public static class MselFacilitatorRequirement
    {
        public static readonly MselRole[] Roles = { MselRole.Owner, MselRole.Facilitator };

        public static Task<bool> IsMet(Guid userId, Guid? mselId, BlueprintContext blueprintContext) =>
            HoldsRole(userId, mselId, Roles, blueprintContext);

        internal static async Task<bool> HoldsRole(Guid userId, Guid? mselId, MselRole[] roles, BlueprintContext blueprintContext)
        {
            var msel = await blueprintContext.Msels.FirstOrDefaultAsync(m => m.Id == mselId);
            if (msel == null)
                return false;
            if (msel.CreatedBy == userId)
                return true;

            var mselUnitIdList = await blueprintContext.MselUnits
                .Where(mu => mu.MselId == mselId)
                .Select(mu => mu.UnitId)
                .ToListAsync();
            var isInMselUnit = await blueprintContext.UnitUsers
                .AnyAsync(uu => uu.UserId == userId && mselUnitIdList.Contains(uu.UnitId));

            return isInMselUnit && await blueprintContext.UserMselRoles
                .AnyAsync(umr => umr.UserId == userId && umr.MselId == mselId && roles.Contains(umr.Role));
        }
    }

    /// <summary>
    /// Whether the user can watch the deployed MSEL on the Facilitator View: its automation runs, their
    /// results and where the exercise is. That is everyone <see cref="MselFacilitatorRequirement"/> lets
    /// act, plus the MSEL's Editors and Approvers, who see it read-only. Evaluators, Viewers, Move Editors
    /// and participants do not.
    /// </summary>
    public static class MselFacilitatorViewRequirement
    {
        public static readonly MselRole[] Roles = { MselRole.Owner, MselRole.Facilitator, MselRole.Editor, MselRole.Approver };

        public static Task<bool> IsMet(Guid userId, Guid? mselId, BlueprintContext blueprintContext) =>
            MselFacilitatorRequirement.HoldsRole(userId, mselId, Roles, blueprintContext);
    }
}
