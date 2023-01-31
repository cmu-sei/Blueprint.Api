// Copyright 2023 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using AutoMapper;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Blueprint.Api.Data;
using Blueprint.Api.Data.Models;
using Blueprint.Api.Infrastructure.Authorization;
using Blueprint.Api.Infrastructure.Exceptions;
using Blueprint.Api.Infrastructure.Extensions;
using Blueprint.Api.ViewModels;

namespace Blueprint.Api.Services
{
    public interface ICiteRoleService
    {
        Task<IEnumerable<ViewModels.CiteRole>> GetByMselAsync(Guid mselId, CancellationToken ct);
        Task<ViewModels.CiteRole> GetAsync(Guid id, CancellationToken ct);
        Task<ViewModels.CiteRole> CreateAsync(ViewModels.CiteRole citeRole, CancellationToken ct);
        Task<ViewModels.CiteRole> UpdateAsync(Guid id, ViewModels.CiteRole citeRole, CancellationToken ct);
        Task<bool> DeleteAsync(Guid id, CancellationToken ct);
    }

    public class CiteRoleService : ICiteRoleService
    {
        private readonly BlueprintContext _context;
        private readonly IAuthorizationService _authorizationService;
        private readonly ClaimsPrincipal _user;
        private readonly IMapper _mapper;

        public CiteRoleService(
            BlueprintContext context,
            IAuthorizationService authorizationService,
            IPrincipal user,
            IMapper mapper)
        {
            _context = context;
            _authorizationService = authorizationService;
            _user = user as ClaimsPrincipal;
            _mapper = mapper;
        }

        public async Task<IEnumerable<ViewModels.CiteRole>> GetByMselAsync(Guid mselId, CancellationToken ct)
        {
            if (!(await _authorizationService.AuthorizeAsync(_user, null, new BaseUserRequirement())).Succeeded)
                throw new ForbiddenException();

            var citeRoleEntities = await _context.CiteRoles
                .Where(cr => cr.MselId == mselId)
                .Include(cr => cr.Team)
                .ToListAsync();

            return _mapper.Map<IEnumerable<CiteRole>>(citeRoleEntities).ToList();;
        }

        public async Task<ViewModels.CiteRole> GetAsync(Guid id, CancellationToken ct)
        {
            if (!(await _authorizationService.AuthorizeAsync(_user, null, new BaseUserRequirement())).Succeeded)
                throw new ForbiddenException();

            var item = await _context.CiteRoles
                .Include(cr => cr.Team)
                .SingleAsync(cr => cr.Id == id, ct);

            return _mapper.Map<CiteRole>(item);
        }

        public async Task<ViewModels.CiteRole> CreateAsync(ViewModels.CiteRole citeRole, CancellationToken ct)
        {
            if (!(await _authorizationService.AuthorizeAsync(_user, null, new ContentDeveloperRequirement())).Succeeded)
                throw new ForbiddenException();
            citeRole.Id = citeRole.Id != Guid.Empty ? citeRole.Id : Guid.NewGuid();
            citeRole.DateCreated = DateTime.UtcNow;
            citeRole.CreatedBy = _user.GetId();
            citeRole.DateModified = null;
            citeRole.ModifiedBy = null;
            var citeRoleEntity = _mapper.Map<CiteRoleEntity>(citeRole);

            _context.CiteRoles.Add(citeRoleEntity);
            await _context.SaveChangesAsync(ct);
            citeRole = await GetAsync(citeRoleEntity.Id, ct);

            return citeRole;
        }

        public async Task<ViewModels.CiteRole> UpdateAsync(Guid id, ViewModels.CiteRole citeRole, CancellationToken ct)
        {
            if (!(await _authorizationService.AuthorizeAsync(_user, null, new ContentDeveloperRequirement())).Succeeded)
                throw new ForbiddenException();

            var citeRoleToUpdate = await _context.CiteRoles.SingleOrDefaultAsync(v => v.Id == id, ct);

            if (citeRoleToUpdate == null)
                throw new EntityNotFoundException<CiteRole>();

            citeRole.CreatedBy = citeRoleToUpdate.CreatedBy;
            citeRole.DateCreated = citeRoleToUpdate.DateCreated;
            citeRole.ModifiedBy = _user.GetId();
            citeRole.DateModified = DateTime.UtcNow;
            _mapper.Map(citeRole, citeRoleToUpdate);

            _context.CiteRoles.Update(citeRoleToUpdate);
            await _context.SaveChangesAsync(ct);

            citeRole = await GetAsync(citeRoleToUpdate.Id, ct);

            return citeRole;
        }

        public async Task<bool> DeleteAsync(Guid id, CancellationToken ct)
        {
            if (!(await _authorizationService.AuthorizeAsync(_user, null, new ContentDeveloperRequirement())).Succeeded)
                throw new ForbiddenException();

            var citeRoleToDelete = await _context.CiteRoles.SingleOrDefaultAsync(v => v.Id == id, ct);

            if (citeRoleToDelete == null)
                throw new EntityNotFoundException<CiteRole>();

            _context.CiteRoles.Remove(citeRoleToDelete);
            await _context.SaveChangesAsync(ct);

            return true;
        }

    }
}

