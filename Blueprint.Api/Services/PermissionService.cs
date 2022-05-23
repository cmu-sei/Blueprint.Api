// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using AutoMapper;
using AutoMapper.QueryableExtensions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Blueprint.Api.Data;
using Blueprint.Api.Data.Models;
using Blueprint.Api.Infrastructure.Extensions;
using Blueprint.Api.Infrastructure.Authorization;
using Blueprint.Api.Infrastructure.Exceptions;
using Blueprint.Api.ViewModels;

namespace Blueprint.Api.Services
{
    public interface IPermissionService
    {
        Task<IEnumerable<ViewModels.Permission>> GetAsync(CancellationToken ct);
        Task<IEnumerable<ViewModels.Permission>> GetMineAsync(CancellationToken ct);
        Task<IEnumerable<ViewModels.Permission>> GetByUserAsync(Guid id, CancellationToken ct);
        Task<ViewModels.Permission> GetAsync(Guid id, CancellationToken ct);
        // Task<IEnumerable<ViewModels.Permission>> GetByUserIdAsync(Guid userId, CancellationToken ct);
        Task<ViewModels.Permission> CreateAsync(ViewModels.Permission permission, CancellationToken ct);
        Task<ViewModels.Permission> UpdateAsync(Guid id, ViewModels.Permission permission, CancellationToken ct);
        Task<bool> DeleteAsync(Guid id, CancellationToken ct);
    }

    public class PermissionService : IPermissionService
    {
        private readonly BlueprintContext _context;
        private readonly IAuthorizationService _authorizationService;
        private readonly ClaimsPrincipal _user;
        private readonly IMapper _mapper;

        public PermissionService(BlueprintContext context, IAuthorizationService authorizationService, IPrincipal user, IMapper mapper)
        {
            _context = context;
            _authorizationService = authorizationService;
            _user = user as ClaimsPrincipal;
            _mapper = mapper;
        }

        public async Task<IEnumerable<ViewModels.Permission>> GetAsync(CancellationToken ct)
        {
            if (!(await _authorizationService.AuthorizeAsync(_user, null, new FullRightsRequirement())).Succeeded)
                throw new ForbiddenException();

            var items = await _context.Permissions
                .ToListAsync(ct);

            return _mapper.Map<IEnumerable<Permission>>(items);
        }

        public async Task<IEnumerable<ViewModels.Permission>> GetMineAsync(CancellationToken ct)
        {
            var items = await _context.UserPermissions
                .Where(w => w.UserId == _user.GetId())
                .Select(x => x.Permission)
                .ToListAsync(ct);

            return _mapper.Map<IEnumerable<Permission>>(items);
        }

        public async Task<IEnumerable<ViewModels.Permission>> GetByUserAsync(Guid userId, CancellationToken ct)
        {
            if (!(await _authorizationService.AuthorizeAsync(_user, null, new FullRightsRequirement())).Succeeded)
                throw new ForbiddenException();

            var items = await _context.UserPermissions
                .Where(w => w.UserId == userId)
                .Select(x => x.Permission)
                .ToListAsync(ct);

            return _mapper.Map<IEnumerable<Permission>>(items);
        }

        public async Task<ViewModels.Permission> GetAsync(Guid id, CancellationToken ct)
        {
            if (!(await _authorizationService.AuthorizeAsync(_user, null, new FullRightsRequirement())).Succeeded)
                throw new ForbiddenException();

            var item = await _context.Permissions
                .SingleOrDefaultAsync(o => o.Id == id, ct);

            return _mapper.Map<Permission>(item);
        }

        public async Task<ViewModels.Permission> CreateAsync(ViewModels.Permission permission, CancellationToken ct)
        {
            if (!(await _authorizationService.AuthorizeAsync(_user, null, new FullRightsRequirement())).Succeeded)
                throw new ForbiddenException();

            permission.Id = permission.Id != Guid.Empty ? permission.Id : Guid.NewGuid();
            permission.DateCreated = DateTime.UtcNow;
            permission.CreatedBy = _user.GetId();
            permission.DateModified = null;
            permission.ModifiedBy = null;
            var permissionEntity = _mapper.Map<PermissionEntity>(permission);

            _context.Permissions.Add(permissionEntity);
            await _context.SaveChangesAsync(ct);

            return await GetAsync(permissionEntity.Id, ct);
        }

        public async Task<ViewModels.Permission> UpdateAsync(Guid id, ViewModels.Permission permission, CancellationToken ct)
        {
            if (!(await _authorizationService.AuthorizeAsync(_user, null, new FullRightsRequirement())).Succeeded)
                throw new ForbiddenException();

            var permissionToUpdate = await _context.Permissions.SingleOrDefaultAsync(v => v.Id == id, ct);

            if (permissionToUpdate == null)
                throw new EntityNotFoundException<Permission>();

            permission.CreatedBy = permissionToUpdate.CreatedBy;
            permission.DateCreated = permissionToUpdate.DateCreated;
            permission.DateModified = DateTime.UtcNow;
            permission.ModifiedBy = _user.GetId();
            _mapper.Map(permission, permissionToUpdate);

            _context.Permissions.Update(permissionToUpdate);
            await _context.SaveChangesAsync(ct);

            return _mapper.Map(permissionToUpdate, permission);
        }

        public async Task<bool> DeleteAsync(Guid id, CancellationToken ct)
        {
            if (!(await _authorizationService.AuthorizeAsync(_user, null, new FullRightsRequirement())).Succeeded)
                throw new ForbiddenException();

            var permissionToDelete = await _context.Permissions.SingleOrDefaultAsync(v => v.Id == id, ct);

            if (permissionToDelete == null)
                throw new EntityNotFoundException<Permission>();

            _context.Permissions.Remove(permissionToDelete);
            await _context.SaveChangesAsync(ct);

            return true;
        }

    }
}

