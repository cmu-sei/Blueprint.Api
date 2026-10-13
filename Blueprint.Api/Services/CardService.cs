// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using AutoMapper;
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
        Task<IEnumerable<ViewModels.Card>> GetTemplatesAsync(CancellationToken ct);
        Task<IEnumerable<ViewModels.Card>> GetByMselAsync(Guid mselId, bool hasSystemPermission, CancellationToken ct);
        Task<ViewModels.Card> GetAsync(Guid id, bool hasSystemPermission, CancellationToken ct);
        Task<ViewModels.Card> CreateAsync(ViewModels.CardCreate card, bool hasMselPermission, bool hasGalleryCardPermission, CancellationToken ct);
        Task<ViewModels.Card> UpdateAsync(Guid id, ViewModels.CardEdit card, bool hasMselPermission, bool hasGalleryCardPermission, CancellationToken ct);
        Task<bool> DeleteAsync(Guid id, bool hasMselPermission, bool hasGalleryCardPermission, CancellationToken ct);
        Task<IEnumerable<ViewModels.Card>> UploadJsonAsync(FileForm form, CancellationToken ct);
        Task<Tuple<MemoryStream, string>> DownloadJsonAsync(IEnumerable<Guid> ids, CancellationToken ct);
    }

    public class CardService : ICardService
    {
        private readonly BlueprintContext _context;
        private readonly ClaimsPrincipal _user;
        private readonly IMapper _mapper;

        public CardService(
            BlueprintContext context,
            IPrincipal user,
            IMapper mapper)
        {
            _context = context;
            _user = user as ClaimsPrincipal;
            _mapper = mapper;
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

        public async Task<ViewModels.Card> CreateAsync(ViewModels.CardCreate card, bool hasMselPermission, bool hasGalleryCardPermission, CancellationToken ct)
        {
            if (card.MselId.HasValue)
            {
                if (!hasMselPermission &&
                    !await MselOwnerRequirement.IsMet(_user.GetId(), card.MselId, _context) &&
                    !await MselEditorRequirement.IsMet(_user.GetId(), card.MselId, _context))
                    throw new ForbiddenException();
            }
            else
            {
                if (!hasGalleryCardPermission)
                    throw new ForbiddenException();
            }
            var cardEntity = _mapper.Map<CardEntity>(card);
            cardEntity.Id = card.Id != Guid.Empty ? card.Id : Guid.NewGuid();
            cardEntity.CreatedBy = _user.GetId();

            _context.Cards.Add(cardEntity);
            await _context.SaveChangesAsync(ct);
            var created = await GetAsync(cardEntity.Id, true, ct);

            return created;
        }

        public async Task<ViewModels.Card> UpdateAsync(Guid id, ViewModels.CardEdit card, bool hasMselPermission, bool hasGalleryCardPermission, CancellationToken ct)
        {
            var cardToUpdate = await _context.Cards.SingleOrDefaultAsync(v => v.Id == id, ct);

            if (cardToUpdate == null)
                throw new EntityNotFoundException<Card>();

            // decided on the stored row: a MSEL's card needs the MSEL permission or an owner or editor
            // of that MSEL, and a template needs the template permission
            if (cardToUpdate.MselId.HasValue)
            {
                if (!hasMselPermission &&
                    !await MselOwnerRequirement.IsMet(_user.GetId(), cardToUpdate.MselId, _context) &&
                    !await MselEditorRequirement.IsMet(_user.GetId(), cardToUpdate.MselId, _context))
                    throw new ForbiddenException();
            }
            else
            {
                if (!hasGalleryCardPermission)
                    throw new ForbiddenException();
            }

            _mapper.Map(card, cardToUpdate);
            cardToUpdate.ModifiedBy = _user.GetId();

            _context.Cards.Update(cardToUpdate);
            await _context.SaveChangesAsync(ct);

            var updated = await GetAsync(cardToUpdate.Id, true, ct);

            return updated;
        }

        public async Task<bool> DeleteAsync(Guid id, bool hasMselPermission, bool hasGalleryCardPermission, CancellationToken ct)
        {
            var cardToDelete = await _context.Cards.SingleOrDefaultAsync(v => v.Id == id, ct);

            if (cardToDelete == null)
                throw new EntityNotFoundException<Card>();

            if (cardToDelete.MselId.HasValue)
            {
                if (!hasMselPermission &&
                    !await MselOwnerRequirement.IsMet(_user.GetId(), cardToDelete.MselId, _context) &&
                    !await MselEditorRequirement.IsMet(_user.GetId(), cardToDelete.MselId, _context))
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

        public async Task<Tuple<MemoryStream, string>> DownloadJsonAsync(IEnumerable<Guid> ids, CancellationToken ct)
        {
            var idList = ids?.ToList() ?? new List<Guid>();
            // only templates are exported: a MSEL's own rows are read through the MSEL
            var cardEntities = await _context.Cards
                .Where(c => idList.Contains(c.Id) && c.MselId == null)
                .ToListAsync(ct);
            var cards = _mapper.Map<IEnumerable<ViewModels.Card>>(cardEntities).ToList();

            var json = JsonSerializer.Serialize(cards, new JsonSerializerOptions
            {
                ReferenceHandler = ReferenceHandler.Preserve,
                WriteIndented = true,
            });
            var memoryStream = new MemoryStream(Encoding.UTF8.GetBytes(json));

            return Tuple.Create(memoryStream, "card-templates.json");
        }

        public async Task<IEnumerable<ViewModels.Card>> UploadJsonAsync(FileForm form, CancellationToken ct)
        {
            var uploadItem = form.ToUpload;
            string json;
            using (var reader = new StreamReader(uploadItem.OpenReadStream()))
            {
                json = await reader.ReadToEndAsync();
            }
            var options = new JsonSerializerOptions
            {
                ReferenceHandler = ReferenceHandler.Preserve,
                PropertyNameCaseInsensitive = true,
            };
            var incoming = JsonSerializer.Deserialize<List<ViewModels.Card>>(json, options) ?? new List<ViewModels.Card>();

            var created = new List<ViewModels.Card>();
            var userId = _user.GetId();
            foreach (var item in incoming)
            {
                item.Id = Guid.NewGuid();
                item.IsTemplate = true;
                item.MselId = null;
                item.CreatedBy = userId;
                item.DateCreated = DateTime.UtcNow;
                item.ModifiedBy = null;
                item.DateModified = null;
                var entity = _mapper.Map<CardEntity>(item);
                _context.Cards.Add(entity);
                created.Add(_mapper.Map<ViewModels.Card>(entity));
            }
            await _context.SaveChangesAsync(ct);

            return created;
        }

    }
}

