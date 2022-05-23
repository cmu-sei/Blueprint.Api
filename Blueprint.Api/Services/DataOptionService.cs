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
using Blueprint.Api.Infrastructure.QueryParameters;
using Blueprint.Api.ViewModels;

namespace Blueprint.Api.Services
{
    public interface IDataOptionService
    {
        Task<IEnumerable<ViewModels.DataOption>> GetByDataFieldAsync(Guid mselId, CancellationToken ct);
        Task<ViewModels.DataOption> GetAsync(Guid id, CancellationToken ct);
        Task<ViewModels.DataOption> CreateAsync(ViewModels.DataOption dataOption, CancellationToken ct);
        Task<ViewModels.DataOption> UpdateAsync(Guid id, ViewModels.DataOption dataOption, CancellationToken ct);
        Task<bool> DeleteAsync(Guid id, CancellationToken ct);
    }

    public class DataOptionService : IDataOptionService
    {
        private readonly BlueprintContext _context;
        private readonly IAuthorizationService _authorizationService;
        private readonly ClaimsPrincipal _user;
        private readonly IMapper _mapper;

        public DataOptionService(
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

        public async Task<IEnumerable<ViewModels.DataOption>> GetByDataFieldAsync(Guid mselId, CancellationToken ct)
        {
            if (!(await _authorizationService.AuthorizeAsync(_user, null, new BaseUserRequirement())).Succeeded)
                throw new ForbiddenException();

            var dataOptionEntities = await _context.DataOptions
                .Where(dataOption => dataOption.DataFieldId == mselId)
                .ToListAsync();

            return _mapper.Map<IEnumerable<DataOption>>(dataOptionEntities).ToList();;
        }

        public async Task<ViewModels.DataOption> GetAsync(Guid id, CancellationToken ct)
        {
            if (!(await _authorizationService.AuthorizeAsync(_user, null, new BaseUserRequirement())).Succeeded)
                throw new ForbiddenException();

            var item = await _context.DataOptions.SingleAsync(dataOption => dataOption.Id == id, ct);

            return _mapper.Map<DataOption>(item);
        }

        public async Task<ViewModels.DataOption> CreateAsync(ViewModels.DataOption dataOption, CancellationToken ct)
        {
            if (!(await _authorizationService.AuthorizeAsync(_user, null, new ContentDeveloperRequirement())).Succeeded)
                throw new ForbiddenException();
            dataOption.Id = dataOption.Id != Guid.Empty ? dataOption.Id : Guid.NewGuid();
            dataOption.DateCreated = DateTime.UtcNow;
            dataOption.CreatedBy = _user.GetId();
            dataOption.DateModified = null;
            dataOption.ModifiedBy = null;
            var dataOptionEntity = _mapper.Map<DataOptionEntity>(dataOption);

            _context.DataOptions.Add(dataOptionEntity);
            await _context.SaveChangesAsync(ct);
            dataOption = await GetAsync(dataOptionEntity.Id, ct);

            return dataOption;
        }

        public async Task<ViewModels.DataOption> UpdateAsync(Guid id, ViewModels.DataOption dataOption, CancellationToken ct)
        {
            if (!(await _authorizationService.AuthorizeAsync(_user, null, new ContentDeveloperRequirement())).Succeeded)
                throw new ForbiddenException();

            var dataOptionToUpdate = await _context.DataOptions.SingleOrDefaultAsync(v => v.Id == id, ct);

            if (dataOptionToUpdate == null)
                throw new EntityNotFoundException<DataOption>();

            dataOption.CreatedBy = dataOptionToUpdate.CreatedBy;
            dataOption.DateCreated = dataOptionToUpdate.DateCreated;
            dataOption.ModifiedBy = _user.GetId();
            dataOption.DateModified = DateTime.UtcNow;
            _mapper.Map(dataOption, dataOptionToUpdate);

            _context.DataOptions.Update(dataOptionToUpdate);
            await _context.SaveChangesAsync(ct);

            dataOption = await GetAsync(dataOptionToUpdate.Id, ct);

            return dataOption;
        }

        public async Task<bool> DeleteAsync(Guid id, CancellationToken ct)
        {
            if (!(await _authorizationService.AuthorizeAsync(_user, null, new ContentDeveloperRequirement())).Succeeded)
                throw new ForbiddenException();

            var dataOptionToDelete = await _context.DataOptions.SingleOrDefaultAsync(v => v.Id == id, ct);

            if (dataOptionToDelete == null)
                throw new EntityNotFoundException<DataOption>();

            _context.DataOptions.Remove(dataOptionToDelete);
            await _context.SaveChangesAsync(ct);

            return true;
        }

    }
}

