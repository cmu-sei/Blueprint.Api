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
    public interface ITeamUserService
    {
        Task<IEnumerable<ViewModels.TeamUser>> GetByMselAsync(Guid mselId, CancellationToken ct);
        Task<IEnumerable<ViewModels.TeamUser>> GetByTeamAsync(Guid teamId, CancellationToken ct);
        Task<ViewModels.TeamUser> GetAsync(Guid id, CancellationToken ct);
        Task<ViewModels.TeamUser> CreateAsync(ViewModels.TeamUser teamUser, CancellationToken ct);
        Task<bool> DeleteAsync(Guid id, CancellationToken ct);
        Task<bool> DeleteByIdsAsync(Guid teamId, Guid userId, CancellationToken ct);
    }

    public class TeamUserService : ITeamUserService
    {
        private readonly BlueprintContext _context;
        private readonly IAuthorizationService _authorizationService;
        private readonly ClaimsPrincipal _user;
        private readonly IMapper _mapper;
        private readonly ILogger<ITeamUserService> _logger;

        public TeamUserService(BlueprintContext context, IAuthorizationService authorizationService, IPrincipal user, ILogger<ITeamUserService> logger, IMapper mapper)
        {
            _context = context;
            _authorizationService = authorizationService;
            _user = user as ClaimsPrincipal;
            _mapper = mapper;
            _logger = logger;
        }

        public async Task<IEnumerable<ViewModels.TeamUser>> GetByMselAsync(Guid mselId, CancellationToken ct)
        {
            if (
                    !(await MselUserRequirement.IsMet(_user.GetId(), mselId, _context)) &&
                    !(await _authorizationService.AuthorizeAsync(_user, null, new ContentDeveloperRequirement())).Succeeded
               )
                throw new ForbiddenException();

            var items = await _context.TeamUsers
                .Where(tu => tu.Team.MselId == mselId)
                .ToListAsync(ct);

            return _mapper.Map<IEnumerable<TeamUser>>(items);
        }

        public async Task<IEnumerable<ViewModels.TeamUser>> GetByTeamAsync(Guid teamId, CancellationToken ct)
        {
            var team = await _context.Teams.SingleOrDefaultAsync(t => t.Id == teamId);
            if (team == null)
                throw new EntityNotFoundException<Team>();

            if (
                    !(await MselUserRequirement.IsMet(_user.GetId(), team.MselId, _context)) &&
                    !(await _authorizationService.AuthorizeAsync(_user, null, new ContentDeveloperRequirement())).Succeeded
               )
                throw new ForbiddenException();

            var items = await _context.TeamUsers
                .Where(tu => tu.TeamId == teamId)
                .Include(tu => tu.User)
                .ToListAsync(ct);

            return _mapper.Map<IEnumerable<TeamUser>>(items);
        }

        public async Task<ViewModels.TeamUser> GetAsync(Guid id, CancellationToken ct)
        {
            var item = await _context.TeamUsers
                .Include(tu => tu.User)
                .SingleOrDefaultAsync(o => o.Id == id, ct);
            if (item == null)
                throw new EntityNotFoundException<TeamUser>("TeamUser not found " + id.ToString());

            var mselId = (await _context.Teams.SingleOrDefaultAsync(t => t.Id == item.TeamId)).MselId;
            if (
                    !(await MselViewRequirement.IsMet(_user.GetId(), mselId, _context)) &&
                    !(await _authorizationService.AuthorizeAsync(_user, null, new ContentDeveloperRequirement())).Succeeded
               )
                throw new ForbiddenException();

            return _mapper.Map<TeamUser>(item);
        }

        public async Task<ViewModels.TeamUser> CreateAsync(ViewModels.TeamUser teamUser, CancellationToken ct)
        {
            if (!(await _authorizationService.AuthorizeAsync(_user, null, new FullRightsRequirement())).Succeeded)
                throw new ForbiddenException();

            // make sure this would not add a duplicate user on any pending or active msels
            var requestedUser = await _context.Users.FindAsync(teamUser.UserId);
            var requestedTeam = await _context.Teams.FindAsync(teamUser.TeamId);
            // okay to add this TeamUser
            teamUser.Id = teamUser.Id != Guid.Empty ? teamUser.Id : Guid.NewGuid();
            teamUser.DateCreated = DateTime.UtcNow;
            teamUser.CreatedBy = _user.GetId();
            teamUser.DateModified = null;
            teamUser.ModifiedBy = null;
            var teamUserEntity = _mapper.Map<TeamUserEntity>(teamUser);

            _context.TeamUsers.Add(teamUserEntity);
            await _context.SaveChangesAsync(ct);
            _logger.LogWarning($"User {teamUser.UserId} added to team {teamUser.TeamId} by {_user.GetId()}");
            return await GetAsync(teamUserEntity.Id, ct);
        }

        public async Task<bool> DeleteAsync(Guid id, CancellationToken ct)
        {
            if (!(await _authorizationService.AuthorizeAsync(_user, null, new FullRightsRequirement())).Succeeded)
                throw new ForbiddenException();

            var teamUserToDelete = await _context.TeamUsers.SingleOrDefaultAsync(v => v.Id == id, ct);

            if (teamUserToDelete == null)
                throw new EntityNotFoundException<TeamUser>();

            _context.TeamUsers.Remove(teamUserToDelete);
            await _context.SaveChangesAsync(ct);
            _logger.LogWarning($"User {teamUserToDelete.UserId} removed from team {teamUserToDelete.TeamId} by {_user.GetId()}");
            return true;
        }

        public async Task<bool> DeleteByIdsAsync(Guid teamId, Guid userId, CancellationToken ct)
        {
            if (!(await _authorizationService.AuthorizeAsync(_user, null, new FullRightsRequirement())).Succeeded)
                throw new ForbiddenException();

            var teamUserToDelete = await _context.TeamUsers.SingleOrDefaultAsync(v => (v.UserId == userId) && (v.TeamId == teamId), ct);

            if (teamUserToDelete == null)
                throw new EntityNotFoundException<TeamUser>();

            _context.TeamUsers.Remove(teamUserToDelete);
            await _context.SaveChangesAsync(ct);
            _logger.LogWarning($"User {teamUserToDelete.UserId} removed from team {teamUserToDelete.TeamId} by {_user.GetId()}");
            return true;
        }

    }
}

