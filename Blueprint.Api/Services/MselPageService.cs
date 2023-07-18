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
using Blueprint.Api.Data;
using Blueprint.Api.Data.Models;
using Blueprint.Api.Infrastructure.Authorization;
using Blueprint.Api.Infrastructure.Exceptions;
using Blueprint.Api.Infrastructure.Extensions;
using Blueprint.Api.ViewModels;

namespace Blueprint.Api.Services
{
    public interface IMselPageService
    {
        Task<IEnumerable<ViewModels.MselPage>> GetByMselAsync(Guid mselId, CancellationToken ct);
        Task<ViewModels.MselPage> GetAsync(Guid id, CancellationToken ct);
        Task<ViewModels.MselPage> CreateAsync(ViewModels.MselPage mselPage, CancellationToken ct);
        Task<ViewModels.MselPage> UpdateAsync(Guid id, ViewModels.MselPage mselPage, CancellationToken ct);
        Task<bool> DeleteAsync(Guid id, CancellationToken ct);
    }

    public class MselPageService : IMselPageService
    {
        private readonly BlueprintContext _context;
        private readonly IAuthorizationService _authorizationService;
        private readonly ClaimsPrincipal _user;
        private readonly IMapper _mapper;

        public MselPageService(BlueprintContext context, IAuthorizationService authorizationService, IPrincipal user, IMapper mapper)
        {
            _context = context;
            _authorizationService = authorizationService;
            _user = user as ClaimsPrincipal;
            _mapper = mapper;
        }

        public async Task<IEnumerable<ViewModels.MselPage>> GetByMselAsync(Guid mselId, CancellationToken ct)
        {
            var msel = await _context.Msels.SingleOrDefaultAsync(v => v.Id == mselId, ct);
            if (msel == null)
                throw new EntityNotFoundException<MselEntity>();

            // user must be a Content Developer or a MSEL owner
            if (!(await _authorizationService.AuthorizeAsync(_user, null, new ContentDeveloperRequirement())).Succeeded &&
                !(await MselOwnerRequirement.IsMet(_user.GetId(), msel.Id, _context)))
                throw new ForbiddenException();

            var items = await _context.MselPages
                .Where(tc => tc.MselId == mselId)
                .ToListAsync(ct);

            return _mapper.Map<IEnumerable<MselPage>>(items);
        }

        public async Task<ViewModels.MselPage> GetAsync(Guid id, CancellationToken ct)
        {
            var mselPage = await _context.MselPages.SingleOrDefaultAsync(v => v.Id == id, ct);
            if (mselPage == null)
                throw new EntityNotFoundException<MselEntity>();

            // if AllCanView is set, user must be on a MSEL team,
            // otherwise, the user must be a Content Developer or a MSEL Viewer
            if (mselPage.AllCanView)
            {
                if (!(await MselUserRequirement.IsMet(_user.GetId(), mselPage.MselId, _context)))
                    throw new ForbiddenException();
            }
            else if (!(await _authorizationService.AuthorizeAsync(_user, null, new ContentDeveloperRequirement())).Succeeded &&
                !(await MselViewRequirement.IsMet(_user.GetId(), mselPage.MselId, _context)))
            {
                throw new ForbiddenException();
            }

            var item = await _context.MselPages
                .SingleOrDefaultAsync(o => o.Id == id, ct);

            return _mapper.Map<MselPage>(item);
        }

        public async Task<ViewModels.MselPage> CreateAsync(ViewModels.MselPage mselPage, CancellationToken ct)
        {
            var msel = await _context.Msels.SingleOrDefaultAsync(v => v.Id == mselPage.MselId, ct);
            if (msel == null)
                throw new EntityNotFoundException<MselEntity>();

            // user must be a Content Developer or a MSEL owner
            if (!(await _authorizationService.AuthorizeAsync(_user, null, new ContentDeveloperRequirement())).Succeeded &&
                !(await MselOwnerRequirement.IsMet(_user.GetId(), msel.Id, _context)))
                throw new ForbiddenException();

            var mselPageEntity = _mapper.Map<MselPageEntity>(mselPage);
            mselPageEntity.Id = mselPageEntity.Id != Guid.Empty ? mselPageEntity.Id : Guid.NewGuid();

            _context.MselPages.Add(mselPageEntity);
            await _context.SaveChangesAsync(ct);

            return await GetAsync(mselPageEntity.Id, ct);
        }

        public async Task<ViewModels.MselPage> UpdateAsync(Guid id, ViewModels.MselPage mselPage, CancellationToken ct)
        {
            // user must be a Content Developer or a MSEL owner
            if (!(await _authorizationService.AuthorizeAsync(_user, null, new ContentDeveloperRequirement())).Succeeded &&
                !(await MselOwnerRequirement.IsMet(_user.GetId(), mselPage.MselId, _context)))
                throw new ForbiddenException();

            var mselPageToUpdate = await _context.MselPages.SingleOrDefaultAsync(v => v.Id == id, ct);
            if (mselPageToUpdate == null)
                throw new EntityNotFoundException<MselPage>();

            _mapper.Map(mselPage, mselPageToUpdate);

            _context.MselPages.Update(mselPageToUpdate);
            await _context.SaveChangesAsync(ct);

            mselPage = await GetAsync(mselPageToUpdate.Id, ct);

            return mselPage;
        }

        public async Task<bool> DeleteAsync(Guid id, CancellationToken ct)
        {
            var mselPageToDelete = await _context.MselPages.SingleOrDefaultAsync(v => v.Id == id, ct);
            if (mselPageToDelete == null)
                throw new EntityNotFoundException<MselPage>();

            // user must be a Content Developer or a MSEL owner
            if (!(await _authorizationService.AuthorizeAsync(_user, null, new ContentDeveloperRequirement())).Succeeded &&
                !(await MselOwnerRequirement.IsMet(_user.GetId(), mselPageToDelete.MselId, _context)))
                throw new ForbiddenException();

            _context.MselPages.Remove(mselPageToDelete);
            await _context.SaveChangesAsync(ct);

            return true;
        }

    }
}

