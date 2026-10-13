// Copyright 2023 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using System;
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

