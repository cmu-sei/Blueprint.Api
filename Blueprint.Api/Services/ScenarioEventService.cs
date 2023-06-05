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
using Blueprint.Api.Data.Enumerations;
using Blueprint.Api.Data.Models;
using Blueprint.Api.Infrastructure.Authorization;
using Blueprint.Api.Infrastructure.Exceptions;
using Blueprint.Api.Infrastructure.Extensions;
using Blueprint.Api.Infrastructure.Options;
using Blueprint.Api.Infrastructure.QueryParameters;
using Blueprint.Api.ViewModels;

namespace Blueprint.Api.Services
{
    public interface IScenarioEventService
    {
        Task<IEnumerable<ViewModels.ScenarioEvent>> GetByMselAsync(Guid mselId, CancellationToken ct);
        Task<ViewModels.ScenarioEvent> GetAsync(Guid id, CancellationToken ct);
        Task<ViewModels.ScenarioEvent> CreateAsync(ViewModels.ScenarioEvent scenarioEvent, CancellationToken ct);
        Task<ViewModels.ScenarioEvent> UpdateAsync(Guid id, ViewModels.ScenarioEvent scenarioEvent, CancellationToken ct);
        Task<IEnumerable<ViewModels.ScenarioEvent>> DeleteAsync(Guid id, CancellationToken ct);
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

            var scenarioEvents = _context.ScenarioEvents
                .Where(i => i.MselId == mselId);
            // order the results
            scenarioEvents = scenarioEvents.OrderBy(n => n.RowIndex);

            return _mapper.Map<IEnumerable<ScenarioEvent>>(await scenarioEvents.ToListAsync());
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
            await RenumberRowIndexes(scenarioEventEntity, false, ct);
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
            var updateRowIndexes = canUpdateScenarioEvent && (scenarioEventToUpdate.RowIndex != scenarioEvent.RowIndex);
            var newIndexIsGreater = scenarioEvent.RowIndex > scenarioEventToUpdate.RowIndex;
            // update this scenario event
            scenarioEvent.CreatedBy = scenarioEventToUpdate.CreatedBy;
            scenarioEvent.DateCreated = scenarioEventToUpdate.DateCreated;
            scenarioEvent.ModifiedBy = _user.GetId();
            scenarioEvent.DateModified = DateTime.UtcNow;
            _mapper.Map(scenarioEvent, scenarioEventToUpdate);
            _context.ScenarioEvents.Update(scenarioEventToUpdate);
            await _context.SaveChangesAsync(ct);
            if (updateRowIndexes)
            {
                await RenumberRowIndexes(scenarioEventToUpdate, newIndexIsGreater, ct);
            }
            // get the DataField IDs for this MSEL
            var dataFieldIdList = _context.DataFields
                .Where(df => df.MselId == scenarioEvent.MselId)
                .Select(df => df.Id);
            // update the data values
            foreach (var dataValue in scenarioEvent.DataValues)
            {
                var dataValueToUpdate = await _context.DataValues.SingleOrDefaultAsync(dv => dv.Id == dataValue.Id, ct);
                if (dataValueToUpdate == null)
                {
                    if (dataValue.ScenarioEventId != scenarioEvent.Id || !dataFieldIdList.Contains(dataValue.DataFieldId))
                    {
                        throw new InvalidOperationException("The submitted DataValue had a mismatched ScenarioEventId or DataFieldId for the ScenarioEvent.");
                    }
                    dataValue.Id = Guid.NewGuid();
                    dataValue.CreatedBy = (Guid)scenarioEvent.ModifiedBy;
                    dataValue.DateCreated = (DateTime)scenarioEvent.DateModified;
                    dataValue.DateModified = dataValue.DateCreated;
                    dataValue.ModifiedBy = dataValue.CreatedBy;
                    var dataValueEntity = _mapper.Map<DataValueEntity>(dataValue);
                    _context.DataValues.Add(dataValueEntity);
                }
                else if (dataValue.Value != dataValueToUpdate.Value)
                {
                    // update the DataValue
                    dataValue.CreatedBy = dataValueToUpdate.CreatedBy;
                    dataValue.DateCreated = dataValueToUpdate.DateCreated;
                    dataValue.ModifiedBy = scenarioEventToUpdate.ModifiedBy;
                    dataValue.DateModified = scenarioEventToUpdate.DateModified;
                    _mapper.Map(dataValue, dataValueToUpdate);
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

        public async Task<IEnumerable<ViewModels.ScenarioEvent>> DeleteAsync(Guid id, CancellationToken ct)
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
            // reorder
            await RenumberRowIndexes(scenarioEventToDelete, false, ct);
            // update the MSEL modified info
            await ServiceUtilities.SetMselModifiedAsync(scenarioEventToDelete.MselId, _user.GetId(), DateTime.UtcNow, _context, ct);
            var scenarioEventEnitities = await _context.ScenarioEvents
                    .Where(i => i.MselId == scenarioEventToDelete.MselId)
                    .Include(se => se.DataValues)
                    .ToListAsync(ct);
            // commit the transaction
            await _context.Database.CommitTransactionAsync(ct);

            return _mapper.Map<IEnumerable<ViewModels.ScenarioEvent>>(scenarioEventEnitities);
        }

        private async Task<List<ScenarioEventEntity>> RenumberRowIndexes(ScenarioEventEntity scenarioEventEntity, bool newIndexIsGreater, CancellationToken ct)
        {
            var scenarioEvents = await _context.ScenarioEvents
                .Where(se => se.MselId == scenarioEventEntity.MselId)
                    .Include(se => se.DataValues)
                .OrderBy(se => se.RowIndex)
                .ToListAsync(ct);
            var isHere = scenarioEvents.Any(df => df.Id == scenarioEventEntity.Id);
            for (var i = 0; i < scenarioEvents.Count; i++)
            {
                if (scenarioEvents[i].Id != scenarioEventEntity.Id)
                {
                    // handle the item that has the same row index as the newly assigned row index
                    if (isHere && scenarioEvents[i].RowIndex == scenarioEventEntity.RowIndex)
                    {
                        if (newIndexIsGreater)
                        {
                            scenarioEvents[i].RowIndex = scenarioEventEntity.RowIndex - 1;
                        }
                        else
                        {
                            scenarioEvents[i].RowIndex = scenarioEventEntity.RowIndex + 1;
                        }
                    }
                    else
                    {
                        scenarioEvents[i].RowIndex = i + 1;
                    }
                }
                else if (scenarioEventEntity.RowIndex > scenarioEvents.Count)
                {
                    scenarioEventEntity.RowIndex = scenarioEvents.Count;
                }
            }
            await _context.SaveChangesAsync(ct);

            return scenarioEvents;
        }

    }

 }

