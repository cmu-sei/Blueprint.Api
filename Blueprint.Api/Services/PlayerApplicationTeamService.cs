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
        Task<IEnumerable<ViewModels.PlayerApplicationTeam>> GetByPlayerApplicationAsync(Guid cardId, CancellationToken ct);
        Task<IEnumerable<ViewModels.PlayerApplicationTeam>> GetByMselAsync(Guid mselId, CancellationToken ct);
        Task<ViewModels.PlayerApplicationTeam> CreateAsync(ViewModels.PlayerApplicationTeam cardTeam, CancellationToken ct);
        Task<ViewModels.PlayerApplicationTeam> UpdateAsync(Guid id, ViewModels.PlayerApplicationTeam cardTeam, CancellationToken ct);
        Task<bool> DeleteAsync(Guid id, CancellationToken ct);
        Task<bool> DeleteByIdsAsync(Guid cardId, Guid teamId, CancellationToken ct);
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

        public async Task<IEnumerable<ViewModels.PlayerApplicationTeam>> GetByPlayerApplicationAsync(Guid cardId, CancellationToken ct)
        {
            var card = await _context.PlayerApplications.FirstOrDefaultAsync(c => c.Id == cardId, ct);
            if (
                    !(await MselViewRequirement.IsMet(_user.GetId(), card.MselId, _context)) &&
                    !(await _authorizationService.AuthorizeAsync(_user, null, new ContentDeveloperRequirement())).Succeeded
               )
            {
                var mselCheck = await _context.Msels.FindAsync(card.MselId);
                if (!mselCheck.IsTemplate)
                    throw new ForbiddenException();
            }
            var items = await _context.PlayerApplicationTeams
                .Where(et => et.PlayerApplicationId == cardId)
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
            var cardIds = await _context.PlayerApplications
                .Where(c => c.MselId == mselId)
                .Select(c => c.Id)
                .ToListAsync(ct);
            var items = await _context.PlayerApplicationTeams
                .Where(et => cardIds.Contains(et.PlayerApplicationId))
                .ToListAsync(ct);

            return _mapper.Map<IEnumerable<PlayerApplicationTeam>>(items);
        }

        public async Task<ViewModels.PlayerApplicationTeam> CreateAsync(ViewModels.PlayerApplicationTeam cardTeam, CancellationToken ct)
        {
            if (!(await _authorizationService.AuthorizeAsync(_user, null, new ContentDeveloperRequirement())).Succeeded)
                throw new ForbiddenException();

            var cardTeamEntity = _mapper.Map<PlayerApplicationTeamEntity>(cardTeam);
            cardTeamEntity.Id = cardTeamEntity.Id != Guid.Empty ? cardTeamEntity.Id : Guid.NewGuid();

            _context.PlayerApplicationTeams.Add(cardTeamEntity);
            await _context.SaveChangesAsync(ct);

            return await GetAsync(cardTeamEntity.Id, ct);
        }

        public async Task<ViewModels.PlayerApplicationTeam> UpdateAsync(Guid id, ViewModels.PlayerApplicationTeam cardTeam, CancellationToken ct)
        {
            if (!(await _authorizationService.AuthorizeAsync(_user, null, new ContentDeveloperRequirement())).Succeeded)
                throw new ForbiddenException();

            var cardTeamToUpdate = await _context.PlayerApplicationTeams.SingleOrDefaultAsync(v => v.Id == id, ct);

            if (cardTeamToUpdate == null)
                throw new EntityNotFoundException<PlayerApplicationTeam>();

            _mapper.Map(cardTeam, cardTeamToUpdate);

            _context.PlayerApplicationTeams.Update(cardTeamToUpdate);
            await _context.SaveChangesAsync(ct);

            cardTeam = await GetAsync(cardTeamToUpdate.Id, ct);

            return cardTeam;
        }

        public async Task<bool> DeleteAsync(Guid id, CancellationToken ct)
        {
            if (!(await _authorizationService.AuthorizeAsync(_user, null, new ContentDeveloperRequirement())).Succeeded)
                throw new ForbiddenException();

            var cardTeamToDelete = await _context.PlayerApplicationTeams.SingleOrDefaultAsync(v => v.Id == id, ct);

            if (cardTeamToDelete == null)
                throw new EntityNotFoundException<PlayerApplicationTeam>();

            _context.PlayerApplicationTeams.Remove(cardTeamToDelete);
            await _context.SaveChangesAsync(ct);

            return true;
        }

        public async Task<bool> DeleteByIdsAsync(Guid cardId, Guid teamId, CancellationToken ct)
        {
            if (!(await _authorizationService.AuthorizeAsync(_user, null, new ContentDeveloperRequirement())).Succeeded)
                throw new ForbiddenException();

            var cardTeamToDelete = await _context.PlayerApplicationTeams.SingleOrDefaultAsync(v => (v.TeamId == teamId) && (v.PlayerApplicationId == cardId), ct);

            if (cardTeamToDelete == null)
                throw new EntityNotFoundException<PlayerApplicationTeam>();

            _context.PlayerApplicationTeams.Remove(cardTeamToDelete);
            await _context.SaveChangesAsync(ct);

            return true;
        }

    }
}

