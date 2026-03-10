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
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
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
        Task<IEnumerable<ViewModels.Card>> GetTemplatesAsync(CancellationToken ct);
        Task<IEnumerable<ViewModels.Card>> GetByMselAsync(Guid mselId, bool hasSystemPermission, CancellationToken ct);
        Task<ViewModels.Card> GetAsync(Guid id, bool hasSystemPermission, CancellationToken ct);
        Task<ViewModels.Card> CreateAsync(ViewModels.Card card, bool hasMselPermission, bool hasGalleryCardPermission, CancellationToken ct);
        Task<ViewModels.Card> UpdateAsync(Guid id, ViewModels.Card card, bool hasMselPermission, bool hasGalleryCardPermission, CancellationToken ct);
        Task<bool> DeleteAsync(Guid id, bool hasMselPermission, bool hasGalleryCardPermission, CancellationToken ct);
    }

    public class CardService : ICardService
    {
        private readonly BlueprintContext _context;
        private readonly ClaimsPrincipal _user;
        private readonly IMapper _mapper;
        private readonly ILogger<CardService> _logger;

        public CardService(
            BlueprintContext context,
            IPrincipal user,
            IMapper mapper,
            ILogger<CardService> logger)
        {
            _context = context;
            _user = user as ClaimsPrincipal;
            _mapper = mapper;
            _logger = logger;
        }

        public async Task<IEnumerable<ViewModels.Card>> GetTemplatesAsync(CancellationToken ct)
        {
            var cardEntities = await _context.Cards
                .Where(card => card.IsTemplate)
                .ToListAsync(ct);

            return _mapper.Map<IEnumerable<Card>>(cardEntities).ToList();;
        }

        public async Task<IEnumerable<ViewModels.Card>> GetByMselAsync(Guid mselId, bool hasSystemPermission, CancellationToken ct)
        {
            if (!hasSystemPermission && !(await MselViewRequirement.IsMet(_user.GetId(), mselId, _context)))
            {
                var msel = await _context.Msels.FindAsync(mselId);
                if (!msel.IsTemplate)
                    throw new ForbiddenException();
            }

            var cardEntities = await _context.Cards
                .Where(card => card.MselId == mselId)
                .ToListAsync(ct);

            return _mapper.Map<IEnumerable<Card>>(cardEntities).ToList();;
        }

        public async Task<ViewModels.Card> GetAsync(Guid id, bool hasSystemPermission, CancellationToken ct)
        {
            var item = await _context.Cards.SingleAsync(card => card.Id == id, ct);

            if (item == null)
                throw new EntityNotFoundException<DataValueEntity>("DataValue not found: " + id);

            if (!hasSystemPermission && !await MselUserRequirement.IsMet(_user.GetId(), item.MselId, _context))
                throw new ForbiddenException();

            return _mapper.Map<Card>(item);
        }

        public async Task<ViewModels.Card> CreateAsync(ViewModels.Card card, bool hasMselPermission, bool hasGalleryCardPermission, CancellationToken ct)
        {
            if (card.MselId.HasValue)
            {
                if (!hasMselPermission && !await MselOwnerRequirement.IsMet(_user.GetId(), card.MselId, _context))
                    throw new ForbiddenException();
            }
            else
            {
                if (!hasGalleryCardPermission)
                    throw new ForbiddenException();
            }

            // Validate required fields
            if (string.IsNullOrWhiteSpace(card.Name))
                throw new ArgumentException("Card Name is required and cannot be empty.");

            // Validate MselId if provided
            if (card.MselId.HasValue && card.MselId.Value != Guid.Empty)
            {
                var mselExists = await _context.Msels.AnyAsync(m => m.Id == card.MselId.Value, ct);
                if (!mselExists)
                    throw new EntityNotFoundException<Msel>($"Invalid MselId '{card.MselId}'. The MSEL does not exist.");
            }

            card.Id = card.Id != Guid.Empty ? card.Id : Guid.NewGuid();
            card.CreatedBy = _user.GetId();
            var cardEntity = _mapper.Map<CardEntity>(card);

            _context.Cards.Add(cardEntity);
            await _context.SaveChangesAsync(ct);

            card = await GetAsync(cardEntity.Id, true, ct);
            return card;
        }

        public async Task<ViewModels.Card> UpdateAsync(Guid id, ViewModels.Card card, bool hasMselPermission, bool hasGalleryCardPermission, CancellationToken ct)
        {
            if (card.MselId.HasValue)
            {
                if (!hasMselPermission && !await MselOwnerRequirement.IsMet(_user.GetId(), card.MselId, _context))
                    throw new ForbiddenException();
            }
            else
            {
                if (!hasGalleryCardPermission)
                    throw new ForbiddenException();
            }

            var cardToUpdate = await _context.Cards.SingleOrDefaultAsync(v => v.Id == id, ct);

            if (cardToUpdate == null)
                throw new EntityNotFoundException<Card>();

            card.ModifiedBy = _user.GetId();
            _mapper.Map(card, cardToUpdate);

            _context.Cards.Update(cardToUpdate);
            await _context.SaveChangesAsync(ct);

            card = await GetAsync(cardToUpdate.Id, true, ct);

            return card;
        }

        public async Task<bool> DeleteAsync(Guid id, bool hasMselPermission, bool hasGalleryCardPermission, CancellationToken ct)
        {
            var cardToDelete = await _context.Cards.SingleOrDefaultAsync(v => v.Id == id, ct);

            if (cardToDelete == null)
                throw new EntityNotFoundException<Card>();

            if (cardToDelete.MselId.HasValue)
            {
                if (!hasMselPermission && !await MselOwnerRequirement.IsMet(_user.GetId(), cardToDelete.MselId, _context))
                    throw new ForbiddenException();
            }
            else
            {
                if (!hasGalleryCardPermission)
                    throw new ForbiddenException();
            }

            _context.Cards.Remove(cardToDelete);
            await _context.SaveChangesAsync(ct);

            return true;
        }

    }
}

