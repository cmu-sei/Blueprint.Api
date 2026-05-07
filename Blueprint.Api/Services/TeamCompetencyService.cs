// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using AutoMapper;
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
    public interface ITeamCompetencyService
    {
        Task<IEnumerable<ViewModels.TeamCompetency>> GetByTeamAsync(Guid teamId, bool hasSystemPermission, CancellationToken ct);
        Task<IEnumerable<ViewModels.TeamCompetency>> GetByMselAsync(Guid mselId, bool hasSystemPermission, CancellationToken ct);
        Task<ViewModels.TeamCompetency> GetAsync(Guid id, bool hasSystemPermission, CancellationToken ct);
        Task<ViewModels.TeamCompetency> CreateAsync(ViewModels.TeamCompetency teamCompetency, bool hasSystemPermission, CancellationToken ct);
        Task<bool> DeleteAsync(Guid id, bool hasSystemPermission, CancellationToken ct);
        Task<bool> DeleteByIdsAsync(Guid teamId, Guid competencyId, bool hasSystemPermission, CancellationToken ct);
    }

    public class TeamCompetencyService : ITeamCompetencyService
    {
        private readonly BlueprintContext _context;
        private readonly ClaimsPrincipal _user;
        private readonly IMapper _mapper;
        private readonly ILogger<ITeamCompetencyService> _logger;

        public TeamCompetencyService(BlueprintContext context, IPrincipal user, ILogger<ITeamCompetencyService> logger, IMapper mapper)
        {
            _context = context;
            _user = user as ClaimsPrincipal;
            _mapper = mapper;
            _logger = logger;
        }

        public async Task<IEnumerable<ViewModels.TeamCompetency>> GetByTeamAsync(Guid teamId, bool hasSystemPermission, CancellationToken ct)
        {
            var team = await _context.Teams.SingleOrDefaultAsync(t => t.Id == teamId, ct);
            if (team == null)
                throw new EntityNotFoundException<TeamEntity>();

            if (!hasSystemPermission && !(await MselViewRequirement.IsMet(_user.GetId(), team.MselId, _context)))
                throw new ForbiddenException();

            var items = await _context.TeamCompetencies
                .Where(tc => tc.TeamId == teamId)
                .Include(tc => tc.Competency)
                .ToListAsync(ct);

            return _mapper.Map<IEnumerable<TeamCompetency>>(items);
        }

        public async Task<IEnumerable<ViewModels.TeamCompetency>> GetByMselAsync(Guid mselId, bool hasSystemPermission, CancellationToken ct)
        {
            var msel = await _context.Msels.SingleOrDefaultAsync(m => m.Id == mselId, ct);
            if (msel == null)
                throw new EntityNotFoundException<MselEntity>();

            if (!hasSystemPermission && !(await MselViewRequirement.IsMet(_user.GetId(), mselId, _context)))
                throw new ForbiddenException();

            var teamIds = await _context.Teams
                .Where(t => t.MselId == mselId)
                .Select(t => t.Id)
                .ToListAsync(ct);

            var items = await _context.TeamCompetencies
                .Where(tc => teamIds.Contains(tc.TeamId))
                .Include(tc => tc.Competency)
                .ToListAsync(ct);

            return _mapper.Map<IEnumerable<TeamCompetency>>(items);
        }

        public async Task<ViewModels.TeamCompetency> GetAsync(Guid id, bool hasSystemPermission, CancellationToken ct)
        {
            var item = await _context.TeamCompetencies
                .Include(tc => tc.Competency)
                .Include(tc => tc.Team)
                .SingleOrDefaultAsync(tc => tc.Id == id, ct);

            if (item == null)
                throw new EntityNotFoundException<TeamCompetency>();

            if (!hasSystemPermission && !(await MselViewRequirement.IsMet(_user.GetId(), item.Team.MselId, _context)))
                throw new ForbiddenException();

            return _mapper.Map<TeamCompetency>(item);
        }

        public async Task<ViewModels.TeamCompetency> CreateAsync(ViewModels.TeamCompetency teamCompetency, bool hasSystemPermission, CancellationToken ct)
        {
            var team = await _context.Teams.SingleOrDefaultAsync(t => t.Id == teamCompetency.TeamId, ct);
            if (team == null)
                throw new EntityNotFoundException<TeamEntity>();

            var competency = await _context.Competencies.SingleOrDefaultAsync(c => c.Id == teamCompetency.CompetencyId, ct);
            if (competency == null)
                throw new EntityNotFoundException<CompetencyEntity>();

            if (!hasSystemPermission && !(await MselOwnerRequirement.IsMet(_user.GetId(), team.MselId, _context)))
                throw new ForbiddenException();

            if (await _context.TeamCompetencies.AnyAsync(tc => tc.TeamId == team.Id && tc.CompetencyId == competency.Id, ct))
                throw new ArgumentException("Team Competency already exists.");

            var entity = _mapper.Map<TeamCompetencyEntity>(teamCompetency);
            entity.Id = entity.Id != Guid.Empty ? entity.Id : Guid.NewGuid();

            _context.TeamCompetencies.Add(entity);
            await _context.SaveChangesAsync(ct);
            _logger.LogWarning($"Competency {teamCompetency.CompetencyId} added to Team {teamCompetency.TeamId} by {_user.GetId()}");
            return await GetAsync(entity.Id, true, ct);
        }

        public async Task<bool> DeleteAsync(Guid id, bool hasSystemPermission, CancellationToken ct)
        {
            var item = await _context.TeamCompetencies
                .Include(tc => tc.Team)
                .SingleOrDefaultAsync(tc => tc.Id == id, ct);
            if (item == null)
                throw new EntityNotFoundException<TeamCompetency>();

            if (!hasSystemPermission && !(await MselOwnerRequirement.IsMet(_user.GetId(), item.Team.MselId, _context)))
                throw new ForbiddenException();

            _context.TeamCompetencies.Remove(item);
            await _context.SaveChangesAsync(ct);
            _logger.LogWarning($"Competency {item.CompetencyId} removed from Team {item.TeamId} by {_user.GetId()}");
            return true;
        }

        public async Task<bool> DeleteByIdsAsync(Guid teamId, Guid competencyId, bool hasSystemPermission, CancellationToken ct)
        {
            var item = await _context.TeamCompetencies
                .Include(tc => tc.Team)
                .SingleOrDefaultAsync(tc => tc.TeamId == teamId && tc.CompetencyId == competencyId, ct);
            if (item == null)
                throw new EntityNotFoundException<TeamCompetency>();

            if (!hasSystemPermission && !(await MselOwnerRequirement.IsMet(_user.GetId(), item.Team.MselId, _context)))
                throw new ForbiddenException();

            _context.TeamCompetencies.Remove(item);
            await _context.SaveChangesAsync(ct);
            _logger.LogWarning($"Competency {item.CompetencyId} removed from Team {item.TeamId} by {_user.GetId()}");
            return true;
        }
    }
}
