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
    public interface ITeamService
    {
        Task<IEnumerable<ViewModels.Team>> GetAsync(CancellationToken ct);
        Task<ViewModels.Team> GetAsync(Guid id, CancellationToken ct);
        Task<IEnumerable<ViewModels.Team>> GetMineAsync(CancellationToken ct);
        Task<IEnumerable<ViewModels.Team>> GetByUserAsync(Guid userId, CancellationToken ct);
        Task<ViewModels.Team> CreateAsync(ViewModels.Team team, CancellationToken ct);
        Task<ViewModels.Team> UpdateAsync(Guid id, ViewModels.Team team, CancellationToken ct);
        Task<bool> DeleteAsync(Guid id, CancellationToken ct);
    }

    public class TeamService : ITeamService
    {
        private readonly BlueprintContext _context;
        private readonly ClaimsPrincipal _user;
        private readonly IAuthorizationService _authorizationService;
        private readonly IMapper _mapper;

        public TeamService(BlueprintContext context, IPrincipal team, IAuthorizationService authorizationService, IMapper mapper)
        {
            _context = context;
            _user = team as ClaimsPrincipal;
            _authorizationService = authorizationService;
            _mapper = mapper;
        }

        public async Task<IEnumerable<ViewModels.Team>> GetAsync(CancellationToken ct)
        {
            if(!(await _authorizationService.AuthorizeAsync(_user, null, new BaseUserRequirement())).Succeeded)
                throw new ForbiddenException();

            var items = await _context.Teams
                .ProjectTo<ViewModels.Team>(_mapper.ConfigurationProvider)
                .ToArrayAsync(ct);
            return items;
        }

        public async Task<ViewModels.Team> GetAsync(Guid id, CancellationToken ct)
        {
            if (!(await _authorizationService.AuthorizeAsync(_user, null, new ContentDeveloperRequirement())).Succeeded)
                throw new ForbiddenException();

            var item = await _context.Teams
                .ProjectTo<ViewModels.Team>(_mapper.ConfigurationProvider, dest => dest.Users)
                .SingleOrDefaultAsync(o => o.Id == id, ct);
            return item;
        }

        public async Task<IEnumerable<ViewModels.Team>> GetMineAsync(CancellationToken ct)
        {
            if(!(await _authorizationService.AuthorizeAsync(_user, null, new BaseUserRequirement())).Succeeded)
                throw new ForbiddenException();

            var items = await _context.TeamUsers
                .Where(w => w.UserId == _user.GetId())
                .Include(tu => tu.Team)
                .Select(x => x.Team)
                .ToListAsync(ct);

            return _mapper.Map<IEnumerable<Team>>(items);
        }

        public async Task<IEnumerable<ViewModels.Team>> GetByUserAsync(Guid userId, CancellationToken ct)
        {
            if (!(await _authorizationService.AuthorizeAsync(_user, null, new FullRightsRequirement())).Succeeded)
                throw new ForbiddenException();

            var items = await _context.TeamUsers
                .Where(w => w.UserId == userId)
                .Select(x => x.Team)
                .ToListAsync(ct);

            return _mapper.Map<IEnumerable<Team>>(items);
        }

        public async Task<ViewModels.Team> CreateAsync(ViewModels.Team team, CancellationToken ct)
        {
            if (!(await _authorizationService.AuthorizeAsync(_user, null, new FullRightsRequirement())).Succeeded)
                throw new ForbiddenException();

            team.Id = team.Id != Guid.Empty ? team.Id : Guid.NewGuid();
            team.DateCreated = DateTime.UtcNow;
            team.CreatedBy = _user.GetId();
            team.DateModified = null;
            team.ModifiedBy = null;
            var teamEntity = _mapper.Map<TeamEntity>(team);

            _context.Teams.Add(teamEntity);
            await _context.SaveChangesAsync(ct);

            return await GetAsync(teamEntity.Id, ct);
        }

        public async Task<ViewModels.Team> UpdateAsync(Guid id, ViewModels.Team team, CancellationToken ct)
        {
            if (!(await _authorizationService.AuthorizeAsync(_user, null, new FullRightsRequirement())).Succeeded)
                throw new ForbiddenException();

            // Don't allow changing your own Id
            if (id == _user.GetId() && id != team.Id)
            {
                throw new ForbiddenException("You cannot change your own Id");
            }

            var teamToUpdate = await _context.Teams.SingleOrDefaultAsync(v => v.Id == id, ct);

            if (teamToUpdate == null)
                throw new EntityNotFoundException<Team>();

            team.CreatedBy = teamToUpdate.CreatedBy;
            team.DateCreated = teamToUpdate.DateCreated;
            team.ModifiedBy = _user.GetId();
            team.DateModified = DateTime.UtcNow;
            _mapper.Map(team, teamToUpdate);

            _context.Teams.Update(teamToUpdate);
            await _context.SaveChangesAsync(ct);

            return await GetAsync(id, ct);
        }

        public async Task<bool> DeleteAsync(Guid id, CancellationToken ct)
        {
            if (!(await _authorizationService.AuthorizeAsync(_user, null, new FullRightsRequirement())).Succeeded)
                throw new ForbiddenException();

            if (id == _user.GetId())
            {
                throw new ForbiddenException("You cannot delete your own account");
            }

            var teamToDelete = await _context.Teams.SingleOrDefaultAsync(v => v.Id == id, ct);

            if (teamToDelete == null)
                throw new EntityNotFoundException<Team>();

            _context.Teams.Remove(teamToDelete);
            await _context.SaveChangesAsync(ct);

            return true;
        }

    }
}

