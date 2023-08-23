// Copyright 2023 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

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
    public interface IMselTeamService
    {
        Task<IEnumerable<ViewModels.MselTeam>> GetByMselAsync(Guid mselId, CancellationToken ct);
        Task<ViewModels.MselTeam> GetAsync(Guid id, CancellationToken ct);
        Task<ViewModels.MselTeam> CreateAsync(ViewModels.MselTeam mselTeam, CancellationToken ct);
        Task<ViewModels.MselTeam> UpdateAsync(Guid id, ViewModels.MselTeam mselTeam, CancellationToken ct);
        Task<bool> DeleteAsync(Guid id, CancellationToken ct);
        Task<bool> DeleteByIdsAsync(Guid mselId, Guid teamId, CancellationToken ct);
    }

    public class MselTeamService : IMselTeamService
    {
        private readonly BlueprintContext _context;
        private readonly IAuthorizationService _authorizationService;
        private readonly ClaimsPrincipal _user;
        private readonly IMapper _mapper;
        private readonly ILogger<IMselTeamService> _logger;

        public MselTeamService(BlueprintContext context, IAuthorizationService authorizationService, IPrincipal user, ILogger<IMselTeamService> logger, IMapper mapper)
        {
            _context = context;
            _authorizationService = authorizationService;
            _user = user as ClaimsPrincipal;
            _mapper = mapper;
            _logger = logger;
        }

        public async Task<IEnumerable<ViewModels.MselTeam>> GetByMselAsync(Guid mselId, CancellationToken ct)
        {
            var msel = await _context.Msels.SingleOrDefaultAsync(v => v.Id == mselId, ct);
            if (msel == null)
                throw new EntityNotFoundException<MselEntity>();

            // user must be a Content Developer or a MSEL owner
            if (!(await _authorizationService.AuthorizeAsync(_user, null, new ContentDeveloperRequirement())).Succeeded &&
                !(await MselOwnerRequirement.IsMet(_user.GetId(), msel.Id, _context)))
                throw new ForbiddenException();

            var items = await _context.MselTeams
                .Where(tc => tc.MselId == mselId)
                .Include(mt => mt.Team)
                .ThenInclude(t => t.TeamUsers)
                .ThenInclude(tu => tu.User)
                .ToListAsync(ct);

            return _mapper.Map<IEnumerable<MselTeam>>(items);
        }

        public async Task<ViewModels.MselTeam> GetAsync(Guid id, CancellationToken ct)
        {
            var mselTeam = await _context.MselTeams.SingleOrDefaultAsync(v => v.Id == id, ct);
            if (mselTeam == null)
                throw new EntityNotFoundException<MselEntity>();

            // user must be a Content Developer or a MSEL Viewer
            if (!(await _authorizationService.AuthorizeAsync(_user, null, new ContentDeveloperRequirement())).Succeeded &&
                !(await MselViewRequirement.IsMet(_user.GetId(), mselTeam.MselId, _context)))
                throw new ForbiddenException();

            var item = await _context.MselTeams
                .Include(mt => mt.Team)
                .ThenInclude(t => t.TeamUsers)
                .ThenInclude(tu => tu.User)
                .SingleOrDefaultAsync(o => o.Id == id, ct);

            return _mapper.Map<MselTeam>(item);
        }

        public async Task<ViewModels.MselTeam> CreateAsync(ViewModels.MselTeam mselTeam, CancellationToken ct)
        {
            var msel = await _context.Msels.SingleOrDefaultAsync(v => v.Id == mselTeam.MselId, ct);
            if (msel == null)
                throw new EntityNotFoundException<MselEntity>();

            var team = await _context.Teams.SingleOrDefaultAsync(v => v.Id == mselTeam.TeamId, ct);
            if (team == null)
                throw new EntityNotFoundException<TeamEntity>();

            // user must be a Content Developer or a MSEL owner
            if (!(await _authorizationService.AuthorizeAsync(_user, null, new ContentDeveloperRequirement())).Succeeded &&
                !(await MselOwnerRequirement.IsMet(_user.GetId(), msel.Id, _context)))
                throw new ForbiddenException();

            if (await _context.MselTeams.AnyAsync(mt => mt.TeamId == team.Id && mt.MselId == msel.Id))
                throw new ArgumentException("MSEL Team already exists.");

            var mselTeamEntity = _mapper.Map<MselTeamEntity>(mselTeam);
            mselTeamEntity.Id = mselTeamEntity.Id != Guid.Empty ? mselTeamEntity.Id : Guid.NewGuid();

            _context.MselTeams.Add(mselTeamEntity);
            await _context.SaveChangesAsync(ct);
            _logger.LogWarning($"Team {mselTeam.TeamId} added to MSEL {mselTeam.MselId} by {_user.GetId()}");
            return await GetAsync(mselTeamEntity.Id, ct);
        }

        public async Task<ViewModels.MselTeam> UpdateAsync(Guid id, ViewModels.MselTeam mselTeam, CancellationToken ct)
        {
            // user must be a Content Developer or a MSEL owner
            if (!(await _authorizationService.AuthorizeAsync(_user, null, new ContentDeveloperRequirement())).Succeeded &&
                !(await MselOwnerRequirement.IsMet(_user.GetId(), mselTeam.MselId, _context)))
                throw new ForbiddenException();

            var mselTeamToUpdate = await _context.MselTeams.SingleOrDefaultAsync(v => v.Id == id, ct);
            if (mselTeamToUpdate == null)
                throw new EntityNotFoundException<MselTeam>();

            _mapper.Map(mselTeam, mselTeamToUpdate);

            _context.MselTeams.Update(mselTeamToUpdate);
            await _context.SaveChangesAsync(ct);

            mselTeam = await GetAsync(mselTeamToUpdate.Id, ct);
            _logger.LogWarning($"Team {mselTeam.TeamId} updated to CiteTeamType {mselTeam.CiteTeamTypeId} on MSEL {mselTeam.MselId} by {_user.GetId()}");
            return mselTeam;
        }

        public async Task<bool> DeleteAsync(Guid id, CancellationToken ct)
        {
            var mselTeamToDelete = await _context.MselTeams.SingleOrDefaultAsync(v => v.Id == id, ct);
            if (mselTeamToDelete == null)
                throw new EntityNotFoundException<MselTeam>();

            // user must be a Content Developer or a MSEL owner
            if (!(await _authorizationService.AuthorizeAsync(_user, null, new ContentDeveloperRequirement())).Succeeded &&
                !(await MselOwnerRequirement.IsMet(_user.GetId(), mselTeamToDelete.MselId, _context)))
                throw new ForbiddenException();

            _context.MselTeams.Remove(mselTeamToDelete);
            await _context.SaveChangesAsync(ct);
            _logger.LogWarning($"Team {mselTeamToDelete.TeamId} removed from MSEL {mselTeamToDelete.MselId} by {_user.GetId()}");
            return true;
        }

        public async Task<bool> DeleteByIdsAsync(Guid mselId, Guid teamId, CancellationToken ct)
        {
            var mselTeamToDelete = await _context.MselTeams.SingleOrDefaultAsync(v => (v.TeamId == teamId) && (v.MselId == mselId), ct);
            if (mselTeamToDelete == null)
                throw new EntityNotFoundException<MselTeam>();

            // user must be a Content Developer or a MSEL owner
            if (!(await _authorizationService.AuthorizeAsync(_user, null, new ContentDeveloperRequirement())).Succeeded &&
                !(await MselOwnerRequirement.IsMet(_user.GetId(), mselTeamToDelete.MselId, _context)))
                throw new ForbiddenException();

            _context.MselTeams.Remove(mselTeamToDelete);
            await _context.SaveChangesAsync(ct);
            _logger.LogWarning($"Team {mselTeamToDelete.TeamId} removed from MSEL {mselTeamToDelete.MselId} by {_user.GetId()}");
            return true;
        }

    }
}

