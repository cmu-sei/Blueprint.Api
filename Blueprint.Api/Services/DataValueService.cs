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
    public interface IDataValueService
    {
        Task<ViewModels.DataValue> GetAsync(Guid id, CancellationToken ct);
        Task<ViewModels.DataValue> CreateAsync(ViewModels.DataValue DataValue, CancellationToken ct);
        Task<ViewModels.DataValue> UpdateAsync(Guid id, ViewModels.DataValue DataValue, CancellationToken ct);
        Task<bool> DeleteAsync(Guid id, CancellationToken ct);
    }

    public class DataValueService : IDataValueService
    {
        private readonly BlueprintContext _context;
        private readonly IAuthorizationService _authorizationService;
        private readonly ClaimsPrincipal _user;
        private readonly IMapper _mapper;

        public DataValueService(
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

        public async Task<ViewModels.DataValue> GetAsync(Guid id, CancellationToken ct)
        {
            if (!(await _authorizationService.AuthorizeAsync(_user, null, new BaseUserRequirement())).Succeeded)
                throw new ForbiddenException();

            var item = await _context.DataValues.FindAsync(id);
            if (item == null)
                throw new EntityNotFoundException<DataValueEntity>("DataValue not found: " + id);

            return _mapper.Map<DataValue>(item);
        }

        public async Task<ViewModels.DataValue> CreateAsync(ViewModels.DataValue DataValue, CancellationToken ct)
        {
            // user must be a Content Developer or be on the requested team and be able to submit
            if (!(await _authorizationService.AuthorizeAsync(_user, null, new ContentDeveloperRequirement())).Succeeded)
            {
                var mselId = await _context.DataFields
                    .Where(df => df.Id == DataValue.DataFieldId)
                    .Select(df => df.MselId)
                    .FirstOrDefaultAsync();
                if (!( await MselEditorRequirement.IsMet(_user.GetId(), mselId, _context)))
                    throw new ForbiddenException();
            }

            DataValue.Id = DataValue.Id != Guid.Empty ? DataValue.Id : Guid.NewGuid();
            DataValue.DateCreated = DateTime.UtcNow;
            DataValue.CreatedBy = _user.GetId();
            DataValue.DateModified = null;
            DataValue.ModifiedBy = null;
            var DataValueEntity = _mapper.Map<DataValueEntity>(DataValue);

            _context.DataValues.Add(DataValueEntity);
            await _context.SaveChangesAsync(ct);
            DataValue = await GetAsync(DataValueEntity.Id, ct);

            return DataValue;
        }

        public async Task<ViewModels.DataValue> UpdateAsync(Guid id, ViewModels.DataValue DataValue, CancellationToken ct)
        {
            var DataValueToUpdate = await _context.DataValues.SingleOrDefaultAsync(v => v.Id == id, ct);
            if (DataValueToUpdate == null)
                throw new EntityNotFoundException<DataValue>();

            // user must be a Content Developer or be on the requested team and be able to submit
            if (!(await _authorizationService.AuthorizeAsync(_user, null, new ContentDeveloperRequirement())).Succeeded)
            {
                var mselId = await _context.DataFields
                    .Where(df => df.Id == DataValueToUpdate.DataFieldId)
                    .Select(df => df.MselId)
                    .FirstOrDefaultAsync();
                if (!( await MselEditorRequirement.IsMet(_user.GetId(), mselId, _context)))
                    throw new ForbiddenException();
            }

            DataValue.CreatedBy = DataValueToUpdate.CreatedBy;
            DataValue.DateCreated = DataValueToUpdate.DateCreated;
            DataValue.ModifiedBy = _user.GetId();
            DataValue.DateModified = DateTime.UtcNow;
            _mapper.Map(DataValue, DataValueToUpdate);

            _context.DataValues.Update(DataValueToUpdate);
            await _context.SaveChangesAsync(ct);

            DataValue = await GetAsync(DataValueToUpdate.Id, ct);

            return DataValue;
        }

        public async Task<bool> DeleteAsync(Guid id, CancellationToken ct)
        {
            var DataValueToDelete = await _context.DataValues.SingleOrDefaultAsync(v => v.Id == id, ct);
            if (DataValueToDelete == null)
                throw new EntityNotFoundException<DataValue>();

            // user must be a Content Developer or be on the requested team and be able to submit
            if (!(await _authorizationService.AuthorizeAsync(_user, null, new ContentDeveloperRequirement())).Succeeded)
            {
                var mselId = await _context.DataFields
                    .Where(df => df.Id == DataValueToDelete.DataFieldId)
                    .Select(df => df.MselId)
                    .FirstOrDefaultAsync();
                if (!( await MselEditorRequirement.IsMet(_user.GetId(), mselId, _context)))
                    throw new ForbiddenException();
            }

            _context.DataValues.Remove(DataValueToDelete);
            await _context.SaveChangesAsync(ct);

            return true;
        }

    }
}

