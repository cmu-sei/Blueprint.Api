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

    }
}

