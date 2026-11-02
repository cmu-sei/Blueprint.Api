// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Blueprint.Api.Data;
using Blueprint.Api.Data.Enumerations;
using Microsoft.EntityFrameworkCore;

namespace Blueprint.Api.Services
{
    /// <summary>
    /// The CITE and Gallery roles that let a user watch every team, filled in on the Contributors tab where none is set: exercise
    /// control (the EXCON team) as a CITE facilitator, who also advances the moves, and a Gallery observer; an evaluator (an
    /// assessor) as a CITE and a Gallery observer, who see every team and change nothing. A role set by hand is kept, and the
    /// push sends what the tab shows.
    /// </summary>
    public static class IntegrationRoleDefaults
    {
        public const string Facilitator = "Facilitator";
        public const string Observer = "Observer";

        /// <summary>
        /// Fills the empty CITE and Gallery roles of a user's role rows on a MSEL, the stored ones and any added but not yet saved,
        /// for the caller's own save: a second save in the same request lets the team user handlers' cleared navigations remove
        /// the row just added. joinsControl says the user is being added to exercise control in this request.
        /// </summary>
        public static async Task FillAsync(BlueprintContext context, Guid mselId, Guid userId, bool joinsControl, CancellationToken ct)
        {
            var rows = (await context.UserMselRoles.Where(r => r.MselId == mselId && r.UserId == userId).ToListAsync(ct))
                .Union(context.UserMselRoles.Local.Where(r => r.MselId == mselId && r.UserId == userId))
                .Distinct()
                .ToList();
            if (!rows.Any())
                return;
            var onControl = joinsControl || await context.TeamUsers.AnyAsync(tu => tu.UserId == userId && tu.Team.MselId == mselId && tu.Team.ShortName.ToUpper() == "EXCON", ct);
            var evaluator = rows.Any(r => r.Role == MselRole.Evaluator);
            if (!onControl && !evaluator)
                return;
            if (rows.All(r => !string.IsNullOrEmpty(r.CiteEvaluationRole) && !string.IsNullOrEmpty(r.GalleryExhibitRole)))
                return;
            var cite = onControl ? Facilitator : Observer;
            // one value across the user's rows, as the push reads the first; a role set by hand on any row is the user's
            var setCite = rows.Select(r => r.CiteEvaluationRole).FirstOrDefault(r => !string.IsNullOrEmpty(r)) ?? cite;
            var setGallery = rows.Select(r => r.GalleryExhibitRole).FirstOrDefault(r => !string.IsNullOrEmpty(r)) ?? Observer;
            foreach (var row in rows)
            {
                row.CiteEvaluationRole ??= setCite;
                row.GalleryExhibitRole ??= setGallery;
            }
        }
    }
}
