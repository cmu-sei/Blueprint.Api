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
    public interface IDataFieldService
    {
        Task<IEnumerable<ViewModels.DataField>> GetByMselAsync(Guid mselId, CancellationToken ct);
        Task<ViewModels.DataField> GetAsync(Guid id, CancellationToken ct);
        Task<ViewModels.DataField> CreateAsync(ViewModels.DataField dataField, CancellationToken ct);
        Task<ViewModels.DataField> UpdateAsync(Guid id, ViewModels.DataField dataField, CancellationToken ct);
        Task<bool> DeleteAsync(Guid id, CancellationToken ct);
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

        public async Task<IEnumerable<ViewModels.DataField>> GetByMselAsync(Guid mselId, CancellationToken ct)
        {
            if (!(await _authorizationService.AuthorizeAsync(_user, null, new BaseUserRequirement())).Succeeded)
                throw new ForbiddenException();

            var dataFieldEntities = await _context.DataFields
                .Where(dataField => dataField.MselId == mselId)
                .ToListAsync();

            return _mapper.Map<IEnumerable<DataField>>(dataFieldEntities).ToList();;
        }

        public async Task<ViewModels.DataField> GetAsync(Guid id, CancellationToken ct)
        {
            if (!(await _authorizationService.AuthorizeAsync(_user, null, new BaseUserRequirement())).Succeeded)
                throw new ForbiddenException();

            var item = await _context.DataFields.SingleAsync(dataField => dataField.Id == id, ct);

            return _mapper.Map<DataField>(item);
        }

        public async Task<ViewModels.DataField> CreateAsync(ViewModels.DataField dataField, CancellationToken ct)
        {
            // must be a content developer or MSEL owner
            // Content developers and MSEL owners can update anything, others require condition checks
            if (!(await _authorizationService.AuthorizeAsync(_user, null, new ContentDeveloperRequirement())).Succeeded &&
                !(await MselOwnerRequirement.IsMet(_user.GetId(), dataField.MselId, _context)))
                throw new ForbiddenException();

            // start a transaction, because we may also update other data fields
            await _context.Database.BeginTransactionAsync();
            dataField.Id = dataField.Id != Guid.Empty ? dataField.Id : Guid.NewGuid();
            dataField.DateCreated = DateTime.UtcNow;
            dataField.CreatedBy = _user.GetId();
            dataField.DateModified = null;
            dataField.ModifiedBy = null;
            var dataFieldEntity = _mapper.Map<DataFieldEntity>(dataField);
            _context.DataFields.Add(dataFieldEntity);
            await _context.SaveChangesAsync(ct);
            // reorder the data fields
            await Reorder(dataFieldEntity, ct);
            // add data values for the new data field
            await AddNewDataValues(dataFieldEntity, ct);
            // commit the transaction
            await _context.Database.CommitTransactionAsync(ct);
            dataField = await GetAsync(dataFieldEntity.Id, ct);

            return dataField;
        }

        public async Task<ViewModels.DataField> UpdateAsync(Guid id, ViewModels.DataField dataField, CancellationToken ct)
        {
            // must be a content developer or MSEL owner
            // Content developers and MSEL owners can update anything, others require condition checks
            if (!(await _authorizationService.AuthorizeAsync(_user, null, new ContentDeveloperRequirement())).Succeeded &&
                !(await MselOwnerRequirement.IsMet(_user.GetId(), dataField.MselId, _context)))
                throw new ForbiddenException();

            var dataFieldToUpdate = await _context.DataFields.SingleOrDefaultAsync(v => v.Id == id, ct);
            if (dataFieldToUpdate == null)
                throw new EntityNotFoundException<DataField>();

            // start a transaction, because we may also update other data fields
            await _context.Database.BeginTransactionAsync();
            var updateDisplayOrder = dataFieldToUpdate.DisplayOrder != dataField.DisplayOrder;
            // update values
            dataField.CreatedBy = dataFieldToUpdate.CreatedBy;
            dataField.DateCreated = dataFieldToUpdate.DateCreated;
            dataField.ModifiedBy = _user.GetId();
            dataField.DateModified = DateTime.UtcNow;
            _mapper.Map(dataField, dataFieldToUpdate);
            _context.DataFields.Update(dataFieldToUpdate);
            await _context.SaveChangesAsync(ct);
            // reorder the data fields if necessary
            if (updateDisplayOrder)
            {
                await Reorder(dataFieldToUpdate, ct);
            }
            // commit the transaction
            await _context.Database.CommitTransactionAsync(ct);

            dataField = await GetAsync(dataFieldToUpdate.Id, ct);

            return dataField;
        }

        public async Task<bool> DeleteAsync(Guid id, CancellationToken ct)
        {
            var dataFieldToDelete = await _context.DataFields.SingleOrDefaultAsync(v => v.Id == id, ct);
            if (dataFieldToDelete == null)
                throw new EntityNotFoundException<DataField>();

            // must be a content developer or MSEL owner
            // Content developers and MSEL owners can update anything, others require condition checks
            if (!(await _authorizationService.AuthorizeAsync(_user, null, new ContentDeveloperRequirement())).Succeeded &&
                !(await MselOwnerRequirement.IsMet(_user.GetId(), dataFieldToDelete.MselId, _context)))
                throw new ForbiddenException();

            _context.DataFields.Remove(dataFieldToDelete);
            await _context.SaveChangesAsync(ct);

            return true;
        }

        //
        // Helper Methods
        //

        private async Task Reorder(DataFieldEntity dataFieldEntity, CancellationToken ct)
        {
            var dataFields = await _context.DataFields
                .Where(se => se.MselId == dataFieldEntity.MselId &&
                    se.DisplayOrder >= dataFieldEntity.DisplayOrder &&
                    se.Id != dataFieldEntity.Id)
                .OrderBy(se => se.DisplayOrder)
                .ToListAsync(ct);
            if (dataFields.Any(se => se.DisplayOrder == dataFieldEntity.DisplayOrder))
            {
                for (var i = dataFields.Count; i > 0; i--)
                {
                    dataFields[i-1].DisplayOrder = dataFieldEntity.DisplayOrder + i;
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

