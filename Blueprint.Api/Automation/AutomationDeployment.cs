// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Blueprint.Api.Data;
using Blueprint.Api.Data.Models;
using Microsoft.EntityFrameworkCore;

namespace Blueprint.Api.Automation
{
    /// <summary>
    /// A MSEL's deployment, as its automation sees it. A push that deploys the MSEL starts one: a new deployment id, which
    /// its runs are kept under, and the first move as the current one, where the push leaves CITE and Gallery. Removing
    /// the integrations ends it, and cancels its runs.
    /// </summary>
    public static class AutomationDeployment
    {
        public static async Task StartAsync(BlueprintContext db, MselEntity msel, DateTime now, CancellationToken ct)
        {
            // a Caster retry finishes the same push: it stays in the deployment it started
            if (msel.DeploymentId != null)
                return;
            msel.DeploymentId = Guid.NewGuid();
            msel.CurrentMoveNumber = await db.Moves
                .Where(m => m.MselId == msel.Id)
                .OrderBy(m => m.DeltaSeconds).ThenBy(m => m.MoveNumber)
                .Select(m => (int?)m.MoveNumber)
                .FirstOrDefaultAsync(ct) ?? 0;
            msel.CurrentGroupNumber = 0;
            msel.CurrentGroupStartedAt = now;
            msel.AutomationPaused = false;
        }

        public static void End(MselEntity msel)
        {
            msel.DeploymentId = null;
            msel.CurrentMoveNumber = null;
            msel.CurrentGroupNumber = null;
            msel.CurrentGroupStartedAt = null;
            msel.AutomationPaused = false;
        }
    }
}
