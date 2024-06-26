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
using Blueprint.Api.ViewModels;
using Blueprint.Api.Infrastructure.JsonConverters;

namespace Blueprint.Api.Services
{
    public interface IDataFieldService
    {
        Task<IEnumerable<ViewModels.DataField>> GetTemplatesAsync(CancellationToken ct);
        Task<IEnumerable<ViewModels.DataField>> GetByMselAsync(Guid mselId, CancellationToken ct);
        Task<IEnumerable<ViewModels.DataField>> GetByInjectTypeAsync(Guid injectTypeId, CancellationToken ct);
        Task<ViewModels.DataField> GetAsync(Guid id, CancellationToken ct);
         Task<ViewModels.DataField> CreateAsync(ViewModels.DataField dataField, CancellationToken ct);
         Task<ViewModels.DataField> UpdateAsync(Guid id, ViewModels.DataField dataField, CancellationToken ct);
         Task<Guid> DeleteAsync(Guid id, CancellationToken ct);
    }

    public class DataFieldService : IDataFieldService
    {
        private readonly BlueprintContext _context;
        private readonly IAuthorizationService _authorizationService;
        private readonly ClaimsPrincipal _user;
        private readonly IMapper _mapper;

        public DataFieldService(
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

        public async Task<IEnumerable<ViewModels.DataField>> GetTemplatesAsync(CancellationToken ct)
        {
            if (!(await _authorizationService.AuthorizeAsync(_user, null, new BaseUserRequirement())).Succeeded)
                throw new ForbiddenException();

            var dataFieldEntities = await _context.DataFields
                .Where(dataField => dataField.IsTemplate)
                .Include(dataField => dataField.DataOptions)
                .ToListAsync(ct);

            return _mapper.Map<IEnumerable<DataField>>(dataFieldEntities).ToList();;
        }

        public async Task<IEnumerable<ViewModels.DataField>> GetByMselAsync(Guid mselId, CancellationToken ct)
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

            var dataFieldEntities = await _context.DataFields
                .Where(dataField => dataField.MselId == mselId)
                .Include(df => df.DataOptions)
                .ToListAsync(ct);

            return _mapper.Map<IEnumerable<DataField>>(dataFieldEntities).ToList();;
        }

        public async Task<IEnumerable<ViewModels.DataField>> GetByInjectTypeAsync(Guid injectTypeId, CancellationToken ct)
        {
            if (!(await _authorizationService.AuthorizeAsync(_user, null, new BaseUserRequirement())).Succeeded)
                throw new ForbiddenException();

            var dataFieldEntities = await _context.DataFields
                .Where(dataField => dataField.InjectTypeId == injectTypeId)
                .Include(df => df.DataOptions)
                .ToListAsync(ct);

            return _mapper.Map<IEnumerable<DataField>>(dataFieldEntities).ToList();;
        }

        public async Task<ViewModels.DataField> GetAsync(Guid id, CancellationToken ct)
        {
            var item = await _context.DataFields
                .Include(dataField => dataField.DataOptions)
                .SingleAsync(dataField => dataField.Id == id, ct);

            if (item == null)
                throw new EntityNotFoundException<DataValueEntity>("DataValue not found: " + id);

            if (!(await _authorizationService.AuthorizeAsync(_user, null, new ContentDeveloperRequirement())).Succeeded &&
                !await MselUserRequirement.IsMet(_user.GetId(), item.MselId, _context))
                throw new ForbiddenException();

            return _mapper.Map<DataField>(item);
        }

        public async Task<ViewModels.DataField> CreateAsync(ViewModels.DataField dataField, CancellationToken ct)
        {
            var userId = _user.GetId();
            var dateNow = DateTime.UtcNow;
            // must be a content developer or MSEL owner
            // Content developers and MSEL owners can update anything, others require condition checks
            if (!(await _authorizationService.AuthorizeAsync(_user, null, new ContentDeveloperRequirement())).Succeeded &&
                !await MselOwnerRequirement.IsMet(userId, dataField.MselId, _context))
                throw new ForbiddenException();

            // start a transaction
            await _context.Database.BeginTransactionAsync();
            dataField.Id = dataField.Id != Guid.Empty ? dataField.Id : Guid.NewGuid();
            dataField.DateCreated = dateNow;
            dataField.CreatedBy = userId;
            dataField.DateModified = null;
            dataField.ModifiedBy = null;
            var dataFieldEntity = _mapper.Map<DataFieldEntity>(dataField);
            _context.DataFields.Add(dataFieldEntity);
            // update DataOptions
            foreach (var dataOption in dataField.DataOptions)
            {
                dataOption.DataFieldId = dataField.Id;
                dataOption.Id = Guid.NewGuid();
                dataOption.DateCreated = dateNow;
                dataOption.CreatedBy = userId;
                var newDataOption = _mapper.Map<DataOptionEntity>(dataOption);
                _context.DataOptions.Add(newDataOption);
            }
            await _context.SaveChangesAsync(ct);
            // If on a MSEL
            if (dataFieldEntity.MselId != null)
            {
                // add data values for the new data field
                await AddNewDataValues(dataFieldEntity, ct);
                // reorder the data fields
                await Reorder(dataFieldEntity, false, ct);
                // update the MSEL modified info
                await ServiceUtilities.SetMselModifiedAsync(dataField.MselId, dataField.CreatedBy, dataField.DateCreated, _context, ct);
                // commit the transaction
            }
            else if (dataFieldEntity.InjectTypeId != null)
            {
                // reorder the data fields
                await Reorder(dataFieldEntity, false, ct);
            }
            await _context.Database.CommitTransactionAsync(ct);

            return _mapper.Map<DataField>(dataFieldEntity);
        }

        public async Task<ViewModels.DataField> UpdateAsync(Guid id, ViewModels.DataField dataField, CancellationToken ct)
        {
            var exists = await _context.DataFields
                .AnyAsync(v => v.Id == id, ct);
            if (!exists)
                throw new EntityNotFoundException<DataField>();

            var userId = _user.GetId();
            var dateNow = DateTime.UtcNow;
            // must be a content developer or MSEL owner
            // Content developers and MSEL owners can update anything, others require condition checks
            if (!(await _authorizationService.AuthorizeAsync(_user, null, new ContentDeveloperRequirement())).Succeeded &&
                !(await MselOwnerRequirement.IsMet(userId, dataField.MselId, _context)))
                throw new ForbiddenException();

            // start a transaction
            await _context.Database.BeginTransactionAsync();
            var existingDataOptions = await _context.DataOptions
                .Where(dataField => dataField.DataFieldId == id)
                .ToListAsync();
            // update DataOptions
            foreach (var dataOption in existingDataOptions)
            {
                if(!dataField.DataOptions.Any(d => d.Id == dataOption.Id))
                {
                    _context.DataOptions.Remove(dataOption);
                }
            }
            foreach (var dataOption in dataField.DataOptions)
            {
                dataOption.DataFieldId = id;
                var existingDataOption = existingDataOptions
                    .Where(d => d.Id == dataOption.Id)
                    .SingleOrDefault();
                if (existingDataOption != null)
                {
                    dataOption.DateModified = dateNow;
                    dataOption.ModifiedBy = userId;
                    _context.Entry(existingDataOption).CurrentValues.SetValues(dataOption);
                }
                else
                {
                    dataOption.Id = Guid.NewGuid();
                    dataOption.DateCreated = dateNow;
                    dataOption.CreatedBy = userId;
                    var newDataOption = _mapper.Map<DataOptionEntity>(dataOption);
                    _context.DataOptions.Add(newDataOption);
                }
            }
            await _context.SaveChangesAsync(ct);
            // get the existing dataField
            var dataFieldToUpdate = await _context.DataFields
                .Include(df => df.DataOptions)
                .SingleOrDefaultAsync(v => v.Id == id, ct);
            // get the initial and final display order for this data field
            var first = Math.Min(dataField.DisplayOrder, dataFieldToUpdate.DisplayOrder);
            var last = Math.Max(dataField.DisplayOrder, dataFieldToUpdate.DisplayOrder);
            var updateDisplayOrder = dataFieldToUpdate.DisplayOrder != dataField.DisplayOrder;
            var newIndexIsGreater = dataField.DisplayOrder > dataFieldToUpdate.DisplayOrder;
            // update values
            dataField.CreatedBy = dataFieldToUpdate.CreatedBy;
            dataField.DateCreated = dataFieldToUpdate.DateCreated;
            dataField.ModifiedBy = userId;
            dataField.DateModified = dateNow;
            _mapper.Map(dataField, dataFieldToUpdate);
            _context.DataFields.Update(dataFieldToUpdate);
            await _context.SaveChangesAsync(ct);
            // reorder the data fields if necessary
            if (updateDisplayOrder)
            {
                await Reorder(dataFieldToUpdate, newIndexIsGreater, ct);
            }
            // update the MSEL modified info
            await ServiceUtilities.SetMselModifiedAsync(dataField.MselId, dataField.ModifiedBy, dataField.DateModified, _context, ct);
            // commit the transaction
            await _context.Database.CommitTransactionAsync(ct);

            return _mapper.Map<DataField>(dataFieldToUpdate);;
        }

        public async Task<Guid> DeleteAsync(Guid id, CancellationToken ct)
        {
            var dataFieldToDelete = await _context.DataFields.SingleOrDefaultAsync(v => v.Id == id, ct);
            if (dataFieldToDelete == null)
                throw new EntityNotFoundException<DataField>();

            // must be a content developer or MSEL owner
            // Content developers and MSEL owners can update anything, others require condition checks
            if (!(await _authorizationService.AuthorizeAsync(_user, null, new ContentDeveloperRequirement())).Succeeded &&
                !(await MselOwnerRequirement.IsMet(_user.GetId(), dataFieldToDelete.MselId, _context)))
                throw new ForbiddenException();

            // start a transaction, because we may also update other data fields
            await _context.Database.BeginTransactionAsync();
            _context.DataFields.Remove(dataFieldToDelete);
            await _context.SaveChangesAsync(ct);
            await Reorder(dataFieldToDelete, false, ct);
            // update the MSEL modified info
            await ServiceUtilities.SetMselModifiedAsync(dataFieldToDelete.MselId, _user.GetId(), DateTime.UtcNow, _context, ct);
            // commit the transaction
            await _context.Database.CommitTransactionAsync(ct);

            return id;
        }

        //
        // Helper Methods
        //

        private async Task Reorder(DataFieldEntity dataFieldEntity, bool newIndexIsGreater, CancellationToken ct)
        {
            var dataFields = await _context.DataFields
                .Where(df => df.MselId == dataFieldEntity.MselId && df.InjectTypeId == dataFieldEntity.InjectTypeId)
                .OrderBy(se => se.DisplayOrder)
                .ToListAsync(ct);
            var isHere = dataFields.Any(df => df.Id == dataFieldEntity.Id);
            for (var i = 0; i < dataFields.Count; i++)
            {
                if (dataFields[i].Id != dataFieldEntity.Id)
                {
                    if (isHere && dataFields[i].DisplayOrder == dataFieldEntity.DisplayOrder)
                    {
                        if (newIndexIsGreater)
                        {
                            dataFields[i].DisplayOrder = dataFieldEntity.DisplayOrder - 1;
                        }
                        else
                        {
                            dataFields[i].DisplayOrder = dataFieldEntity.DisplayOrder + 1;
                        }
                    }
                    else
                    {
                        dataFields[i].DisplayOrder = i + 1;
                    }
                }
                else if (dataFieldEntity.DisplayOrder > dataFields.Count)
                {
                    dataFieldEntity.DisplayOrder = dataFields.Count;
                }
            }
            await _context.SaveChangesAsync(ct);
        }

        private async Task AddNewDataValues(DataFieldEntity dataFieldEntity, CancellationToken ct)
        {
            var scenarioEventIds = await _context.ScenarioEvents
                .Where(se => se.MselId == dataFieldEntity.MselId)
                .Select(se => se.Id)
                .ToListAsync(ct);
            foreach (var scenarioEventId in scenarioEventIds)
            {
                var dataValueEntity = new DataValueEntity() {
                    ScenarioEventId = scenarioEventId,
                    DataFieldId = dataFieldEntity.Id
                };
                _context.DataValues.Add(dataValueEntity);
            }
            await _context.SaveChangesAsync(ct);
        }

    }
}
