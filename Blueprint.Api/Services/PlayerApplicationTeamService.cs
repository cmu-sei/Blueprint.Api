// Copyright 2024 Carnegie Mellon University. All Rights Reserved.
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
using Blueprint.Api.Data;
using Blueprint.Api.Data.Models;
using Blueprint.Api.Infrastructure.Authorization;
using Blueprint.Api.Infrastructure.Exceptions;
using Blueprint.Api.Infrastructure.Extensions;
using Blueprint.Api.ViewModels;

namespace Blueprint.Api.Services
{
    public interface IPlayerApplicationTeamService
    {
        Task<IEnumerable<ViewModels.PlayerApplicationTeam>> GetAsync(CancellationToken ct);
        Task<ViewModels.PlayerApplicationTeam> GetAsync(Guid id, CancellationToken ct);
        Task<IEnumerable<ViewModels.PlayerApplicationTeam>> GetByPlayerApplicationAsync(Guid playerApplicationId, CancellationToken ct);
        Task<IEnumerable<ViewModels.PlayerApplicationTeam>> GetByMselAsync(Guid mselId, CancellationToken ct);
        Task<ViewModels.PlayerApplicationTeam> CreateAsync(ViewModels.PlayerApplicationTeam playerApplicationTeam, CancellationToken ct);
        Task<ViewModels.PlayerApplicationTeam> UpdateAsync(Guid id, ViewModels.PlayerApplicationTeam playerApplicationTeam, CancellationToken ct);
        Task<bool> DeleteAsync(Guid id, CancellationToken ct);
        Task<bool> DeleteByIdsAsync(Guid playerApplicationId, Guid teamId, CancellationToken ct);
    }

    public class PlayerApplicationTeamService : IPlayerApplicationTeamService
    {
        private readonly BlueprintContext _context;
        private readonly IAuthorizationService _authorizationService;
        private readonly ClaimsPrincipal _user;
        private readonly IMapper _mapper;

        public PlayerApplicationTeamService(BlueprintContext context, IAuthorizationService authorizationService, IPrincipal team, IMapper mapper)
        {
            _context = context;
            _authorizationService = authorizationService;
            _user = team as ClaimsPrincipal;
            _mapper = mapper;
        }

        public async Task<IEnumerable<ViewModels.PlayerApplicationTeam>> GetAsync(CancellationToken ct)
        {
            if (!(await _authorizationService.AuthorizeAsync(_user, null, new ContentDeveloperRequirement())).Succeeded)
                throw new ForbiddenException();

            var items = await _context.PlayerApplicationTeams
                .ToListAsync(ct);

            return _mapper.Map<IEnumerable<PlayerApplicationTeam>>(items);
        }

        public async Task<ViewModels.PlayerApplicationTeam> GetAsync(Guid id, CancellationToken ct)
        {
            var item = await _context.PlayerApplicationTeams
                .Include(ct => ct.PlayerApplication)
                .SingleOrDefaultAsync(o => o.Id == id, ct);
            if (
                    !(await MselViewRequirement.IsMet(_user.GetId(), item.PlayerApplication.MselId, _context)) &&
                    !(await _authorizationService.AuthorizeAsync(_user, null, new ContentDeveloperRequirement())).Succeeded
               )
            {
                var mselCheck = await _context.Msels.FindAsync(item.PlayerApplication.MselId);
                if (!mselCheck.IsTemplate)
                    throw new ForbiddenException();
            }

            return _mapper.Map<PlayerApplicationTeam>(item);
        }

        public async Task<IEnumerable<ViewModels.PlayerApplicationTeam>> GetByPlayerApplicationAsync(Guid playerApplicationId, CancellationToken ct)
        {
            var playerApplication = await _context.PlayerApplications.FirstOrDefaultAsync(c => c.Id == playerApplicationId, ct);
            if (
                    !(await MselViewRequirement.IsMet(_user.GetId(), playerApplication.MselId, _context)) &&
                    !(await _authorizationService.AuthorizeAsync(_user, null, new ContentDeveloperRequirement())).Succeeded
               )
            {
                var mselCheck = await _context.Msels.FindAsync(playerApplication.MselId);
                if (!mselCheck.IsTemplate)
                    throw new ForbiddenException();
            }
            var items = await _context.PlayerApplicationTeams
                .Where(et => et.PlayerApplicationId == playerApplicationId)
                .ToListAsync(ct);

            return _mapper.Map<IEnumerable<PlayerApplicationTeam>>(items);
        }

        public async Task<IEnumerable<ViewModels.PlayerApplicationTeam>> GetByMselAsync(Guid mselId, CancellationToken ct)
        {
            if (
                    !(await MselViewRequirement.IsMet(_user.GetId(), mselId, _context)) &&
                    !(await _authorizationService.AuthorizeAsync(_user, null, new ContentDeveloperRequirement())).Succeeded
               )
            {
                var mselCheck = await _context.Msels.FindAsync(mselId);
                if (!mselCheck.IsTemplate)
                    throw new ForbiddenException();
            }
            var playerApplicationIds = await _context.PlayerApplications
                .Where(c => c.MselId == mselId)
                .Select(c => c.Id)
                .ToListAsync(ct);
            var items = await _context.PlayerApplicationTeams
                .Where(et => playerApplicationIds.Contains(et.PlayerApplicationId))
                .ToListAsync(ct);

            return _mapper.Map<IEnumerable<PlayerApplicationTeam>>(items);
        }

        public async Task<ViewModels.PlayerApplicationTeam> CreateAsync(ViewModels.PlayerApplicationTeam playerApplicationTeam, CancellationToken ct)
        {
            if (!(await _authorizationService.AuthorizeAsync(_user, null, new ContentDeveloperRequirement())).Succeeded)
                throw new ForbiddenException();

            var playerApplicationTeamEntity = _mapper.Map<PlayerApplicationTeamEntity>(playerApplicationTeam);
            playerApplicationTeamEntity.Id = playerApplicationTeamEntity.Id != Guid.Empty ? playerApplicationTeamEntity.Id : Guid.NewGuid();

            _context.PlayerApplicationTeams.Add(playerApplicationTeamEntity);
            await _context.SaveChangesAsync(ct);

            return await GetAsync(playerApplicationTeamEntity.Id, ct);
        }

        public async Task<ViewModels.PlayerApplicationTeam> UpdateAsync(Guid id, ViewModels.PlayerApplicationTeam playerApplicationTeam, CancellationToken ct)
        {
            if (!(await _authorizationService.AuthorizeAsync(_user, null, new ContentDeveloperRequirement())).Succeeded)
                throw new ForbiddenException();

            var playerApplicationTeamToUpdate = await _context.PlayerApplicationTeams.SingleOrDefaultAsync(v => v.Id == id, ct);

            if (playerApplicationTeamToUpdate == null)
                throw new EntityNotFoundException<PlayerApplicationTeam>();

            _mapper.Map(playerApplicationTeam, playerApplicationTeamToUpdate);

            _context.PlayerApplicationTeams.Update(playerApplicationTeamToUpdate);
            await _context.SaveChangesAsync(ct);

            playerApplicationTeam = await GetAsync(playerApplicationTeamToUpdate.Id, ct);

            return playerApplicationTeam;
        }

        public async Task<bool> DeleteAsync(Guid id, CancellationToken ct)
        {
            if (!(await _authorizationService.AuthorizeAsync(_user, null, new ContentDeveloperRequirement())).Succeeded)
                throw new ForbiddenException();

            var playerApplicationTeamToDelete = await _context.PlayerApplicationTeams.SingleOrDefaultAsync(v => v.Id == id, ct);

            if (playerApplicationTeamToDelete == null)
                throw new EntityNotFoundException<PlayerApplicationTeam>();

            _context.PlayerApplicationTeams.Remove(playerApplicationTeamToDelete);
            await _context.SaveChangesAsync(ct);

            return true;
        }

        public async Task<bool> DeleteByIdsAsync(Guid playerApplicationId, Guid teamId, CancellationToken ct)
        {
            if (!(await _authorizationService.AuthorizeAsync(_user, null, new ContentDeveloperRequirement())).Succeeded)
                throw new ForbiddenException();

            var playerApplicationTeamToDelete = await _context.PlayerApplicationTeams.SingleOrDefaultAsync(v => (v.TeamId == teamId) && (v.PlayerApplicationId == playerApplicationId), ct);

            if (playerApplicationTeamToDelete == null)
                throw new EntityNotFoundException<PlayerApplicationTeam>();

            _context.PlayerApplicationTeams.Remove(playerApplicationTeamToDelete);
            await _context.SaveChangesAsync(ct);

            return true;
        }

    }
}

