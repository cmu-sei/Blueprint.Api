// Copyright 2023 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Blueprint.Api.Data;
using Blueprint.Api.Infrastructure.Exceptions;
using Blueprint.Api.ViewModels;

namespace Blueprint.Api.Services
{
    public static class ServiceUtilities
    {
        // the ISO 8601 strings the UI's date picker saves, and the M/d/yyyy dates the xlsx import writes;
        // a bare time such as 10:00 is left out, since the UI can't show it as a date
        private static readonly string[] DateTimeValueFormats =
        [
            "yyyy-MM-dd",
            "yyyy-MM-ddTHH:mm",
            "yyyy-MM-ddTHH:mmK",
            "yyyy-MM-ddTHH:mm:ssK",
            "yyyy-MM-ddTHH:mm:ss.FFFFFFFK",
            "M/d/yyyy",
            "M/d/yyyy H:mm",
            "M/d/yyyy h:mm tt",
        ];

        /// <summary>True when a DateTime field's value is a date the UI can show.</summary>
        public static bool IsDateTimeValue(string value) =>
            DateTimeOffset.TryParseExact(value?.Trim(), DateTimeValueFormats, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out _);

        public static async Task SetMselModifiedAsync(Guid? id, Guid? modifiedBy, DateTime? dateModified, BlueprintContext context, CancellationToken ct)
        {
            if (id != null)
            {
                var mselToUpdate = await context.Msels.SingleOrDefaultAsync(v => v.Id == id, ct);
                if (mselToUpdate == null)
                    throw new EntityNotFoundException<Msel>();

                // okay to update this msel
                mselToUpdate.ModifiedBy = modifiedBy;
                mselToUpdate.DateModified = dateModified;
                context.Msels.Update(mselToUpdate);
                await context.SaveChangesAsync(ct);
            }
        }

        public static async Task SetCatalogModifiedAsync(Guid? id, Guid? modifiedBy, DateTime? dateModified, BlueprintContext context, CancellationToken ct)
        {
            if (id != null)
            {
                var catalogToUpdate = await context.Catalogs.SingleOrDefaultAsync(v => v.Id == id, ct);
                if (catalogToUpdate == null)
                    throw new EntityNotFoundException<Catalog>();

                // okay to update this catalog
                catalogToUpdate.ModifiedBy = modifiedBy;
                catalogToUpdate.DateModified = dateModified;
                context.Catalogs.Update(catalogToUpdate);
                await context.SaveChangesAsync(ct);
            }
        }

        /// <summary>
        /// Throws a 404 unless the team belongs to the MSEL. A reference to another MSEL's team is
        /// answered the same as a reference to no team at all.
        /// </summary>
        public static async Task EnsureTeamIsInMselAsync(Guid teamId, Guid? mselId, BlueprintContext context, CancellationToken ct)
        {
            if (mselId == null || !await context.Teams.AnyAsync(t => t.Id == teamId && t.MselId == mselId, ct))
                throw new EntityNotFoundException<Team>($"Team {teamId} was not found in MSEL {mselId}.");
        }

    }
}

