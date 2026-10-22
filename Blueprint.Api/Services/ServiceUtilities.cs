// Copyright 2023 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Blueprint.Api.Data;
using Blueprint.Api.Data.Enumerations;
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

        // the forms the xlsx import writes, which a workbook's date cell can't hold as they are
        private static readonly string[] ImportedDateTimeFormats = ["M/d/yyyy", "M/d/yyyy H:mm", "M/d/yyyy h:mm tt"];

        /// <summary>
        /// A DateTime value as an xlsx date cell holds it: an M/d/yyyy value, with its time if it has one,
        /// becomes yyyy-MM-ddTHH:mm:ss, and an ISO 8601 value is already in that form.
        /// </summary>
        public static string ToCellDateTime(string value)
        {
            var text = value.Trim();
            return DateTime.TryParseExact(text, ImportedDateTimeFormats, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var dateTime)
                ? dateTime.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture)
                : text;
        }

        /// <summary>
        /// A cell's metadata, "color,tint,weight,type". The tint is written without the server's regional
        /// settings, since a decimal comma would add a part and move the type out of the last place.
        /// </summary>
        public static string CellMetadata(string color, double tint, string fontWeight, DataFieldType dataType) =>
            string.Join(",", color, tint.ToString(CultureInfo.InvariantCulture), fontWeight, (int)dataType);

        /// <summary>True when a value can be written as an xlsx cell of its Data Field's type.</summary>
        public static bool FitsCellType(DataFieldType dataType, string value) => dataType switch
        {
            DataFieldType.DateTime => IsDateTimeValue(value),
            DataFieldType.Integer or DataFieldType.Double =>
                double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _),
            DataFieldType.Boolean => value.Trim() is "0" or "1" ||
                bool.TryParse(value.Trim(), out _),
            _ => true,
        };

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

