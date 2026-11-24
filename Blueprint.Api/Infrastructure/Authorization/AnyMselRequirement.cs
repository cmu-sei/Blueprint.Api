// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Blueprint.Api.Data;

namespace Blueprint.Api.Infrastructure.Authorization
{
    /// <summary>
    /// Whether the user works on at least one MSEL: they created one, or hold a role on one.
    /// </summary>
    /// <remarks>
    /// The gate for the shared lists a MSEL's tabs load whatever the caller's role on it - the units and
    /// the template libraries. Those belong to no MSEL, so no per-MSEL requirement can decide them, and
    /// every caller who can open a MSEL's tabs holds a role on it or created it. A MSEL's participants,
    /// who reach it only through a team, are not let in: they are shown the Exercise View, which loads
    /// none of these lists.
    /// </remarks>
    public static class AnyMselRequirement
    {
        public static async Task<bool> IsMetAsync(Guid userId, BlueprintContext blueprintContext, CancellationToken ct)
        {
            return await blueprintContext.Msels.AnyAsync(m => m.CreatedBy == userId, ct) ||
                await blueprintContext.UserMselRoles.AnyAsync(r => r.UserId == userId, ct);
        }
    }
}
