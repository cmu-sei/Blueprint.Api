// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Blueprint.Api.Data;
using Blueprint.Api.Data.Models;

namespace Blueprint.Api.Infrastructure.Authorization
{
    /// <summary>
    /// What a MSEL participant may read. A participant reaches the MSEL through one of its teams without
    /// holding a role on the Contributors tab, and Blueprint shows them the Exercise View: the scenario
    /// events not marked hidden, and the cells of the Data Fields marked to show there. The API returns the
    /// same, so the rest of the MSEL does not reach a participant's browser. A holder of ViewMsels, a
    /// contributor (<see cref="MselContributorRequirement"/>) and anyone reading a template sees it all.
    /// </summary>
    public static class MselParticipantScope
    {
        /// <summary>
        /// Whether the caller sees only the Exercise View of the MSEL. Call it after the caller has passed the
        /// read check of the route; it does not decide whether the caller may read the MSEL at all.
        /// </summary>
        public static async Task<bool> IsLimitedAsync(Guid userId, Guid? mselId, bool hasSystemPermission, BlueprintContext context)
        {
            if (hasSystemPermission || mselId == null)
                return false;
            var isTemplate = await context.Msels
                .Where(m => m.Id == mselId)
                .Select(m => m.IsTemplate)
                .FirstOrDefaultAsync();
            if (isTemplate)
                return false;
            return !await MselContributorRequirement.IsMet(userId, mselId, false, context);
        }

        /// <summary>Whether a participant is shown this event: the Exercise View leaves out hidden events.</summary>
        public static bool Shows(ScenarioEventEntity scenarioEvent) => !scenarioEvent.IsHidden;

        /// <summary>Whether a participant is shown this Data Field: the Exercise View shows only those marked for it.</summary>
        public static bool Shows(DataFieldEntity dataField) => dataField.OnExerciseView;

        /// <summary>
        /// Clears the parts of an event the Exercise View does not show: the Steamfitter task it runs, where it
        /// is delivered, and the text of an Information event.
        /// </summary>
        public static ViewModels.ScenarioEvent Trim(ViewModels.ScenarioEvent scenarioEvent)
        {
            scenarioEvent.SteamfitterTaskId = null;
            scenarioEvent.SteamfitterTask = null;
            scenarioEvent.IntegrationTarget = null;
            scenarioEvent.Information = null;
            return scenarioEvent;
        }
    }
}
