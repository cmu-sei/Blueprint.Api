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
        Task<ViewModels.PlayerApplication> CreateAsync(ViewModels.PlayerApplication playerApplication, CancellationToken ct);
        Task<ViewModels.PlayerApplication> CreateAndPushAsync(ViewModels.PlayerApplication playerApplication, CancellationToken ct);
        Task<ViewModels.PlayerApplication> UpdateAsync(Guid id, ViewModels.PlayerApplication playerApplication, CancellationToken ct);
        Task<bool> DeleteAsync(Guid id, CancellationToken ct);
    }

    public class PlayerApplicationService : IPlayerApplicationService
    {
        private readonly BlueprintContext _context;
        private readonly IAuthorizationService _authorizationService;
        private readonly IPlayerService _playerService;
        private readonly ClaimsPrincipal _user;
        private readonly IMapper _mapper;

        public PlayerApplicationService(
            BlueprintContext context,
            IAuthorizationService authorizationService,
            IPlayerService playerService,
            IPrincipal user,
            IMapper mapper)
        {
            _context = context;
            _authorizationService = authorizationService;
            _playerService = playerService;
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

            var playerApplicationEntities = await _context.PlayerApplications
                .Where(playerApplication => playerApplication.MselId == mselId)
                .ToListAsync(ct);

            return _mapper.Map<IEnumerable<PlayerApplication>>(playerApplicationEntities).ToList();;
        }

        public async Task<ViewModels.PlayerApplication> GetAsync(Guid id, CancellationToken ct)
        {
            var item = await _context.PlayerApplications.SingleAsync(playerApplication => playerApplication.Id == id, ct);

            if (item == null)
                throw new EntityNotFoundException<DataValueEntity>("DataValue not found: " + id);

            if (!(await _authorizationService.AuthorizeAsync(_user, null, new ContentDeveloperRequirement())).Succeeded &&
                !await MselUserRequirement.IsMet(_user.GetId(), item.MselId, _context))
                throw new ForbiddenException();

            return _mapper.Map<PlayerApplication>(item);
        }

        public async Task<ViewModels.PlayerApplication> CreateAsync(ViewModels.PlayerApplication playerApplication, CancellationToken ct)
        {
            if (!(await _authorizationService.AuthorizeAsync(_user, null, new ContentDeveloperRequirement())).Succeeded &&
                !await MselOwnerRequirement.IsMet(_user.GetId(), playerApplication.MselId, _context))
                throw new ForbiddenException();
            playerApplication.Id = playerApplication.Id != Guid.Empty ? playerApplication.Id : Guid.NewGuid();
            playerApplication.DateCreated = DateTime.UtcNow;
            playerApplication.CreatedBy = _user.GetId();
            playerApplication.DateModified = null;
            playerApplication.ModifiedBy = null;
            var playerApplicationEntity = _mapper.Map<PlayerApplicationEntity>(playerApplication);

            _context.PlayerApplications.Add(playerApplicationEntity);
            await _context.SaveChangesAsync(ct);
            playerApplication = await GetAsync(playerApplicationEntity.Id, ct);

            return playerApplication;
        }

        public async Task<ViewModels.PlayerApplication> CreateAndPushAsync(ViewModels.PlayerApplication playerApplication, CancellationToken ct)
        {
            // authorization will be checked in CreateAsync
            var item = await CreateAsync(playerApplication, ct);
            // push the application to Player
            await _playerService.PushApplication(item, ct);

            return item;
        }

        public async Task<ViewModels.PlayerApplication> UpdateAsync(Guid id, ViewModels.PlayerApplication playerApplication, CancellationToken ct)
        {
            if (!(await _authorizationService.AuthorizeAsync(_user, null, new ContentDeveloperRequirement())).Succeeded &&
                !await MselOwnerRequirement.IsMet(_user.GetId(), playerApplication.MselId, _context))
                throw new ForbiddenException();

            var playerApplicationToUpdate = await _context.PlayerApplications.SingleOrDefaultAsync(v => v.Id == id, ct);

            if (playerApplicationToUpdate == null)
                throw new EntityNotFoundException<PlayerApplication>();

            playerApplication.CreatedBy = playerApplicationToUpdate.CreatedBy;
            playerApplication.DateCreated = playerApplicationToUpdate.DateCreated;
            playerApplication.ModifiedBy = _user.GetId();
            playerApplication.DateModified = DateTime.UtcNow;
            playerApplication.Msel = null;
            _mapper.Map(playerApplication, playerApplicationToUpdate);

            _context.PlayerApplications.Update(playerApplicationToUpdate);
            await _context.SaveChangesAsync(ct);

            playerApplication = await GetAsync(playerApplicationToUpdate.Id, ct);

            return playerApplication;
        }

        public async Task<bool> DeleteAsync(Guid id, CancellationToken ct)
        {
            var playerApplicationToDelete = await _context.PlayerApplications.SingleOrDefaultAsync(v => v.Id == id, ct);

            if (!(await _authorizationService.AuthorizeAsync(_user, null, new ContentDeveloperRequirement())).Succeeded &&
                !await MselOwnerRequirement.IsMet(_user.GetId(), playerApplicationToDelete.MselId, _context))
                throw new ForbiddenException();

            if (playerApplicationToDelete == null)
                throw new EntityNotFoundException<PlayerApplication>();

            _context.PlayerApplications.Remove(playerApplicationToDelete);
            await _context.SaveChangesAsync(ct);

            return true;
        }

    }
}
