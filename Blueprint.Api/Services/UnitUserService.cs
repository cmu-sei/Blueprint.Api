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
    public interface IUnitUserService
    {
        Task<IEnumerable<ViewModels.UnitUser>> GetAsync(CancellationToken ct);
        Task<ViewModels.UnitUser> GetAsync(Guid id, CancellationToken ct);
        Task<ViewModels.UnitUser> CreateAsync(ViewModels.UnitUser unitUser, CancellationToken ct);
        Task<bool> DeleteAsync(Guid id, CancellationToken ct);
        Task<bool> DeleteByIdsAsync(Guid unitId, Guid userId, CancellationToken ct);
    }

    public class UnitUserService : IUnitUserService
    {
        private readonly BlueprintContext _context;
        private readonly IAuthorizationService _authorizationService;
        private readonly ClaimsPrincipal _user;
        private readonly IMapper _mapper;
        private readonly ILogger<IUnitUserService> _logger;

        public UnitUserService(BlueprintContext context, IAuthorizationService authorizationService, IPrincipal user, ILogger<IUnitUserService> logger, IMapper mapper)
        {
            _context = context;
            _authorizationService = authorizationService;
            _user = user as ClaimsPrincipal;
            _mapper = mapper;
            _logger = logger;
        }

        public async Task<IEnumerable<ViewModels.UnitUser>> GetAsync(CancellationToken ct)
        {
            if (!(await _authorizationService.AuthorizeAsync(_user, null, new FullRightsRequirement())).Succeeded)
                throw new ForbiddenException();

            var items = await _context.UnitUsers
                .ToListAsync(ct);

            return _mapper.Map<IEnumerable<UnitUser>>(items);
        }

        public async Task<ViewModels.UnitUser> GetAsync(Guid id, CancellationToken ct)
        {
            if (!(await _authorizationService.AuthorizeAsync(_user, null, new FullRightsRequirement())).Succeeded)
                throw new ForbiddenException();

            var item = await _context.UnitUsers
                .Include(tu => tu.User)
                .SingleOrDefaultAsync(o => o.Id == id, ct);

            return _mapper.Map<UnitUser>(item);
        }

        public async Task<ViewModels.UnitUser> CreateAsync(ViewModels.UnitUser unitUser, CancellationToken ct)
        {
            if (!(await _authorizationService.AuthorizeAsync(_user, null, new FullRightsRequirement())).Succeeded)
                throw new ForbiddenException();

            // make sure this would not add a duplicate user on any pending or active msels
            var requestedUser = await _context.Users.FindAsync(unitUser.UserId);
            var requestedUnit = await _context.Units.FindAsync(unitUser.UnitId);
            // okay to add this UnitUser
            unitUser.Id = unitUser.Id != Guid.Empty ? unitUser.Id : Guid.NewGuid();
            unitUser.DateCreated = DateTime.UtcNow;
            unitUser.CreatedBy = _user.GetId();
            unitUser.DateModified = null;
            unitUser.ModifiedBy = null;
            var unitUserEntity = _mapper.Map<UnitUserEntity>(unitUser);

            _context.UnitUsers.Add(unitUserEntity);
            await _context.SaveChangesAsync(ct);
            _logger.LogWarning($"User {unitUser.UserId} added to unit {unitUser.UnitId} by {_user.GetId()}");
            return await GetAsync(unitUserEntity.Id, ct);
        }

        public async Task<bool> DeleteAsync(Guid id, CancellationToken ct)
        {
            if (!(await _authorizationService.AuthorizeAsync(_user, null, new FullRightsRequirement())).Succeeded)
                throw new ForbiddenException();

            var unitUserToDelete = await _context.UnitUsers.SingleOrDefaultAsync(v => v.Id == id, ct);

            if (unitUserToDelete == null)
                throw new EntityNotFoundException<UnitUser>();

            _context.UnitUsers.Remove(unitUserToDelete);
            await _context.SaveChangesAsync(ct);
            _logger.LogWarning($"User {unitUserToDelete.UserId} removed from unit {unitUserToDelete.UnitId} by {_user.GetId()}");
            return true;
        }

        public async Task<bool> DeleteByIdsAsync(Guid unitId, Guid userId, CancellationToken ct)
        {
            if (!(await _authorizationService.AuthorizeAsync(_user, null, new FullRightsRequirement())).Succeeded)
                throw new ForbiddenException();

            var unitUserToDelete = await _context.UnitUsers.SingleOrDefaultAsync(v => (v.UserId == userId) && (v.UnitId == unitId), ct);

            if (unitUserToDelete == null)
                throw new EntityNotFoundException<UnitUser>();

            _context.UnitUsers.Remove(unitUserToDelete);
            await _context.SaveChangesAsync(ct);
            _logger.LogWarning($"User {unitUserToDelete.UserId} removed from unit {unitUserToDelete.UnitId} by {_user.GetId()}");
            return true;
        }

    }
}

