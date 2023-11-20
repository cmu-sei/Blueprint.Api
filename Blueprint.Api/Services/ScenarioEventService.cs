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
using Blueprint.Api.Infrastructure.Options;
using Blueprint.Api.ViewModels;
using System.Security.Cryptography.X509Certificates;

namespace Blueprint.Api.Services
{
    public interface IScenarioEventService
    {
        Task<IEnumerable<ViewModels.ScenarioEvent>> GetByMselAsync(Guid mselId, CancellationToken ct);
        Task<ViewModels.ScenarioEvent> GetAsync(Guid id, CancellationToken ct);
        Task<ViewModels.ScenarioEvent> CreateAsync(ViewModels.ScenarioEvent scenarioEvent, CancellationToken ct);
        Task<ViewModels.ScenarioEvent> UpdateAsync(Guid id, ViewModels.ScenarioEvent scenarioEvent, CancellationToken ct);
        Task<bool> DeleteAsync(Guid id, CancellationToken ct);
        Task<bool> BatchDeleteAsync(Guid[] idList, CancellationToken ct);
    }

    public class ScenarioEventService : IScenarioEventService
    {
        private readonly BlueprintContext _context;
        private readonly IAuthorizationService _authorizationService;
        private readonly ClaimsPrincipal _user;
        private readonly IMapper _mapper;
        private readonly DatabaseOptions _options;
        

        public ScenarioEventService(
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

        public async Task<IEnumerable<ViewModels.ScenarioEvent>> GetByMselAsync(Guid mselId, CancellationToken ct)
        {
            // user must be a Content Developer or a MSEL viewer
            if (!(await _authorizationService.AuthorizeAsync(_user, null, new ContentDeveloperRequirement())).Succeeded &&
                !(await MselViewRequirement.IsMet(_user.GetId(), mselId, _context)))
                throw new ForbiddenException();

            var scenarioEvents = await _context.ScenarioEvents
                .Where(i => i.MselId == mselId)
                .OrderBy(se => se.DeltaSeconds)
                .ToListAsync(ct);
            return _mapper.Map<IEnumerable<ScenarioEvent>>(scenarioEvents);
        }

        public async Task<ViewModels.ScenarioEvent> GetAsync(Guid id, CancellationToken ct)
        {
            var item = await _context.ScenarioEvents
                .Include(se => se.DataValues)
                .SingleAsync(a => a.Id == id, ct);

            if (item == null)
                throw new EntityNotFoundException<ScenarioEventEntity>();

            if (!(await _authorizationService.AuthorizeAsync(_user, null, new ContentDeveloperRequirement())).Succeeded &&
                !(await MselViewRequirement.IsMet(_user.GetId(), item.MselId, _context)))
                throw new ForbiddenException();

            return _mapper.Map<ViewModels.ScenarioEvent>(item);
        }

        public async Task<ViewModels.ScenarioEvent> CreateAsync(ViewModels.ScenarioEvent scenarioEvent, CancellationToken ct)
        {
            // user must be a Content Developer or a MSEL owner
            if (!(await _authorizationService.AuthorizeAsync(_user, null, new ContentDeveloperRequirement())).Succeeded &&
                !(await MselOwnerRequirement.IsMet(_user.GetId(), scenarioEvent.MselId, _context)))
                throw new ForbiddenException();

            // start a transaction, because we may also update DataValues and other scenario events
            await _context.Database.BeginTransactionAsync();
            // create the scenario event
            scenarioEvent.Id = scenarioEvent.Id != Guid.Empty ? scenarioEvent.Id : Guid.NewGuid();
            scenarioEvent.DateCreated = DateTime.UtcNow;
            scenarioEvent.CreatedBy = _user.GetId();
            scenarioEvent.DateModified = null;
            scenarioEvent.ModifiedBy = null;
            var scenarioEventEntity = _mapper.Map<ScenarioEventEntity>(scenarioEvent);
            _context.ScenarioEvents.Add(scenarioEventEntity);
            await _context.SaveChangesAsync(ct);
            // create the associated data values
            var dataFieldIdList = _context.DataFields
                .Where(df => df.MselId == scenarioEvent.MselId)
                .Select(df => df.Id);
            foreach (var dataFieldId in dataFieldIdList)
            {
                var dataValue = scenarioEvent.DataValues
                    .FirstOrDefault(dv => dv.DataFieldId == dataFieldId);
                if (dataValue == null)
                {
                    dataValue = new DataValue();
                    dataValue.DataFieldId = dataFieldId;
                }
                dataValue.Id = Guid.NewGuid();
                dataValue.ScenarioEventId = scenarioEvent.Id;
                dataValue.CreatedBy = scenarioEvent.CreatedBy;
                dataValue.DateCreated = scenarioEvent.DateCreated;
                dataValue.DateModified = null;
                dataValue.ModifiedBy = null;
                var dataValueEntity = _mapper.Map<DataValueEntity>(dataValue);
                _context.DataValues.Add(dataValueEntity);
            }
            await _context.SaveChangesAsync(ct);
            // update the MSEL modified info
            await ServiceUtilities.SetMselModifiedAsync(scenarioEventEntity.MselId, scenarioEventEntity.CreatedBy, scenarioEventEntity.DateCreated, _context, ct);
            // commit the transaction
            await _context.Database.CommitTransactionAsync(ct);

            return  _mapper.Map<ViewModels.ScenarioEvent>(scenarioEventEntity);
        }

        public async Task<ViewModels.ScenarioEvent> UpdateAsync(Guid id, ViewModels.ScenarioEvent scenarioEvent, CancellationToken ct)
        {
            var canUpdateScenarioEvent = (await _authorizationService.AuthorizeAsync(_user, null, new ContentDeveloperRequirement())).Succeeded ||
                (await MselOwnerRequirement.IsMet(_user.GetId(), scenarioEvent.MselId, _context));
            // check minimum permission
            if (!canUpdateScenarioEvent &&
                !(await MselApproverRequirement.IsMet(_user.GetId(), scenarioEvent.MselId, _context)) &&
                !(await MselEditorRequirement.IsMet(_user.GetId(), scenarioEvent.MselId, _context)))
                throw new ForbiddenException($"No update permissions");

            // make sure entity exists
            var scenarioEventToUpdate = await _context.ScenarioEvents.SingleOrDefaultAsync(v => v.Id == id, ct);
            if (scenarioEventToUpdate == null)
                throw new EntityNotFoundException<ScenarioEventEntity>($"ScenarioEvent not found {id}.");

            // start a transaction, because we may also update DataValues and other scenario events
            await _context.Database.BeginTransactionAsync();
            // determine if row indexes need updated for the other scenario events on this MSEL
            // update this scenario event
            scenarioEvent.CreatedBy = scenarioEventToUpdate.CreatedBy;
            scenarioEvent.DateCreated = scenarioEventToUpdate.DateCreated;
            scenarioEvent.ModifiedBy = _user.GetId();
            scenarioEvent.DateModified = DateTime.UtcNow;
            _mapper.Map(scenarioEvent, scenarioEventToUpdate);
            _context.ScenarioEvents.Update(scenarioEventToUpdate);
            await _context.SaveChangesAsync(ct);
            // get the DataField IDs for this MSEL
            var dataFieldIdList = await _context.DataFields
                .Where(df => df.MselId == scenarioEvent.MselId)
                .Select(df => df.Id)
                .ToListAsync(ct);
            var cellMetadata = GetCellMetaDataForRow(scenarioEvent.RowMetadata);
            // update the data values
            foreach (var dataFieldId in dataFieldIdList)
            {
                var dataValueToUpdate = await _context.DataValues
                    .SingleOrDefaultAsync(dv => dv.ScenarioEventId == scenarioEvent.Id && dv.DataFieldId == dataFieldId, ct);
                var dataValue = scenarioEvent.DataValues
                    .SingleOrDefault(dv => dv.ScenarioEventId == scenarioEvent.Id && dv.DataFieldId == dataFieldId);
                if (dataValueToUpdate == null)
                {
                    dataValue.Id = Guid.NewGuid();
                    dataValue.CreatedBy = (Guid)scenarioEvent.ModifiedBy;
                    dataValue.DateCreated = (DateTime)scenarioEvent.DateModified;
                    dataValue.DateModified = dataValue.DateCreated;
                    dataValue.ModifiedBy = dataValue.CreatedBy;
                    dataValue.CellMetadata = cellMetadata;
                    var dataValueEntity = _mapper.Map<DataValueEntity>(dataValue);
                    _context.DataValues.Add(dataValueEntity);
                }
                else if (dataValue == null)
                {
                    if (dataValueToUpdate.CellMetadata != cellMetadata)
                    {
                        // update the DataValue
                        dataValueToUpdate.ModifiedBy = scenarioEventToUpdate.ModifiedBy;
                        dataValueToUpdate.DateModified = scenarioEventToUpdate.DateModified;
                        dataValueToUpdate.CellMetadata = cellMetadata;
                        _context.DataValues.Update(dataValueToUpdate);
                    }
                }
                else if (dataValue.Value != dataValueToUpdate.Value || dataValueToUpdate.CellMetadata != cellMetadata)
                {
                    // update the DataValue
                    dataValueToUpdate.ModifiedBy = scenarioEventToUpdate.ModifiedBy;
                    dataValueToUpdate.DateModified = scenarioEventToUpdate.DateModified;
                    dataValueToUpdate.Value = dataValue.Value;
                    dataValueToUpdate.CellMetadata = cellMetadata;
                    _context.DataValues.Update(dataValueToUpdate);
                }
            }
            await _context.SaveChangesAsync(ct);
            // update the MSEL modified info
            await ServiceUtilities.SetMselModifiedAsync(scenarioEventToUpdate.MselId, scenarioEventToUpdate.ModifiedBy, scenarioEventToUpdate.DateModified, _context, ct);
            // commit the transaction
            await _context.Database.CommitTransactionAsync(ct);

            return _mapper.Map<ViewModels.ScenarioEvent>(scenarioEventToUpdate);
        }

        public async Task<bool> DeleteAsync(Guid id, CancellationToken ct)
        {
            var scenarioEventToDelete = await _context.ScenarioEvents.SingleOrDefaultAsync(v => v.Id == id, ct);

            if (scenarioEventToDelete == null)
                throw new EntityNotFoundException<ScenarioEventEntity>();

            // user must be a Content Developer or a MSEL owner
            if (!(await _authorizationService.AuthorizeAsync(_user, null, new ContentDeveloperRequirement())).Succeeded &&
                !(await MselOwnerRequirement.IsMet(_user.GetId(), scenarioEventToDelete.MselId, _context)))
                throw new ForbiddenException();

            // start a transaction, because we may also update DataValues and other scenario events
            await _context.Database.BeginTransactionAsync();
            _context.ScenarioEvents.Remove(scenarioEventToDelete);
            await _context.SaveChangesAsync(ct);
            // update the MSEL modified info
            await ServiceUtilities.SetMselModifiedAsync(scenarioEventToDelete.MselId, _user.GetId(), DateTime.UtcNow, _context, ct);
            // commit the transaction
            await _context.Database.CommitTransactionAsync(ct);

            return true;
        }

        public async Task<bool> BatchDeleteAsync(Guid[] idList, CancellationToken ct)
        {
            var mselId = Guid.Empty;
            var scenarioEventList = new List<ScenarioEventEntity>();
            foreach (var id in idList)
            {
                var scenarioEventToDelete = await _context.ScenarioEvents.SingleOrDefaultAsync(v => v.Id == id, ct);

                if (scenarioEventToDelete == null)
                    throw new EntityNotFoundException<ScenarioEventEntity>();

                if (mselId == Guid.Empty)
                {
                    mselId = scenarioEventToDelete.MselId;
                }
                else if (mselId != scenarioEventToDelete.MselId)
                {
                    throw new ArgumentException("Scenario events can only be from one MSEL for batch delete!");
                }

                // user must be a Content Developer or a MSEL owner
                if (!(await _authorizationService.AuthorizeAsync(_user, null, new ContentDeveloperRequirement())).Succeeded &&
                    !(await MselOwnerRequirement.IsMet(_user.GetId(), mselId, _context)))
                    throw new ForbiddenException();

                scenarioEventList.Add(scenarioEventToDelete);
            }

            await _context.Database.BeginTransactionAsync();
            foreach (var scenarioEventToDelete in scenarioEventList)
            {
                // start a transaction, because we may also update DataValues and other scenario events
                _context.ScenarioEvents.Remove(scenarioEventToDelete);
            }
            await _context.SaveChangesAsync(ct);
            // update the MSEL modified info
            await ServiceUtilities.SetMselModifiedAsync(mselId, _user.GetId(), DateTime.UtcNow, _context, ct);
            // commit the transaction
            await _context.Database.CommitTransactionAsync(ct);

            return true;
        }

        private string GetCellMetaDataForRow(string rowMetaData)
        {
          if (rowMetaData != null && rowMetaData.Count() > 0)
          {
            var cellMetaData = "";
            var rowParts = rowMetaData.Split(",");
            if (rowParts.Count() == 4)
            {
              for (var i = 1; i < 4; i++)
              {
                  cellMetaData = cellMetaData + int.Parse(rowParts[i]).ToString("X");
              }
            }
            else
            {
              cellMetaData = "FFFFFF";
            }
            return cellMetaData + ",0.7,normal,0";
          }
          return "FFFFFF,0,normal,0";
        }

    }

 }

