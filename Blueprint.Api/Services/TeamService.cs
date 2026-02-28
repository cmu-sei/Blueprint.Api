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
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
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
        Task<ViewModels.Team> GetAsync(Guid id, CancellationToken ct);
        Task<IEnumerable<ViewModels.Team>> GetMineAsync(CancellationToken ct);
        Task<IEnumerable<ViewModels.Team>> GetByMselAsync(Guid mselId, bool hasSystemPermission, CancellationToken ct);
        Task<IEnumerable<ViewModels.Team>> GetByUserAsync(Guid userId, CancellationToken ct);
        Task<ViewModels.Team> CreateAsync(ViewModels.Team team, bool hasSystemPermission, CancellationToken ct);
        Task<ViewModels.Team> CreateFromUnitAsync(Guid unitId, Guid mselId, bool hasSystemPermission, CancellationToken ct);
        Task<ViewModels.Team> UpdateAsync(Guid id, ViewModels.Team team, bool hasSystemPermission, CancellationToken ct);
        Task<bool> DeleteAsync(Guid id, bool hasSystemPermission, CancellationToken ct);
    }

    public class TeamService : ITeamService
    {
        private readonly BlueprintContext _context;
        private readonly ClaimsPrincipal _user;
        private readonly IMapper _mapper;
        private readonly ILogger<ITeamService> _logger;

        public TeamService(BlueprintContext context, IPrincipal team, ILogger<ITeamService> logger, IMapper mapper)
        {
            _context = context;
            _user = team as ClaimsPrincipal;
            _mapper = mapper;
            _logger = logger;
        }

        public async Task<ViewModels.Team> GetAsync(Guid id, CancellationToken ct)
        {
            var item = await _context.Teams
                .Include(t => t.TeamUsers)
                .ThenInclude(tu => tu.User)
                .SingleOrDefaultAsync(o => o.Id == id, ct);
            return _mapper.Map<Team>(item);
        }

        public async Task<IEnumerable<ViewModels.Team>> GetMineAsync(CancellationToken ct)
        {
            var items = await _context.TeamUsers
                .Where(w => w.UserId == _user.GetId())
                .Include(tu => tu.Team)
                .Select(x => x.Team)
                .ToListAsync(ct);

            return _mapper.Map<IEnumerable<Team>>(items);
        }

        public async Task<IEnumerable<ViewModels.Team>> GetByMselAsync(Guid mselId, bool hasSystemPermission, CancellationToken ct)
        {
            if (
                    !hasSystemPermission &&
                    !(await MselViewRequirement.IsMet(_user.GetId(), mselId, _context))
               )
            {
                var msel = await _context.Msels.FindAsync(mselId);
                if (!msel.IsTemplate)
                    throw new ForbiddenException();
            }

            var items = await _context.Teams
                .Where(t => t.MselId == mselId)
                .Include(t => t.TeamUsers)
                .ThenInclude(tu => tu.User)
                .ToListAsync(ct);

            return _mapper.Map<IEnumerable<Team>>(items);
        }

        public async Task<IEnumerable<ViewModels.Team>> GetByUserAsync(Guid userId, CancellationToken ct)
        {
            var items = await _context.TeamUsers
                .Where(w => w.UserId == userId)
                .Select(x => x.Team)
                .ToListAsync(ct);

            return _mapper.Map<IEnumerable<Team>>(items);
        }

        public async Task<ViewModels.Team> CreateAsync(ViewModels.Team team, bool hasSystemPermission, CancellationToken ct)
        {
            if (
                    !hasSystemPermission &&
                    !(await MselOwnerRequirement.IsMet(_user.GetId(), team.MselId, _context))
               )
                throw new ForbiddenException();

            team.Id = team.Id != Guid.Empty ? team.Id : Guid.NewGuid();
            team.CreatedBy = _user.GetId();
            var teamEntity = _mapper.Map<TeamEntity>(team);

            _context.Teams.Add(teamEntity);
            await _context.SaveChangesAsync(ct);
            _logger.LogWarning($"Team ({teamEntity.Id}) created by {_user.GetId()}");
            return await GetAsync(teamEntity.Id, ct);
        }

        public async Task<ViewModels.Team> CreateFromUnitAsync(Guid unitId, Guid mselId, bool hasSystemPermission, CancellationToken ct)
        {
            if (
                    !hasSystemPermission &&
                    !(await MselOwnerRequirement.IsMet(_user.GetId(), mselId, _context))
               )
                throw new ForbiddenException();

            var unit = await _context.Units
                .Include(u => u.UnitUsers)
                .SingleOrDefaultAsync(u => u.Id == unitId, ct);
            if (unit == null)
                throw new EntityNotFoundException<Unit>("Unit not found for ID: " + unitId);
            // create the new team entity
            var team = new TeamEntity
            {
                Id = Guid.NewGuid(),
                Name = unit.Name,
                ShortName = unit.ShortName,
                MselId = mselId,
                CreatedBy = _user.GetId()
            };
            // add all of the UnitUsers as TeamUsers
            foreach (var unitUser in unit.UnitUsers)
            {
                var teamUser = new TeamUserEntity()
                {
                    TeamId = team.Id,
                    UserId = unitUser.UserId
                };
                team.TeamUsers.Add(teamUser);
            }
            // save the team and return the result
            _context.Teams.Add(team);
            await _context.SaveChangesAsync(ct);
            _logger.LogWarning($"Team ({team.Id}) created by {_user.GetId()}");

            return await GetAsync(team.Id, ct);
        }

        public async Task<ViewModels.Team> UpdateAsync(Guid id, ViewModels.Team team, bool hasSystemPermission, CancellationToken ct)
        {
            var teamToUpdate = await _context.Teams.SingleOrDefaultAsync(v => v.Id == id, ct);
            if (teamToUpdate == null)
                throw new EntityNotFoundException<Team>();

            if (
                    !hasSystemPermission &&
                    !(await MselOwnerRequirement.IsMet(_user.GetId(), team.MselId, _context))
            )
                throw new ForbiddenException();

            if (teamToUpdate.MselId != team.MselId)
                throw new ArgumentException("The MselId of the team cannot be changed!");

            _mapper.Map(team, teamToUpdate);

            _context.Teams.Update(teamToUpdate);
            await _context.SaveChangesAsync(ct);
            _logger.LogWarning($"Team ({teamToUpdate.Id}) updated by {_user.GetId()}");
            return await GetAsync(id, ct);
        }

        public async Task<bool> DeleteAsync(Guid id, bool hasSystemPermission, CancellationToken ct)
        {
            var teamToDelete = await _context.Teams.SingleOrDefaultAsync(v => v.Id == id, ct);
            if (teamToDelete == null)
                throw new EntityNotFoundException<Team>();

            if (
                    !hasSystemPermission &&
                    !await MselOwnerRequirement.IsMet(_user.GetId(), teamToDelete.MselId, _context)
               )
                throw new ForbiddenException();

            _context.Teams.Remove(teamToDelete);
            await _context.SaveChangesAsync(ct);
            _logger.LogWarning($"Team ({teamToDelete.Id}) deleted by {_user.GetId()}");
            return true;
        }

    }
}

