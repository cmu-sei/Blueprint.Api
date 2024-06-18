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
using Blueprint.Api.Infrastructure.Options;
using Blueprint.Api.ViewModels;

namespace Blueprint.Api.Services
{
    public interface IInjectService
    {
        Task<IEnumerable<ViewModels.Injectm>> GetByCatalogAsync(Guid catalogId, CancellationToken ct);
        Task<ViewModels.Injectm> GetAsync(Guid id, CancellationToken ct);
        Task<ViewModels.Injectm> CreateAsync(Guid catalogId, ViewModels.Injectm inject, CancellationToken ct);
        Task<ViewModels.Injectm> UpdateAsync(Guid id, ViewModels.Injectm inject, CancellationToken ct);
        Task<bool> DeleteAsync(Guid id, CancellationToken ct);
    }

    public class InjectService : IInjectService
    {
        private readonly BlueprintContext _context;
        private readonly IAuthorizationService _authorizationService;
        private readonly ClaimsPrincipal _user;
        private readonly IMapper _mapper;
        private readonly DatabaseOptions _options;


        public InjectService(
            BlueprintContext context,
            IAuthorizationService authorizationService,
            IPrincipal user,
            IMapper mapper,
            DatabaseOptions options)
        {
            _context = context;
            _authorizationService = authorizationService;
            _user = user as ClaimsPrincipal;
            _mapper = mapper;
            _options = options;
        }

        public async Task<IEnumerable<ViewModels.Injectm>> GetByCatalogAsync(Guid catalogId, CancellationToken ct)
        {
            // user must be a Content Developer or a Catalog viewer
            if (!(await _authorizationService.AuthorizeAsync(_user, null, new ContentDeveloperRequirement())).Succeeded &&
                !await CatalogViewRequirement.IsMet(_user.GetId(), catalogId, _context))
                throw new ForbiddenException();

            var injects = await _context.CatalogInjects
                .Where(i => i.CatalogId == catalogId)
                .Select(m => m.Inject)
                .ToListAsync(ct);
            return _mapper.Map<IEnumerable<Injectm>>(injects);
        }

        public async Task<ViewModels.Injectm> GetAsync(Guid id, CancellationToken ct)
        {
            var item = await _context.Injects
                .Include(se => se.DataValues)
                .SingleAsync(a => a.Id == id, ct);

            if (item == null)
                throw new EntityNotFoundException<InjectEntity>();

            if (!(await _authorizationService.AuthorizeAsync(_user, null, new ContentDeveloperRequirement())).Succeeded)
            {
                var userId = _user.GetId();
                var unitIdList = await _context.CatalogUnits
                    .Where(m => m.Id == userId)
                    .Select(m => m.UnitId)
                    .ToListAsync(ct);
                var catalogIdList = await _context.CatalogUnits
                    .Where(m => unitIdList.Contains(m.UnitId))
                    .Select(m => m.CatalogId)
                    .ToListAsync(ct);
                var isAuthorized = await _context.CatalogInjects
                    .Where(m => catalogIdList.Contains(m.CatalogId))
                    .AnyAsync(ct);
                if (!isAuthorized)
                    throw new ForbiddenException();
            }

            return _mapper.Map<ViewModels.Injectm>(item);
        }

        public async Task<ViewModels.Injectm> CreateAsync(Guid catalogId, Injectm inject, CancellationToken ct)
        {
            // user must be a Content Developer
            if (!(await _authorizationService.AuthorizeAsync(_user, null, new ContentDeveloperRequirement())).Succeeded)
                throw new ForbiddenException();

            // start a transaction, because we may also update DataValues
            await _context.Database.BeginTransactionAsync();
            // create the inject
            inject.Id = inject.Id != Guid.Empty ? inject.Id : Guid.NewGuid();
            inject.DateCreated = DateTime.UtcNow;
            inject.CreatedBy = _user.GetId();
            inject.DateModified = null;
            inject.ModifiedBy = null;
            var injectEntity = _mapper.Map<InjectEntity>(inject);
            _context.Injects.Add(injectEntity);
            await _context.SaveChangesAsync(ct);
            // create the associated data values
            var dataFieldList = await _context.InjectTypes
                .Where(m => m.Id == inject.InjectTypeId)
                .Select(m => m.DataFields)
                .SingleOrDefaultAsync(ct);
            foreach (var dataField in dataFieldList)
            {
                var dataValue = inject.DataValues
                    .FirstOrDefault(dv => dv.DataFieldId == dataField.Id);
                if (dataValue == null)
                {
                    dataValue = new DataValue();
                    dataValue.DataFieldId = dataField.Id;
                }
                dataValue.Id = Guid.NewGuid();
                dataValue.CreatedBy = inject.CreatedBy;
                dataValue.DateCreated = inject.DateCreated;
                dataValue.DateModified = null;
                dataValue.ModifiedBy = null;
                var dataValueEntity = _mapper.Map<DataValueEntity>(dataValue);
                _context.DataValues.Add(dataValueEntity);
            }
            await _context.SaveChangesAsync(ct);
            // update the Catalog modified info
            await ServiceUtilities.SetCatalogModifiedAsync(catalogId, injectEntity.CreatedBy, injectEntity.DateCreated, _context, ct);
            // commit the transaction
            await _context.Database.CommitTransactionAsync(ct);

            return  _mapper.Map<ViewModels.Injectm>(injectEntity);
        }

        public async Task<ViewModels.Injectm> UpdateAsync(Guid id, ViewModels.Injectm inject, CancellationToken ct)
        {
            // user must be a Content Developer
            if (!(await _authorizationService.AuthorizeAsync(_user, null, new ContentDeveloperRequirement())).Succeeded)
                throw new ForbiddenException();

            // make sure entity exists
            var injectToUpdate = await _context.Injects.SingleOrDefaultAsync(v => v.Id == id, ct);
            if (injectToUpdate == null)
                throw new EntityNotFoundException<InjectEntity>($"Inject not found {id}.");

            // start a transaction, because we may also update DataValues and other injects
            await _context.Database.BeginTransactionAsync();
            // determine if row indexes need updated for the other injects on this Catalog
            // update this inject
            inject.CreatedBy = injectToUpdate.CreatedBy;
            inject.DateCreated = injectToUpdate.DateCreated;
            inject.ModifiedBy = _user.GetId();
            inject.DateModified = DateTime.UtcNow;
            _mapper.Map(inject, injectToUpdate);
            _context.Injects.Update(injectToUpdate);
            await _context.SaveChangesAsync(ct);
            // get the DataField IDs for this Catalog
            var dataFieldList = await _context.InjectTypes
                .Where(m => m.Id == inject.InjectTypeId)
                .Select(m => m.DataFields)
                .SingleOrDefaultAsync(ct);
            // update the data values
            foreach (var dataField in dataFieldList)
            {
                var dataValueToUpdate = await _context.DataValues
                    .SingleOrDefaultAsync(dv => dv.InjectId == inject.Id && dv.DataFieldId == dataField.Id, ct);
                var dataValue = inject.DataValues
                    .SingleOrDefault(dv => dv.InjectId == inject.Id && dv.DataFieldId == dataField.Id);
                if (dataValueToUpdate == null)
                {
                    dataValue.Id = Guid.NewGuid();
                    dataValue.CreatedBy = (Guid)inject.ModifiedBy;
                    dataValue.DateCreated = (DateTime)inject.DateModified;
                    dataValue.DateModified = dataValue.DateCreated;
                    dataValue.ModifiedBy = dataValue.CreatedBy;
                    var dataValueEntity = _mapper.Map<DataValueEntity>(dataValue);
                    _context.DataValues.Add(dataValueEntity);
                }
                else if (dataValue.Value != dataValueToUpdate.Value)
                {
                    // update the DataValue
                    dataValueToUpdate.ModifiedBy = injectToUpdate.ModifiedBy;
                    dataValueToUpdate.DateModified = injectToUpdate.DateModified;
                    dataValueToUpdate.Value = dataValue.Value;
                    _context.DataValues.Update(dataValueToUpdate);
                }
            }
            await _context.SaveChangesAsync(ct);
            // commit the transaction
            await _context.Database.CommitTransactionAsync(ct);

            return _mapper.Map<ViewModels.Injectm>(injectToUpdate);
        }

        public async Task<bool> DeleteAsync(Guid id, CancellationToken ct)
        {
            // user must be a Content Developer
            if (!(await _authorizationService.AuthorizeAsync(_user, null, new ContentDeveloperRequirement())).Succeeded)
                throw new ForbiddenException();

            var injectToDelete = await _context.Injects.SingleOrDefaultAsync(v => v.Id == id, ct);
            if (injectToDelete == null)
                throw new EntityNotFoundException<InjectEntity>();

            // start a transaction, because we may also update DataValues and other injects
            await _context.Database.BeginTransactionAsync();
            _context.Injects.Remove(injectToDelete);
            await _context.SaveChangesAsync(ct);
            // commit the transaction
            await _context.Database.CommitTransactionAsync(ct);

            return true;
        }

    }

 }
