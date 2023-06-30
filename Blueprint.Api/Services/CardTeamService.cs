// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
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
    public interface ICardTeamService
    {
        Task<IEnumerable<ViewModels.CardTeam>> GetAsync(CancellationToken ct);
        Task<ViewModels.CardTeam> GetAsync(Guid id, CancellationToken ct);
        Task<IEnumerable<ViewModels.CardTeam>> GetByCardAsync(Guid cardId, CancellationToken ct);
        Task<IEnumerable<ViewModels.CardTeam>> GetByMselAsync(Guid mselId, CancellationToken ct);
        Task<ViewModels.CardTeam> CreateAsync(ViewModels.CardTeam cardTeam, CancellationToken ct);
        Task<ViewModels.CardTeam> UpdateAsync(Guid id, ViewModels.CardTeam cardTeam, CancellationToken ct);
        Task<bool> DeleteAsync(Guid id, CancellationToken ct);
        Task<bool> DeleteByIdsAsync(Guid cardId, Guid teamId, CancellationToken ct);
    }

    public class CardTeamService : ICardTeamService
    {
        private readonly BlueprintContext _context;
        private readonly IAuthorizationService _authorizationService;
        private readonly ClaimsPrincipal _user;
        private readonly IMapper _mapper;

        public CardTeamService(BlueprintContext context, IAuthorizationService authorizationService, IPrincipal team, IMapper mapper)
        {
            _context = context;
            _authorizationService = authorizationService;
            _user = team as ClaimsPrincipal;
            _mapper = mapper;
        }

        public async Task<IEnumerable<ViewModels.CardTeam>> GetAsync(CancellationToken ct)
        {
            if (!(await _authorizationService.AuthorizeAsync(_user, null, new ContentDeveloperRequirement())).Succeeded)
                throw new ForbiddenException();

            var items = await _context.CardTeams
                .ToListAsync(ct);

            return _mapper.Map<IEnumerable<CardTeam>>(items);
        }

        public async Task<ViewModels.CardTeam> GetAsync(Guid id, CancellationToken ct)
        {
            var item = await _context.CardTeams
                .Include(ct => ct.Card)
                .SingleOrDefaultAsync(o => o.Id == id, ct);
            if (
                    !(await MselViewRequirement.IsMet(_user.GetId(), item.Card.MselId, _context)) &&
                    !(await _authorizationService.AuthorizeAsync(_user, null, new ContentDeveloperRequirement())).Succeeded
               )
            {
                var mselCheck = await _context.Msels.FindAsync(item.Card.MselId);
                if (!mselCheck.IsTemplate)
                    throw new ForbiddenException();
            }

            return _mapper.Map<CardTeam>(item);
        }

        public async Task<IEnumerable<ViewModels.CardTeam>> GetByCardAsync(Guid cardId, CancellationToken ct)
        {
            var card = await _context.Cards.FirstOrDefaultAsync(c => c.Id == cardId, ct);
            if (
                    !(await MselViewRequirement.IsMet(_user.GetId(), card.MselId, _context)) &&
                    !(await _authorizationService.AuthorizeAsync(_user, null, new ContentDeveloperRequirement())).Succeeded
               )
            {
                var mselCheck = await _context.Msels.FindAsync(card.MselId);
                if (!mselCheck.IsTemplate)
                    throw new ForbiddenException();
            }
            var items = await _context.CardTeams
                .Where(et => et.CardId == cardId)
                .ToListAsync(ct);

            return _mapper.Map<IEnumerable<CardTeam>>(items);
        }

        public async Task<IEnumerable<ViewModels.CardTeam>> GetByMselAsync(Guid mselId, CancellationToken ct)
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
            var cardIds = await _context.Cards
                .Where(c => c.MselId == mselId)
                .Select(c => c.Id)
                .ToListAsync(ct);
            var items = await _context.CardTeams
                .Where(et => cardIds.Contains(et.CardId))
                .ToListAsync(ct);

            return _mapper.Map<IEnumerable<CardTeam>>(items);
        }

        public async Task<ViewModels.CardTeam> CreateAsync(ViewModels.CardTeam cardTeam, CancellationToken ct)
        {
            if (!(await _authorizationService.AuthorizeAsync(_user, null, new ContentDeveloperRequirement())).Succeeded)
                throw new ForbiddenException();

            var cardTeamEntity = _mapper.Map<CardTeamEntity>(cardTeam);
            cardTeamEntity.Id = cardTeamEntity.Id != Guid.Empty ? cardTeamEntity.Id : Guid.NewGuid();

            _context.CardTeams.Add(cardTeamEntity);
            await _context.SaveChangesAsync(ct);

            return await GetAsync(cardTeamEntity.Id, ct);
        }

        public async Task<ViewModels.CardTeam> UpdateAsync(Guid id, ViewModels.CardTeam cardTeam, CancellationToken ct)
        {
            if (!(await _authorizationService.AuthorizeAsync(_user, null, new ContentDeveloperRequirement())).Succeeded)
                throw new ForbiddenException();

            var cardTeamToUpdate = await _context.CardTeams.SingleOrDefaultAsync(v => v.Id == id, ct);

            if (cardTeamToUpdate == null)
                throw new EntityNotFoundException<CardTeam>();

            _mapper.Map(cardTeam, cardTeamToUpdate);

            _context.CardTeams.Update(cardTeamToUpdate);
            await _context.SaveChangesAsync(ct);

            cardTeam = await GetAsync(cardTeamToUpdate.Id, ct);

            return cardTeam;
        }

        public async Task<bool> DeleteAsync(Guid id, CancellationToken ct)
        {
            if (!(await _authorizationService.AuthorizeAsync(_user, null, new ContentDeveloperRequirement())).Succeeded)
                throw new ForbiddenException();

            var cardTeamToDelete = await _context.CardTeams.SingleOrDefaultAsync(v => v.Id == id, ct);

            if (cardTeamToDelete == null)
                throw new EntityNotFoundException<CardTeam>();

            _context.CardTeams.Remove(cardTeamToDelete);
            await _context.SaveChangesAsync(ct);

            return true;
        }

        public async Task<bool> DeleteByIdsAsync(Guid cardId, Guid teamId, CancellationToken ct)
        {
            if (!(await _authorizationService.AuthorizeAsync(_user, null, new ContentDeveloperRequirement())).Succeeded)
                throw new ForbiddenException();

            var cardTeamToDelete = await _context.CardTeams.SingleOrDefaultAsync(v => (v.TeamId == teamId) && (v.CardId == cardId), ct);

            if (cardTeamToDelete == null)
                throw new EntityNotFoundException<CardTeam>();

            _context.CardTeams.Remove(cardTeamToDelete);
            await _context.SaveChangesAsync(ct);

            return true;
        }

    }
}

