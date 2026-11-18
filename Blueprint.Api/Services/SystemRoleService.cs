// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Blueprint.Api.Data;
using Blueprint.Api.Data.Models;
using Blueprint.Api.Infrastructure.Exceptions;
using Blueprint.Api.ViewModels;
using Blueprint.Api.Infrastructure.Mappings;

namespace Blueprint.Api.Services
{
    public interface ISystemRoleService
    {
        Task<IEnumerable<SystemRole>> GetAsync(CancellationToken ct);
        Task<SystemRole> GetAsync(Guid id, CancellationToken ct);
        Task<SystemRole> CreateAsync(SystemRole systemRole, CancellationToken ct);
        Task<SystemRole> UpdateAsync(Guid id, SystemRole systemRole, CancellationToken ct);
        Task<bool> DeleteAsync(Guid id, CancellationToken ct);
    }

    public class SystemRoleService : ISystemRoleService
    {
        private readonly BlueprintContext _context;
        private readonly ClaimsPrincipal _user;

        public SystemRoleService(BlueprintContext context, IPrincipal user)
        {
            _context = context;
            _user = user as ClaimsPrincipal;
        }

        public async Task<IEnumerable<SystemRole>> GetAsync(CancellationToken ct)
        {
            var items = await _context.SystemRoles.ToListAsync(ct);

            return items.Select(x => x.ToSystemRole()).ToList();
        }

        public async Task<SystemRole> GetAsync(Guid id, CancellationToken ct)
        {
            var item = await _context.SystemRoles.SingleOrDefaultAsync(o => o.Id == id, ct);

            return item.ToSystemRole();
        }

        public async Task<SystemRole> CreateAsync(SystemRole systemRole, CancellationToken ct)
        {
            var systemRoleEntity = systemRole.ToEntity();

            _context.SystemRoles.Add(systemRoleEntity);
            await _context.SaveChangesAsync(ct);

            return await GetAsync(systemRoleEntity.Id, ct);
        }

        public async Task<SystemRole> UpdateAsync(Guid id, SystemRole systemRole, CancellationToken ct)
        {
            var systemRoleToUpdate = await _context.SystemRoles.SingleOrDefaultAsync(v => v.Id == id, ct);

            if (systemRoleToUpdate == null)
                throw new EntityNotFoundException<SystemRole>();

            systemRole.ApplyTo(systemRoleToUpdate);

            _context.SystemRoles.Update(systemRoleToUpdate);
            await _context.SaveChangesAsync(ct);

            systemRoleToUpdate.ApplyTo(systemRole);
            return systemRole;
        }

        public async Task<bool> DeleteAsync(Guid id, CancellationToken ct)
        {
            var systemRoleToDelete = await _context.SystemRoles.SingleOrDefaultAsync(v => v.Id == id, ct);

            if (systemRoleToDelete == null)
                throw new EntityNotFoundException<SystemRole>();

            _context.SystemRoles.Remove(systemRoleToDelete);
            await _context.SaveChangesAsync(ct);

            return true;
        }
    }
}
