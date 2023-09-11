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
using Blueprint.Api.Data;
using Blueprint.Api.Data.Models;
using Blueprint.Api.Infrastructure.Authorization;
using Blueprint.Api.Infrastructure.Exceptions;
using Blueprint.Api.Infrastructure.Extensions;
using Blueprint.Api.ViewModels;

namespace Blueprint.Api.Services
{
    public interface ICardService
    {
        Task<IEnumerable<ViewModels.Card>> GetByMselAsync(Guid mselId, CancellationToken ct);
        Task<ViewModels.Card> GetAsync(Guid id, CancellationToken ct);
        Task<ViewModels.Card> CreateAsync(ViewModels.Card card, CancellationToken ct);
        Task<ViewModels.Card> UpdateAsync(Guid id, ViewModels.Card card, CancellationToken ct);
        Task<bool> DeleteAsync(Guid id, CancellationToken ct);
    }

    public class CardService : ICardService
    {
        private readonly BlueprintContext _context;
        private readonly IAuthorizationService _authorizationService;
        private readonly ClaimsPrincipal _user;
        private readonly IMapper _mapper;

        public CardService(
            BlueprintContext context,
            IAuthorizationService authorizationService,
            IPrincipal user,
            IMapper mapper)
        {
            _context = context;
            _authorizationService = authorizationService;
            _user = user as ClaimsPrincipal;
            _mapper = mapper;
        }

        public async Task<IEnumerable<ViewModels.Card>> GetByMselAsync(Guid mselId, CancellationToken ct)
        {
            if (!(await _authorizationService.AuthorizeAsync(_user, null, new ContentDeveloperRequirement())).Succeeded &&
                !await MselUserRequirement.IsMet(_user.GetId(), mselId, _context))
                throw new ForbiddenException();

            var cardEntities = await _context.Cards
                .Where(card => card.MselId == mselId)
                .ToListAsync(ct);

            return _mapper.Map<IEnumerable<Card>>(cardEntities).ToList();;
        }

        public async Task<ViewModels.Card> GetAsync(Guid id, CancellationToken ct)
        {
            var item = await _context.Cards.SingleAsync(card => card.Id == id, ct);

            if (item == null)
                throw new EntityNotFoundException<DataValueEntity>("DataValue not found: " + id);

            if (!(await _authorizationService.AuthorizeAsync(_user, null, new ContentDeveloperRequirement())).Succeeded &&
                !await MselUserRequirement.IsMet(_user.GetId(), item.MselId, _context))
                throw new ForbiddenException();

            return _mapper.Map<Card>(item);
        }

        public async Task<ViewModels.Card> CreateAsync(ViewModels.Card card, CancellationToken ct)
        {
            if (!(await _authorizationService.AuthorizeAsync(_user, null, new ContentDeveloperRequirement())).Succeeded &&
                !await MselOwnerRequirement.IsMet(_user.GetId(), card.MselId, _context))
                throw new ForbiddenException();
            card.Id = card.Id != Guid.Empty ? card.Id : Guid.NewGuid();
            card.DateCreated = DateTime.UtcNow;
            card.CreatedBy = _user.GetId();
            card.DateModified = null;
            card.ModifiedBy = null;
            var cardEntity = _mapper.Map<CardEntity>(card);

            _context.Cards.Add(cardEntity);
            await _context.SaveChangesAsync(ct);
            card = await GetAsync(cardEntity.Id, ct);

            return card;
        }

        public async Task<ViewModels.Card> UpdateAsync(Guid id, ViewModels.Card card, CancellationToken ct)
        {
            if (!(await _authorizationService.AuthorizeAsync(_user, null, new ContentDeveloperRequirement())).Succeeded &&
                !await MselOwnerRequirement.IsMet(_user.GetId(), card.MselId, _context))
                throw new ForbiddenException();

            var cardToUpdate = await _context.Cards.SingleOrDefaultAsync(v => v.Id == id, ct);

            if (cardToUpdate == null)
                throw new EntityNotFoundException<Card>();

            card.CreatedBy = cardToUpdate.CreatedBy;
            card.DateCreated = cardToUpdate.DateCreated;
            card.ModifiedBy = _user.GetId();
            card.DateModified = DateTime.UtcNow;
            _mapper.Map(card, cardToUpdate);

            _context.Cards.Update(cardToUpdate);
            await _context.SaveChangesAsync(ct);

            card = await GetAsync(cardToUpdate.Id, ct);

            return card;
        }

        public async Task<bool> DeleteAsync(Guid id, CancellationToken ct)
        {
            var cardToDelete = await _context.Cards.SingleOrDefaultAsync(v => v.Id == id, ct);

            if (!(await _authorizationService.AuthorizeAsync(_user, null, new ContentDeveloperRequirement())).Succeeded &&
                !await MselOwnerRequirement.IsMet(_user.GetId(), cardToDelete.MselId, _context))
                throw new ForbiddenException();

            if (cardToDelete == null)
                throw new EntityNotFoundException<Card>();

            _context.Cards.Remove(cardToDelete);
            await _context.SaveChangesAsync(ct);

            return true;
        }

    }
}

