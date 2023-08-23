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
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
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
        Task<IEnumerable<ViewModels.UserMselRole>> GetByMselAsync(Guid mselId, CancellationToken ct);
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
        private readonly ILogger<IUserMselRoleService> _logger;

        public UserMselRoleService(BlueprintContext context, IAuthorizationService authorizationService, IPrincipal user, ILogger<IUserMselRoleService> logger, IMapper mapper)
        {
            _context = context;
            _authorizationService = authorizationService;
            _user = user as ClaimsPrincipal;
            _mapper = mapper;
            _logger = logger;
        }

        public async Task<IEnumerable<ViewModels.UserMselRole>> GetByMselAsync(Guid mselId, CancellationToken ct)
        {
            // must be a MSEL viewer
            if (!(await _authorizationService.AuthorizeAsync(_user, null, new ContentDeveloperRequirement())).Succeeded &&
                !(await MselViewRequirement.IsMet(_user.GetId(), mselId, _context)))
                throw new ForbiddenException();

            var items = await _context.UserMselRoles
                .Where(umr => umr.MselId == mselId)
                .ToListAsync(ct);

            return _mapper.Map<IEnumerable<UserMselRole>>(items);
        }

        public async Task<ViewModels.UserMselRole> GetAsync(Guid id, CancellationToken ct)
        {
            var item = await _context.UserMselRoles
                .SingleOrDefaultAsync(o => o.Id == id, ct);

            // must be a MSEL viewer
            if (!(await _authorizationService.AuthorizeAsync(_user, null, new ContentDeveloperRequirement())).Succeeded &&
                !(await MselViewRequirement.IsMet(_user.GetId(), item.MselId, _context)))
                throw new ForbiddenException();

            return _mapper.Map<UserMselRole>(item);
        }

        public async Task<ViewModels.UserMselRole> CreateAsync(ViewModels.UserMselRole userMselRole, CancellationToken ct)
        {
            // must be a MSEL owner
            if (!(await _authorizationService.AuthorizeAsync(_user, null, new ContentDeveloperRequirement())).Succeeded &&
                !(await MselOwnerRequirement.IsMet(_user.GetId(), userMselRole.MselId, _context)))
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
            _logger.LogWarning($"UserMselRole created by {_user.GetId()} = User: {userMselRole.UserId}, Role: {userMselRole.Role} on MSEL: {userMselRole.MselId}");
            return await GetAsync(userMselRoleEntity.Id, ct);
        }

        public async Task<bool> DeleteAsync(Guid id, CancellationToken ct)
        {
            var userMselRoleToDelete = await _context.UserMselRoles.SingleOrDefaultAsync(v => v.Id == id, ct);

            if (userMselRoleToDelete == null)
                throw new EntityNotFoundException<UserMselRole>();

            // must be a MSEL owner
            if (!(await _authorizationService.AuthorizeAsync(_user, null, new ContentDeveloperRequirement())).Succeeded &&
                !(await MselOwnerRequirement.IsMet(_user.GetId(), userMselRoleToDelete.MselId, _context)))
                throw new ForbiddenException();

            // start a transaction, because we may also update other data fields
            await _context.Database.BeginTransactionAsync();
            _context.UserMselRoles.Remove(userMselRoleToDelete);
            await _context.SaveChangesAsync(ct);
            // update the MSEL modified info
            await ServiceUtilities.SetMselModifiedAsync(userMselRoleToDelete.MselId, _user.GetId(), DateTime.UtcNow, _context, ct);
            // commit the transaction
            await _context.Database.CommitTransactionAsync(ct);
            _logger.LogWarning($"UserMselRole deleted by {_user.GetId()} = User: {userMselRoleToDelete.UserId}, Role: {userMselRoleToDelete.Role} on MSEL: {userMselRoleToDelete.MselId}");
            return true;
        }

    }
}

