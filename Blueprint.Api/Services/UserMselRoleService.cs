// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using System;
using System.Collections.Generic;
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
    public interface IUserMselRoleService
    {
        Task<IEnumerable<ViewModels.UserMselRole>> GetAsync(CancellationToken ct);
        Task<ViewModels.UserMselRole> GetAsync(Guid id, CancellationToken ct);
        Task<ViewModels.UserMselRole> CreateAsync(ViewModels.UserMselRole userMselRole, CancellationToken ct);
        Task<bool> DeleteAsync(Guid id, CancellationToken ct);
    }

    public class UserMselRoleService : IUserMselRoleService
    {
        private readonly BlueprintContext _context;
        private readonly IAuthorizationService _authorizationService;
        private readonly ClaimsPrincipal _user;
        private readonly IMapper _mapper;

        public UserMselRoleService(BlueprintContext context, IAuthorizationService authorizationService, IPrincipal user, IMapper mapper)
        {
            _context = context;
            _authorizationService = authorizationService;
            _user = user as ClaimsPrincipal;
            _mapper = mapper;
        }

        public async Task<IEnumerable<ViewModels.UserMselRole>> GetAsync(CancellationToken ct)
        {
            if (!(await _authorizationService.AuthorizeAsync(_user, null, new FullRightsRequirement())).Succeeded)
                throw new ForbiddenException();

            var items = await _context.UserMselRoles
                .ToListAsync(ct);

            return _mapper.Map<IEnumerable<UserMselRole>>(items);
        }

        public async Task<ViewModels.UserMselRole> GetAsync(Guid id, CancellationToken ct)
        {
            if (!(await _authorizationService.AuthorizeAsync(_user, null, new FullRightsRequirement())).Succeeded)
                throw new ForbiddenException();

            var item = await _context.UserMselRoles
                .SingleOrDefaultAsync(o => o.Id == id, ct);

            return _mapper.Map<UserMselRole>(item);
        }

        public async Task<ViewModels.UserMselRole> CreateAsync(ViewModels.UserMselRole userMselRole, CancellationToken ct)
        {
            if (!(await _authorizationService.AuthorizeAsync(_user, null, new FullRightsRequirement())).Succeeded)
                throw new ForbiddenException();

            // start a transaction, because we may also update other data fields
            await _context.Database.BeginTransactionAsync();
            userMselRole.Id = userMselRole.Id != Guid.Empty ? userMselRole.Id : Guid.NewGuid();
            userMselRole.DateCreated = DateTime.UtcNow;
            userMselRole.CreatedBy = _user.GetId();
            userMselRole.DateModified = null;
            userMselRole.ModifiedBy = null;
            var userMselRoleEntity = _mapper.Map<UserMselRoleEntity>(userMselRole);

            _context.UserMselRoles.Add(userMselRoleEntity);
            await _context.SaveChangesAsync(ct);
            // update the MSEL modified info
            await ServiceUtilities.SetMselModifiedAsync(userMselRole.MselId, userMselRole.CreatedBy, userMselRole.DateCreated, _context, ct);
            // commit the transaction
            await _context.Database.CommitTransactionAsync(ct);

            return await GetAsync(userMselRoleEntity.Id, ct);
        }

        public async Task<bool> DeleteAsync(Guid id, CancellationToken ct)
        {
            if (!(await _authorizationService.AuthorizeAsync(_user, null, new FullRightsRequirement())).Succeeded)
                throw new ForbiddenException();

            var userMselRoleToDelete = await _context.UserMselRoles.SingleOrDefaultAsync(v => v.Id == id, ct);

            if (userMselRoleToDelete == null)
                throw new EntityNotFoundException<UserMselRole>();

            // start a transaction, because we may also update other data fields
            await _context.Database.BeginTransactionAsync();
            _context.UserMselRoles.Remove(userMselRoleToDelete);
            await _context.SaveChangesAsync(ct);
            // update the MSEL modified info
            await ServiceUtilities.SetMselModifiedAsync(userMselRoleToDelete.MselId, _user.GetId(), DateTime.UtcNow, _context, ct);
            // commit the transaction
            await _context.Database.CommitTransactionAsync(ct);

            return true;
        }

    }
}

