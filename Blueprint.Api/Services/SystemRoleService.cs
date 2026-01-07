// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using AutoMapper;
using Microsoft.EntityFrameworkCore;
using Blueprint.Api.Data;
using Blueprint.Api.Data.Models;
using Blueprint.Api.Infrastructure.Exceptions;
using Blueprint.Api.ViewModels;

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
        private readonly IMapper _mapper;

        public SystemRoleService(BlueprintContext context, IPrincipal user, IMapper mapper)
        {
            _context = context;
            _user = user as ClaimsPrincipal;
            _mapper = mapper;
        }

        public async Task<IEnumerable<SystemRole>> GetAsync(CancellationToken ct)
        {
            var items = await _context.SystemRoles.ToListAsync(ct);

            return _mapper.Map<IEnumerable<SystemRole>>(items);
        }

        public async Task<SystemRole> GetAsync(Guid id, CancellationToken ct)
        {
            var item = await _context.SystemRoles.SingleOrDefaultAsync(o => o.Id == id, ct);

            return _mapper.Map<SystemRole>(item);
        }

        public async Task<SystemRole> CreateAsync(SystemRole systemRole, CancellationToken ct)
        {
            var systemRoleEntity = _mapper.Map<SystemRoleEntity>(systemRole);

            _context.SystemRoles.Add(systemRoleEntity);
            await _context.SaveChangesAsync(ct);

            return await GetAsync(systemRoleEntity.Id, ct);
        }

        public async Task<SystemRole> UpdateAsync(Guid id, SystemRole systemRole, CancellationToken ct)
        {
            var systemRoleToUpdate = await _context.SystemRoles.SingleOrDefaultAsync(v => v.Id == id, ct);

            if (systemRoleToUpdate == null)
                throw new EntityNotFoundException<SystemRole>();

            _mapper.Map(systemRole, systemRoleToUpdate);

            _context.SystemRoles.Update(systemRoleToUpdate);
            await _context.SaveChangesAsync(ct);

            return _mapper.Map(systemRoleToUpdate, systemRole);
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
