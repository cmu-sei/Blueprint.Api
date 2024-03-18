// Copyright 2024 Carnegie Mellon University. All Rights Reserved.
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
    public interface IUserTeamRoleService
    {
        Task<IEnumerable<ViewModels.UserTeamRole>> GetByMselAsync(Guid mselId, CancellationToken ct);
        Task<ViewModels.UserTeamRole> GetAsync(Guid id, CancellationToken ct);
        Task<ViewModels.UserTeamRole> CreateAsync(ViewModels.UserTeamRole userTeamRole, CancellationToken ct);
        Task<bool> DeleteAsync(Guid id, CancellationToken ct);
    }

    public class UserTeamRoleService : IUserTeamRoleService
    {
        private readonly BlueprintContext _context;
        private readonly IAuthorizationService _authorizationService;
        private readonly ClaimsPrincipal _user;
        private readonly IMapper _mapper;
        private readonly ILogger<IUserTeamRoleService> _logger;

        public UserTeamRoleService(BlueprintContext context, IAuthorizationService authorizationService, IPrincipal user, ILogger<IUserTeamRoleService> logger, IMapper mapper)
        {
            _context = context;
            _authorizationService = authorizationService;
            _user = user as ClaimsPrincipal;
            _mapper = mapper;
            _logger = logger;
        }

        public async Task<IEnumerable<ViewModels.UserTeamRole>> GetByMselAsync(Guid mselId, CancellationToken ct)
        {
            // must be a msel viewer
            if (!(await _authorizationService.AuthorizeAsync(_user, null, new ContentDeveloperRequirement())).Succeeded &&
                !(await MselViewRequirement.IsMet(_user.GetId(), mselId, _context)))
                throw new ForbiddenException();

            var items = await _context.UserTeamRoles
                .Where(umr => umr.Team.MselId == mselId)
                .ToListAsync(ct);

            return _mapper.Map<IEnumerable<UserTeamRole>>(items);
        }

        public async Task<ViewModels.UserTeamRole> GetAsync(Guid id, CancellationToken ct)
        {
            var item = await _context.UserTeamRoles
                .Include(x => x.Team)
                .SingleOrDefaultAsync(o => o.Id == id, ct);

            // must be a msel viewer
            if (!(await _authorizationService.AuthorizeAsync(_user, null, new ContentDeveloperRequirement())).Succeeded &&
                !(await MselViewRequirement.IsMet(_user.GetId(), item.Team.MselId, _context)))
                throw new ForbiddenException();

            return _mapper.Map<UserTeamRole>(item);
        }

        public async Task<ViewModels.UserTeamRole> CreateAsync(ViewModels.UserTeamRole userTeamRole, CancellationToken ct)
        {
            // must be a msel owner
            var team = await _context.Teams.SingleOrDefaultAsync(t => t.Id == userTeamRole.TeamId);
            if (team == null)
                throw new EntityNotFoundException<Team>();
            if (!(await _authorizationService.AuthorizeAsync(_user, null, new ContentDeveloperRequirement())).Succeeded &&
                !(await MselOwnerRequirement.IsMet(_user.GetId(), team.MselId, _context)))
                throw new ForbiddenException();

            // start a transaction, because we may also update other data fields
            await _context.Database.BeginTransactionAsync();
            userTeamRole.Id = userTeamRole.Id != Guid.Empty ? userTeamRole.Id : Guid.NewGuid();
            userTeamRole.DateCreated = DateTime.UtcNow;
            userTeamRole.CreatedBy = _user.GetId();
            userTeamRole.DateModified = null;
            userTeamRole.ModifiedBy = null;
            var userTeamRoleEntity = _mapper.Map<UserTeamRoleEntity>(userTeamRole);

            _context.UserTeamRoles.Add(userTeamRoleEntity);
            await _context.SaveChangesAsync(ct);
            // update the team modified info
            await ServiceUtilities.SetMselModifiedAsync(team.MselId, userTeamRole.CreatedBy, userTeamRole.DateCreated, _context, ct);
            // commit the transaction
            await _context.Database.CommitTransactionAsync(ct);
            _logger.LogWarning($"UserTeamRole created by {_user.GetId()} = User: {userTeamRole.UserId}, Role: {userTeamRole.Role} on team: {userTeamRole.TeamId}");
            return await GetAsync(userTeamRoleEntity.Id, ct);
        }

        public async Task<bool> DeleteAsync(Guid id, CancellationToken ct)
        {
            var userTeamRoleToDelete = await _context.UserTeamRoles.Include(x => x.Team).SingleOrDefaultAsync(v => v.Id == id, ct);

            if (userTeamRoleToDelete == null)
                throw new EntityNotFoundException<UserTeamRole>();

            // must be a team owner
            if (!(await _authorizationService.AuthorizeAsync(_user, null, new ContentDeveloperRequirement())).Succeeded &&
                !(await MselOwnerRequirement.IsMet(_user.GetId(), userTeamRoleToDelete.Team.MselId, _context)))
                throw new ForbiddenException();

            // start a transaction, because we may also update other data fields
            await _context.Database.BeginTransactionAsync();
            _context.UserTeamRoles.Remove(userTeamRoleToDelete);
            await _context.SaveChangesAsync(ct);
            // update the team modified info
            await ServiceUtilities.SetMselModifiedAsync(userTeamRoleToDelete.Team.MselId, _user.GetId(), DateTime.UtcNow, _context, ct);
            // commit the transaction
            await _context.Database.CommitTransactionAsync(ct);
            _logger.LogWarning($"UserTeamRole deleted by {_user.GetId()} = User: {userTeamRoleToDelete.UserId}, Role: {userTeamRoleToDelete.Role} on team: {userTeamRoleToDelete.TeamId}");
            return true;
        }

    }
}

