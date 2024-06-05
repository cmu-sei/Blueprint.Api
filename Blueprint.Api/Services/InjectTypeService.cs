// Copyright 2024 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact injectType@sei.cmu.edu for full terms.

using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using AutoMapper;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Blueprint.Api.Data;
using Blueprint.Api.Data.Models;
using Blueprint.Api.Infrastructure.Extensions;
using Blueprint.Api.Infrastructure.Authorization;
using Blueprint.Api.Infrastructure.Exceptions;
using Blueprint.Api.ViewModels;

namespace Blueprint.Api.Services
{
    public interface IInjectTypeService
    {
        Task<IEnumerable<ViewModels.InjectType>> GetAsync(CancellationToken ct);
        Task<ViewModels.InjectType> GetAsync(Guid id, CancellationToken ct);
        // Task<IEnumerable<ViewModels.InjectType>> GetByUserIdAsync(Guid userId, CancellationToken ct);
        Task<ViewModels.InjectType> CreateAsync(ViewModels.InjectType injectType, CancellationToken ct);
        Task<ViewModels.InjectType> UpdateAsync(Guid id, ViewModels.InjectType injectType, CancellationToken ct);
        Task<bool> DeleteAsync(Guid id, CancellationToken ct);
    }

    public class InjectTypeService : IInjectTypeService
    {
        private readonly BlueprintContext _context;
        private readonly IAuthorizationService _authorizationService;
        private readonly ClaimsPrincipal _user;
        private readonly IMapper _mapper;

        public InjectTypeService(BlueprintContext context, IAuthorizationService authorizationService, IPrincipal user, IMapper mapper)
        {
            _context = context;
            _authorizationService = authorizationService;
            _user = user as ClaimsPrincipal;
            _mapper = mapper;
        }

        public async Task<IEnumerable<ViewModels.InjectType>> GetAsync(CancellationToken ct)
        {
            if (!(await _authorizationService.AuthorizeAsync(_user, null, new FullRightsRequirement())).Succeeded)
                throw new ForbiddenException();

            var items = await _context.InjectTypes
                .ToListAsync(ct);

            return _mapper.Map<IEnumerable<InjectType>>(items);
        }

        public async Task<ViewModels.InjectType> GetAsync(Guid id, CancellationToken ct)
        {
            if (!(await _authorizationService.AuthorizeAsync(_user, null, new FullRightsRequirement())).Succeeded)
                throw new ForbiddenException();

            var item = await _context.InjectTypes
                .SingleOrDefaultAsync(o => o.Id == id, ct);

            return _mapper.Map<InjectType>(item);
        }

        public async Task<ViewModels.InjectType> CreateAsync(ViewModels.InjectType injectType, CancellationToken ct)
        {
            if (!(await _authorizationService.AuthorizeAsync(_user, null, new FullRightsRequirement())).Succeeded)
                throw new ForbiddenException();

            injectType.Id = injectType.Id != Guid.Empty ? injectType.Id : Guid.NewGuid();
            injectType.DateCreated = DateTime.UtcNow;
            injectType.CreatedBy = _user.GetId();
            injectType.DateModified = null;
            injectType.ModifiedBy = null;
            var injectTypeEntity = _mapper.Map<InjectTypeEntity>(injectType);

            _context.InjectTypes.Add(injectTypeEntity);
            await _context.SaveChangesAsync(ct);

            return await GetAsync(injectTypeEntity.Id, ct);
        }

        public async Task<ViewModels.InjectType> UpdateAsync(Guid id, ViewModels.InjectType injectType, CancellationToken ct)
        {
            if (!(await _authorizationService.AuthorizeAsync(_user, null, new FullRightsRequirement())).Succeeded)
                throw new ForbiddenException();

            var injectTypeToUpdate = await _context.InjectTypes.SingleOrDefaultAsync(v => v.Id == id, ct);

            if (injectTypeToUpdate == null)
                throw new EntityNotFoundException<InjectType>();

            injectType.CreatedBy = injectTypeToUpdate.CreatedBy;
            injectType.DateCreated = injectTypeToUpdate.DateCreated;
            injectType.DateModified = DateTime.UtcNow;
            injectType.ModifiedBy = _user.GetId();
            _mapper.Map(injectType, injectTypeToUpdate);

            _context.InjectTypes.Update(injectTypeToUpdate);
            await _context.SaveChangesAsync(ct);

            return _mapper.Map(injectTypeToUpdate, injectType);
        }

        public async Task<bool> DeleteAsync(Guid id, CancellationToken ct)
        {
            if (!(await _authorizationService.AuthorizeAsync(_user, null, new FullRightsRequirement())).Succeeded)
                throw new ForbiddenException();

            var injectTypeToDelete = await _context.InjectTypes.SingleOrDefaultAsync(v => v.Id == id, ct);

            if (injectTypeToDelete == null)
                throw new EntityNotFoundException<InjectType>();

            _context.InjectTypes.Remove(injectTypeToDelete);
            await _context.SaveChangesAsync(ct);

            return true;
        }

    }
}

