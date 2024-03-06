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
    public interface IInvitationService
    {
        Task<IEnumerable<ViewModels.Invitation>> GetByMselAsync(Guid mselId, CancellationToken ct);
        Task<ViewModels.Invitation> GetAsync(Guid id, CancellationToken ct);
        Task<ViewModels.Invitation> CreateAsync(ViewModels.Invitation invitation, CancellationToken ct);
        Task<ViewModels.Invitation> UpdateAsync(Guid id, ViewModels.Invitation invitation, CancellationToken ct);
        Task<bool> DeleteAsync(Guid id, CancellationToken ct);
    }

    public class InvitationService : IInvitationService
    {
        private readonly BlueprintContext _context;
        private readonly IAuthorizationService _authorizationService;
        private readonly ClaimsPrincipal _user;
        private readonly IMapper _mapper;

        public InvitationService(BlueprintContext context, IAuthorizationService authorizationService, IPrincipal user, IMapper mapper)
        {
            _context = context;
            _authorizationService = authorizationService;
            _user = user as ClaimsPrincipal;
            _mapper = mapper;
        }

        public async Task<IEnumerable<ViewModels.Invitation>> GetByMselAsync(Guid mselId, CancellationToken ct)
        {
            var msel = await _context.Msels.SingleOrDefaultAsync(v => v.Id == mselId, ct);
            if (msel == null)
                throw new EntityNotFoundException<MselEntity>();

            // user must be a Content Developer or a MSEL owner
            if (!(await _authorizationService.AuthorizeAsync(_user, null, new ContentDeveloperRequirement())).Succeeded &&
                !(await MselOwnerRequirement.IsMet(_user.GetId(), msel.Id, _context)))
                throw new ForbiddenException();

            var items = await _context.Invitations
                .Where(tc => tc.MselId == mselId)
                .ToListAsync(ct);

            return _mapper.Map<IEnumerable<Invitation>>(items);
        }

        public async Task<ViewModels.Invitation> GetAsync(Guid id, CancellationToken ct)
        {
            var invitation = await _context.Invitations.SingleOrDefaultAsync(v => v.Id == id, ct);
            if (invitation == null)
                throw new EntityNotFoundException<MselEntity>();

            // the user must be a Content Developer or a MSEL Viewer
            if (!(await _authorizationService.AuthorizeAsync(_user, null, new ContentDeveloperRequirement())).Succeeded &&
                !(await MselViewRequirement.IsMet(_user.GetId(), invitation.MselId, _context)))
            {
                throw new ForbiddenException();
            }

            var item = await _context.Invitations
                .SingleOrDefaultAsync(o => o.Id == id, ct);

            return _mapper.Map<Invitation>(item);
        }

        public async Task<ViewModels.Invitation> CreateAsync(ViewModels.Invitation invitation, CancellationToken ct)
        {
            var msel = await _context.Msels.SingleOrDefaultAsync(v => v.Id == invitation.MselId, ct);
            if (msel == null)
                throw new EntityNotFoundException<MselEntity>();

            // user must be a Content Developer or a MSEL owner
            if (!(await _authorizationService.AuthorizeAsync(_user, null, new ContentDeveloperRequirement())).Succeeded &&
                !(await MselOwnerRequirement.IsMet(_user.GetId(), msel.Id, _context)))
                throw new ForbiddenException();

            var invitationEntity = _mapper.Map<InvitationEntity>(invitation);
            invitationEntity.Id = invitationEntity.Id != Guid.Empty ? invitationEntity.Id : Guid.NewGuid();

            _context.Invitations.Add(invitationEntity);
            await _context.SaveChangesAsync(ct);

            return await GetAsync(invitationEntity.Id, ct);
        }

        public async Task<ViewModels.Invitation> UpdateAsync(Guid id, ViewModels.Invitation invitation, CancellationToken ct)
        {
            // user must be a Content Developer or a MSEL owner
            if (!(await _authorizationService.AuthorizeAsync(_user, null, new ContentDeveloperRequirement())).Succeeded &&
                !(await MselOwnerRequirement.IsMet(_user.GetId(), invitation.MselId, _context)))
                throw new ForbiddenException();

            var invitationToUpdate = await _context.Invitations.SingleOrDefaultAsync(v => v.Id == id, ct);
            if (invitationToUpdate == null)
                throw new EntityNotFoundException<Invitation>();

            _mapper.Map(invitation, invitationToUpdate);

            _context.Invitations.Update(invitationToUpdate);
            await _context.SaveChangesAsync(ct);

            invitation = await GetAsync(invitationToUpdate.Id, ct);

            return invitation;
        }

        public async Task<bool> DeleteAsync(Guid id, CancellationToken ct)
        {
            var invitationToDelete = await _context.Invitations.SingleOrDefaultAsync(v => v.Id == id, ct);
            if (invitationToDelete == null)
                throw new EntityNotFoundException<Invitation>();

            // user must be a Content Developer or a MSEL owner
            if (!(await _authorizationService.AuthorizeAsync(_user, null, new ContentDeveloperRequirement())).Succeeded &&
                !(await MselOwnerRequirement.IsMet(_user.GetId(), invitationToDelete.MselId, _context)))
                throw new ForbiddenException();

            _context.Invitations.Remove(invitationToDelete);
            await _context.SaveChangesAsync(ct);

            return true;
        }

    }
}

