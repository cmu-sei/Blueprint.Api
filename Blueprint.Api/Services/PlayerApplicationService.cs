// Copyright 2024 Carnegie Mellon University. All Rights Reserved.
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
    public interface IPlayerApplicationService
    {
        Task<IEnumerable<ViewModels.PlayerApplication>> GetByMselAsync(Guid mselId, CancellationToken ct);
        Task<ViewModels.PlayerApplication> GetAsync(Guid id, CancellationToken ct);
        Task<ViewModels.PlayerApplication> CreateAsync(ViewModels.PlayerApplication card, CancellationToken ct);
        Task<ViewModels.PlayerApplication> UpdateAsync(Guid id, ViewModels.PlayerApplication card, CancellationToken ct);
        Task<bool> DeleteAsync(Guid id, CancellationToken ct);
    }

    public class PlayerApplicationService : IPlayerApplicationService
    {
        private readonly BlueprintContext _context;
        private readonly IAuthorizationService _authorizationService;
        private readonly ClaimsPrincipal _user;
        private readonly IMapper _mapper;

        public PlayerApplicationService(
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

        public async Task<IEnumerable<ViewModels.PlayerApplication>> GetByMselAsync(Guid mselId, CancellationToken ct)
        {
            if (
                    !(await MselViewRequirement.IsMet(_user.GetId(), mselId, _context)) &&
                    !(await _authorizationService.AuthorizeAsync(_user, null, new ContentDeveloperRequirement())).Succeeded
               )
            {
                var msel = await _context.Msels.FindAsync(mselId);
                if (!msel.IsTemplate)
                    throw new ForbiddenException();
            }

            var cardEntities = await _context.PlayerApplications
                .Where(card => card.MselId == mselId)
                .ToListAsync(ct);

            return _mapper.Map<IEnumerable<PlayerApplication>>(cardEntities).ToList();;
        }

        public async Task<ViewModels.PlayerApplication> GetAsync(Guid id, CancellationToken ct)
        {
            var item = await _context.PlayerApplications.SingleAsync(card => card.Id == id, ct);

            if (item == null)
                throw new EntityNotFoundException<DataValueEntity>("DataValue not found: " + id);

            if (!(await _authorizationService.AuthorizeAsync(_user, null, new ContentDeveloperRequirement())).Succeeded &&
                !await MselUserRequirement.IsMet(_user.GetId(), item.MselId, _context))
                throw new ForbiddenException();

            return _mapper.Map<PlayerApplication>(item);
        }

        public async Task<ViewModels.PlayerApplication> CreateAsync(ViewModels.PlayerApplication card, CancellationToken ct)
        {
            if (!(await _authorizationService.AuthorizeAsync(_user, null, new ContentDeveloperRequirement())).Succeeded &&
                !await MselOwnerRequirement.IsMet(_user.GetId(), card.MselId, _context))
                throw new ForbiddenException();
            card.Id = card.Id != Guid.Empty ? card.Id : Guid.NewGuid();
            card.DateCreated = DateTime.UtcNow;
            card.CreatedBy = _user.GetId();
            card.DateModified = null;
            card.ModifiedBy = null;
            var cardEntity = _mapper.Map<PlayerApplicationEntity>(card);

            _context.PlayerApplications.Add(cardEntity);
            await _context.SaveChangesAsync(ct);
            card = await GetAsync(cardEntity.Id, ct);

            return card;
        }

        public async Task<ViewModels.PlayerApplication> UpdateAsync(Guid id, ViewModels.PlayerApplication card, CancellationToken ct)
        {
            if (!(await _authorizationService.AuthorizeAsync(_user, null, new ContentDeveloperRequirement())).Succeeded &&
                !await MselOwnerRequirement.IsMet(_user.GetId(), card.MselId, _context))
                throw new ForbiddenException();

            var cardToUpdate = await _context.PlayerApplications.SingleOrDefaultAsync(v => v.Id == id, ct);

            if (cardToUpdate == null)
                throw new EntityNotFoundException<PlayerApplication>();

            card.CreatedBy = cardToUpdate.CreatedBy;
            card.DateCreated = cardToUpdate.DateCreated;
            card.ModifiedBy = _user.GetId();
            card.DateModified = DateTime.UtcNow;
            _mapper.Map(card, cardToUpdate);

            _context.PlayerApplications.Update(cardToUpdate);
            await _context.SaveChangesAsync(ct);

            card = await GetAsync(cardToUpdate.Id, ct);

            return card;
        }

        public async Task<bool> DeleteAsync(Guid id, CancellationToken ct)
        {
            var cardToDelete = await _context.PlayerApplications.SingleOrDefaultAsync(v => v.Id == id, ct);

            if (!(await _authorizationService.AuthorizeAsync(_user, null, new ContentDeveloperRequirement())).Succeeded &&
                !await MselOwnerRequirement.IsMet(_user.GetId(), cardToDelete.MselId, _context))
                throw new ForbiddenException();

            if (cardToDelete == null)
                throw new EntityNotFoundException<PlayerApplication>();

            _context.PlayerApplications.Remove(cardToDelete);
            await _context.SaveChangesAsync(ct);

            return true;
        }

    }
}

