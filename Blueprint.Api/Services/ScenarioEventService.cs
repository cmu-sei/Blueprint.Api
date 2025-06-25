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
using Blueprint.Api.ViewModels;
using Blueprint.Api.Infrastructure.JsonConverters;
using NuGet.Packaging.Licenses;
using System.Data;

namespace Blueprint.Api.Services
{
    public interface IScenarioEventService
    {
        Task<IEnumerable<ViewModels.ScenarioEvent>> GetByMselAsync(Guid mselId, CancellationToken ct);
        Task<ViewModels.ScenarioEvent> GetAsync(Guid id, CancellationToken ct);
        Task<IEnumerable<ViewModels.ScenarioEvent>> CreateAsync(ViewModels.ScenarioEvent scenarioEvent, CancellationToken ct);
        Task<IEnumerable<ViewModels.ScenarioEvent>> CreateFromInjectsAsync(CreateFromInjectsForm createFromInjectsForm, CancellationToken ct);
        Task<IEnumerable<ViewModels.ScenarioEvent>> CopyScenarioEventsToMselAsync(Guid mselId, List<Guid> scenarioEventIdList, CancellationToken ct);
        Task<IEnumerable<ViewModels.ScenarioEvent>> UpdateAsync(Guid id, ViewModels.ScenarioEvent scenarioEvent, CancellationToken ct);
        Task<bool> DeleteAsync(Guid id, CancellationToken ct);
        Task<bool> BatchDeleteAsync(Guid[] idList, CancellationToken ct);
        Task<Dictionary<Guid, int[]>> GetMovesAndInjects(Guid mselId, CancellationToken ct);
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
                .Include(e => e.SteamfitterTask)
                .OrderBy(se => se.DeltaSeconds)
                .ThenBy(se => se.GroupOrder)
                .ToListAsync(ct);
            return _mapper.Map<IEnumerable<ScenarioEvent>>(scenarioEvents);
        }

        public async Task<ViewModels.ScenarioEvent> GetAsync(Guid id, CancellationToken ct)
        {
            var item = await _context.ScenarioEvents
                .Include(se => se.DataValues)
                .Include(se => se.SteamfitterTask)
                .SingleAsync(a => a.Id == id, ct);

            if (item == null)
                throw new EntityNotFoundException<ScenarioEventEntity>();

            if (!(await _authorizationService.AuthorizeAsync(_user, null, new ContentDeveloperRequirement())).Succeeded &&
                !(await MselViewRequirement.IsMet(_user.GetId(), item.MselId, _context)))
                throw new ForbiddenException();

            return _mapper.Map<ViewModels.ScenarioEvent>(item);
        }

        public async Task<IEnumerable<ViewModels.ScenarioEvent>> CreateAsync(ViewModels.ScenarioEvent scenarioEvent, CancellationToken ct)
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
            var scenarioEventEnitities = new List<ScenarioEventEntity>
            {
                scenarioEventEntity
            };
            // handle any reordering that may be necessary
            scenarioEventEnitities.AddRange(await ReorderScenarioEvents(scenarioEventEntity, true, ct));
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

            return  _mapper.Map<IEnumerable<ViewModels.ScenarioEvent>>(scenarioEventEnitities);
        }

        public async Task<IEnumerable<ViewModels.ScenarioEvent>> CreateFromInjectsAsync(CreateFromInjectsForm createFromInjectsForm, CancellationToken ct)
        {
            var userId = _user.GetId();
            // user must be a Content Developer or a MSEL owner
            if (!(await _authorizationService.AuthorizeAsync(_user, null, new ContentDeveloperRequirement())).Succeeded &&
                !(await MselOwnerRequirement.IsMet(userId, createFromInjectsForm.MselId, _context)))
                throw new ForbiddenException();

            // get the MSEL
            var msel = await _context.Msels
                .Include(m => m.DataFields)
                .SingleOrDefaultAsync(x => x.Id == createFromInjectsForm.MselId);
            if (msel == null)
                throw new EntityNotFoundException<MselEntity>("Msel " + createFromInjectsForm.MselId.ToString() + " was not found.");

            // get the inject type
            var injectType = await _context.InjectTypes
                .Include(m => m.DataFields)
                .ThenInclude(n => n.DataOptions)
                .AsSplitQuery()
                .SingleOrDefaultAsync(m => m.Id == createFromInjectsForm.InjectTypeId);
            if (injectType == null)
                throw new EntityNotFoundException<InjectTypeEntity>("Inject Type " + createFromInjectsForm.InjectTypeId.ToString() + " was not found.");

            // start a transaction, because we may also update DataValues and other scenario events
            await _context.Database.BeginTransactionAsync();
            var dateCreated = DateTime.UtcNow;
            // create a data field dictionary and create any missing data fields
            var dataFieldDictionary = new Dictionary<Guid, Guid>();
            var displayOrder = msel.DataFields.Count > 0
                ? msel.DataFields.Max(m => m.DisplayOrder)
                : 0;
            // add the name data field
            var nameDataFieldId = Guid.Empty;
            var mselDataFieldEntity = msel.DataFields.SingleOrDefault(m => m.Name == "Name" && m.DataType == DataFieldType.String);
            if (mselDataFieldEntity != null)
            {
                nameDataFieldId = mselDataFieldEntity.Id;
            }
            else
            {
                nameDataFieldId = Guid.NewGuid();
                mselDataFieldEntity = new DataFieldEntity()
                {
                    Id = nameDataFieldId,
                    MselId = msel.Id,
                    Name = "Name",
                    DataType = DataFieldType.String,
                    DisplayOrder = ++displayOrder,
                    DateCreated = dateCreated,
                    CreatedBy = userId,
                    IsChosenFromList = false,
                    OnScenarioEventList = true,
                    OnExerciseView = true,
                };
                _context.DataFields.Add(mselDataFieldEntity);
            }
            // add the desciption data field
            var descriptionDataFieldId = Guid.Empty;
            mselDataFieldEntity = msel.DataFields.SingleOrDefault(m => m.Name == "Description" && m.DataType == DataFieldType.String);
            if (mselDataFieldEntity != null)
            {
                descriptionDataFieldId = mselDataFieldEntity.Id;
            }
            else
            {
                descriptionDataFieldId = Guid.NewGuid();
                mselDataFieldEntity = new DataFieldEntity()
                {
                    Id = descriptionDataFieldId,
                    MselId = msel.Id,
                    Name = "Description",
                    DataType = DataFieldType.String,
                    DisplayOrder = ++displayOrder,
                    DateCreated = dateCreated,
                    CreatedBy = userId,
                    IsChosenFromList = false,
                    OnScenarioEventList = true,
                    OnExerciseView = true,
                };
                _context.DataFields.Add(mselDataFieldEntity);
            }
            foreach (var injectDataField in injectType.DataFields)
            {
                var showIt = injectDataField.DataType != DataFieldType.Html;
                mselDataFieldEntity = msel.DataFields.SingleOrDefault(m => m.Name == injectDataField.Name && m.DataType == injectDataField.DataType);
                if (mselDataFieldEntity == null)
                {
                    mselDataFieldEntity = new DataFieldEntity()
                    {
                        Id = Guid.NewGuid(),
                        MselId = msel.Id,
                        Name = injectDataField.Name,
                        DataType = injectDataField.DataType,
                        DisplayOrder = ++displayOrder,
                        DateCreated = dateCreated,
                        CreatedBy = userId,
                        IsChosenFromList = injectDataField.IsChosenFromList,
                        OnScenarioEventList = showIt,
                        OnExerciseView = showIt,
                    };
                    _context.DataFields.Add(mselDataFieldEntity);
                    if (injectDataField.IsChosenFromList)
                    {
                        foreach (var injectDataOption in injectDataField.DataOptions)
                        {
                            var mselDataOption = new DataOptionEntity()
                            {
                                Id = Guid.NewGuid(),
                                DataFieldId = mselDataFieldEntity.Id,
                                OptionName = injectDataOption.OptionName,
                                OptionValue = injectDataOption.OptionValue,
                                DisplayOrder = injectDataOption.DisplayOrder,
                            };
                            _context.DataOptions.Add(mselDataOption);
                        }
                    }
                }
                dataFieldDictionary[injectDataField.Id] = mselDataFieldEntity.Id;
            }
            await _context.SaveChangesAsync(ct);
            // create a scenario event for each inject
            foreach (var injectId in createFromInjectsForm.InjectIdList)
            {
                // get the inject
                var inject = await _context.Injects
                    .Include(m => m.DataValues)
                    .SingleOrDefaultAsync(x => x.Id == injectId);
                if (inject == null)
                    throw new EntityNotFoundException<InjectEntity>("Inject " + injectId.ToString() + " was not found.");
                // create the new scenario event
                var scenarioEventEntity = new ScenarioEventEntity()
                {
                    Id = Guid.NewGuid(),
                    MselId = msel.Id,
                    InjectId = injectId,
                    ScenarioEventType = EventType.Inject,
                    DeltaSeconds = 0,
                };
                // create the data value for the inject name
                var scenarioEventDataValueEntity = new DataValueEntity()
                {
                    Id = Guid.NewGuid(),
                    DataFieldId = nameDataFieldId,
                    ScenarioEventId = scenarioEventEntity.Id,
                    Value = inject.Name,
                    CreatedBy = userId,
                    DateCreated = dateCreated
                };
                scenarioEventEntity.DataValues.Add(scenarioEventDataValueEntity);
                // create the data value for the inject description
                scenarioEventDataValueEntity = new DataValueEntity()
                {
                    Id = Guid.NewGuid(),
                    DataFieldId = descriptionDataFieldId,
                    ScenarioEventId = scenarioEventEntity.Id,
                    Value = inject.Description,
                    CreatedBy = userId,
                    DateCreated = dateCreated
                };
                scenarioEventEntity.DataValues.Add(scenarioEventDataValueEntity);
                // create the data values for each inject data value
                foreach (var injectDataValueEntity in inject.DataValues)
                {
                    scenarioEventDataValueEntity = new DataValueEntity()
                    {
                        Id = Guid.NewGuid(),
                        DataFieldId = dataFieldDictionary[injectDataValueEntity.DataFieldId],
                        ScenarioEventId = scenarioEventEntity.Id,
                        Value = injectDataValueEntity.Value,
                        CreatedBy = userId,
                        DateCreated = dateCreated
                    };
                    scenarioEventEntity.DataValues.Add(scenarioEventDataValueEntity);
                }
                _context.ScenarioEvents.Add(scenarioEventEntity);
                await _context.SaveChangesAsync(ct);
                // handle any reordering that may be necessary
                await ReorderScenarioEvents(scenarioEventEntity, true, ct);
                await _context.SaveChangesAsync(ct);
            }
            // update the MSEL modified info
            msel.ModifiedBy = userId;
            msel.DateModified = dateCreated;
            await _context.SaveChangesAsync(ct);
            // commit the transaction
            await _context.Database.CommitTransactionAsync(ct);
            // return all msel scenarioEvents
            var scenarioEventEnitities = await _context.ScenarioEvents
                .Include(m => m.DataValues)
                .Include(se => se.SteamfitterTask)
                .Where(m => m.MselId == msel.Id)
                .ToListAsync(ct);
            return _mapper.Map<IEnumerable<ViewModels.ScenarioEvent>>(scenarioEventEnitities);
        }

        public async Task<IEnumerable<ViewModels.ScenarioEvent>> CopyScenarioEventsToMselAsync(Guid mselId, List<Guid> scenarioEventIdList, CancellationToken ct)
        {
            // make sure destination MSEL exists
            var destinationMsel = await _context.Msels
                .Include(m => m.DataFields)
                .SingleOrDefaultAsync(v => v.Id == mselId, ct);
            if (destinationMsel == null)
                throw new EntityNotFoundException<ScenarioEventEntity>($"MSEL not found {mselId}.");

            // make sure the source MSEL and all scenarioEvents exist
            var sourceMsel = await _context.ScenarioEvents
                .Where(m => m.Id == scenarioEventIdList[0])
                .Include(m => m.Msel)
                .ThenInclude(m => m.DataFields)
                .ThenInclude(f => f.DataOptions)
                .Include(m => m.Msel)
                .ThenInclude(m => m.ScenarioEvents)
                .ThenInclude(s => s.DataValues)
                .Select(m => m.Msel)
                .AsSplitQuery()
                .AsNoTracking()
                .SingleOrDefaultAsync(ct);

            // user must be a Content Developer or a MSEL owner for both source and destination MSELs
            if (!(await _authorizationService.AuthorizeAsync(_user, null, new ContentDeveloperRequirement())).Succeeded &&
                !(await MselOwnerRequirement.IsMet(_user.GetId(), mselId, _context) &&
                  await MselOwnerRequirement.IsMet(_user.GetId(), sourceMsel.Id, _context)))
                throw new ForbiddenException();

            // get the sourceScenarioEvents
            var sourceScenarioEvents = sourceMsel.ScenarioEvents.Where(s => scenarioEventIdList.Contains(s.Id));
            if (sourceMsel == null || sourceScenarioEvents.Count() != scenarioEventIdList.Count())
                throw new DataException("The list of Scenario Event IDs was invalid.");

            // determine which data fields are used by these scenario events
            var neededDataFields = new List<Guid>();
            foreach (var scenarioEvent in sourceScenarioEvents)
            {
                foreach (var dataValue in scenarioEvent.DataValues)
                {
                    if (dataValue.Value != null && !neededDataFields.Contains(dataValue.DataFieldId))
                    {
                        neededDataFields.Add(dataValue.DataFieldId);
                    }
                }
            }
            // set some initial values
            var userId = _user.GetId();
            var dateCreated = DateTime.UtcNow;
            var dataFieldDictionary = new Dictionary<Guid, Guid>();
            var displayOrder = destinationMsel.DataFields.Count > 0
                ? destinationMsel.DataFields.Max(m => m.DisplayOrder)
                : 0;

            // start a transaction
            await _context.Database.BeginTransactionAsync();

            // get the data field map (create new data fields as necessary)
            foreach (var sourceDataField in sourceMsel.DataFields.Where(m => neededDataFields.Contains(m.Id)))
            {
                var destinationDataField = destinationMsel.DataFields.SingleOrDefault(m => m.Name == sourceDataField.Name && m.DataType == sourceDataField.DataType);
                if (destinationDataField == null)
                {
                    destinationDataField = new DataFieldEntity()
                    {
                        Id = Guid.NewGuid(),
                        MselId = destinationMsel.Id,
                        Name = sourceDataField.Name,
                        DataType = sourceDataField.DataType,
                        DisplayOrder = ++displayOrder,
                        DateCreated = dateCreated,
                        CreatedBy = userId,
                        IsChosenFromList = sourceDataField.IsChosenFromList,
                        OnScenarioEventList = sourceDataField.OnScenarioEventList,
                        OnExerciseView = sourceDataField.OnExerciseView,
                    };
                    _context.DataFields.Add(destinationDataField);
                    if (sourceDataField.IsChosenFromList)
                    {
                        foreach (var sourceDataOption in sourceDataField.DataOptions)
                        {
                            var destinationDataOption = new DataOptionEntity()
                            {
                                Id = Guid.NewGuid(),
                                DataFieldId = destinationDataField.Id,
                                OptionName = sourceDataOption.OptionName,
                                OptionValue = sourceDataOption.OptionValue,
                                DisplayOrder = sourceDataOption.DisplayOrder,
                            };
                            _context.DataOptions.Add(destinationDataOption);
                        }
                    }
                }
                dataFieldDictionary[destinationDataField.Id] = sourceDataField.Id;
            }
            await _context.SaveChangesAsync(ct);
            // get the new list of data field IDs
            var dataFieldIdList = _context.DataFields
                .Where(df => df.MselId == destinationMsel.Id)
                .Select(df => df.Id);
            // Loop through the source scenario events
            foreach (var sourceScenarioEvent in sourceScenarioEvents)
            {
                var destinationScenarioEvent = new ScenarioEventEntity()
                {
                    Id = Guid.NewGuid(),
                    MselId = destinationMsel.Id,
                    GroupOrder = 0,
                    IsHidden = false,
                    DeltaSeconds = sourceScenarioEvent.DeltaSeconds,
                    ScenarioEventType = sourceScenarioEvent.ScenarioEventType,
                    InjectId = sourceScenarioEvent.InjectId,
                    DateCreated = dateCreated,
                    CreatedBy = userId,
                };
                _context.ScenarioEvents.Add(destinationScenarioEvent);
                // create blank data values
                foreach (var dataFieldId in dataFieldIdList)
                {
                    var dataValue = new DataValueEntity();
                    dataValue.DataFieldId = dataFieldId;
                    dataValue.Id = Guid.NewGuid();
                    dataValue.ScenarioEventId = destinationScenarioEvent.Id;
                    dataValue.CreatedBy = userId;
                    dataValue.DateCreated = dateCreated;
                    dataValue.DateModified = null;
                    dataValue.ModifiedBy = null;
                    if (dataFieldDictionary.Keys.Contains(dataFieldId))
                    {
                        dataValue.Value = sourceScenarioEvent.DataValues.Single(m => m.DataFieldId == dataFieldDictionary[dataFieldId]).Value;
                    }
                    _context.DataValues.Add(dataValue);
                }
            }
            await _context.SaveChangesAsync(ct);
            // commit the transaction
            await _context.Database.CommitTransactionAsync(ct);
            // return all msel scenarioEvents
            var scenarioEventEnitities = await _context.ScenarioEvents
                .Include(m => m.DataValues)
                .Include(se => se.SteamfitterTask)
                .Where(m => m.MselId == destinationMsel.Id)
                .ToListAsync(ct);
            return _mapper.Map<IEnumerable<ViewModels.ScenarioEvent>>(scenarioEventEnitities);
        }

        public async Task<IEnumerable<ViewModels.ScenarioEvent>> UpdateAsync(Guid id, ViewModels.ScenarioEvent scenarioEvent, CancellationToken ct)
        {
            var canUpdateScenarioEvent = (await _authorizationService.AuthorizeAsync(_user, null, new ContentDeveloperRequirement())).Succeeded ||
                (await MselOwnerRequirement.IsMet(_user.GetId(), scenarioEvent.MselId, _context));
            // check minimum permission
            if (!canUpdateScenarioEvent &&
                !(await MselApproverRequirement.IsMet(_user.GetId(), scenarioEvent.MselId, _context)) &&
                !(await MselEditorRequirement.IsMet(_user.GetId(), scenarioEvent.MselId, _context)))
                throw new ForbiddenException($"No update permissions");

            // make sure entity exists
            var scenarioEventToUpdate = await _context.ScenarioEvents.Include(m => m.SteamfitterTask).SingleOrDefaultAsync(v => v.Id == id, ct);
            if (scenarioEventToUpdate == null)
                throw new EntityNotFoundException<ScenarioEventEntity>($"ScenarioEvent not found {id}.");

            // start a transaction, because we may also update DataValues and other scenario events
            await _context.Database.BeginTransactionAsync();
            // // process the steamfitter task, if necessary
            // if (scenarioEventToUpdate.SteamfitterTask != null)
            // {
            //     _context.SteamfitterTasks.Remove(scenarioEventToUpdate.SteamfitterTask);
            //     scenarioEventToUpdate.SteamfitterTask = null;
            //     scenarioEventToUpdate.SteamfitterTaskId = null;
            // }
            // determine if groupOrder needs to be updated for any other scenario events on this MSEL
            var updateOrdering = scenarioEventToUpdate.DeltaSeconds != scenarioEvent.DeltaSeconds ||
                scenarioEventToUpdate.GroupOrder != scenarioEvent.GroupOrder;
            // update this scenario event
            scenarioEvent.CreatedBy = scenarioEventToUpdate.CreatedBy;
            scenarioEvent.DateCreated = scenarioEventToUpdate.DateCreated;
            scenarioEvent.ModifiedBy = _user.GetId();
            scenarioEvent.DateModified = DateTime.UtcNow;
            if (scenarioEvent.SteamfitterTask != null)
            {
                scenarioEvent.SteamfitterTask.ScenarioEvent = null;
                scenarioEvent.SteamfitterTask.ScenarioEventId = scenarioEvent.Id;
            }
            _mapper.Map(scenarioEvent, scenarioEventToUpdate);
            _context.ScenarioEvents.Update(scenarioEventToUpdate);
            var scenarioEventEnitities = new List<ScenarioEventEntity>
            {
                scenarioEventToUpdate
            };
            if (updateOrdering)
            {
                scenarioEventEnitities.AddRange(await ReorderScenarioEvents(scenarioEventToUpdate, false, ct));
            }
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
                if (dataValueToUpdate != null || dataValue != null)
                {
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
            }
            await _context.SaveChangesAsync(ct);
            // update the MSEL modified info
            await ServiceUtilities.SetMselModifiedAsync(scenarioEventToUpdate.MselId, scenarioEventToUpdate.ModifiedBy, scenarioEventToUpdate.DateModified, _context, ct);
            // commit the transaction
            await _context.Database.CommitTransactionAsync(ct);

            return _mapper.Map<IEnumerable<ViewModels.ScenarioEvent>>(scenarioEventEnitities);
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

        public async Task<Dictionary<Guid, int[]>> GetMovesAndInjects(Guid mselId, CancellationToken ct)
        {
            var movesAndInjects= new Dictionary<Guid, int[]>();
            // order scenario events and moves by DeltaSeconds
            var scenarioEvents = await _context.ScenarioEvents.Where(se => se.MselId == mselId).OrderBy(se => se.DeltaSeconds).ToArrayAsync(ct);
            var moves = await _context.Moves.Where(se => se.MselId == mselId).OrderBy(m => m.DeltaSeconds).ToArrayAsync(ct);
            var m = 0;  // move index
            var inject = 0;  // inject value
            var deltaSeconds = scenarioEvents.Length > 0 ? scenarioEvents[0].DeltaSeconds : 0;  // value of the previous scenario event.  Used to determine the inject number.
            // loop through the chronological scenario events
            for (int s = 0; s < scenarioEvents.Length; s++)
            {
                // if not on the last move, check this scenario event time to determine if it is in the current move
                if (moves.Length == 0 || m == +moves.Length - 1 || +scenarioEvents[s].DeltaSeconds < +moves[m + 1].DeltaSeconds)
                {
                    if (scenarioEvents[s].DeltaSeconds != deltaSeconds)
                    {
                        inject++;
                    }
                }
                else
                {
                    // the move must be incremented
                    while (m < +moves.Length - 1 && +scenarioEvents[s].DeltaSeconds >= +moves[m + 1].DeltaSeconds)
                    {
                        m++;  // increment the move
                    }
                    inject = 0;  // start with inject 0 for this new move
                }
                var moveNumber = moves.Length > m ? moves[m].MoveNumber : 0;
                deltaSeconds = scenarioEvents[s].DeltaSeconds;
                movesAndInjects.Add(scenarioEvents[s].Id, new int[] {moves[m].MoveNumber, inject});
            }

            return movesAndInjects;
        }

        private async Task<List<ScenarioEventEntity>> ReorderScenarioEvents(ScenarioEventEntity scenarioEventEntity, Boolean isNewEvent, CancellationToken ct)
        {
            var scenarioEvents = await _context.ScenarioEvents
                .Where(se => se.MselId == scenarioEventEntity.MselId &&
                    se.DeltaSeconds == scenarioEventEntity.DeltaSeconds &&
                    se.GroupOrder >= scenarioEventEntity.GroupOrder &&
                    se.Id != scenarioEventEntity.Id)
                    .Include(se => se.DataValues)
                    .Include(se => se.SteamfitterTask)
                .OrderBy(se => se.GroupOrder)
                .ToListAsync(ct);
            // for newly created events with a zero GroupOrder created after/simultaneously with all of the others, assume it should be appended to the end
            var reorder = scenarioEvents.Any(se => se.GroupOrder == scenarioEventEntity.GroupOrder) &&
                (!isNewEvent || !(0 == scenarioEventEntity.GroupOrder && scenarioEvents.All(se => se.DateCreated <= scenarioEventEntity.DateCreated)));
            if (reorder)
            {
                for (var i = scenarioEvents.Count; i > 0; i--)
                {
                    scenarioEvents[i-1].GroupOrder = scenarioEventEntity.GroupOrder + i;
                }
            }
            else
            {
                scenarioEventEntity.GroupOrder = 0 == scenarioEvents.Count ? 0 : scenarioEvents.Last().GroupOrder + 1;
            }

            return reorder ? scenarioEvents : new List<ScenarioEventEntity>();
        }
    }

    public class CreateFromInjectsForm
    {
        public List<Guid> InjectIdList { get; set; }
        public Guid InjectTypeId { get; set; }
        public Guid MselId { get; set; }
        public bool AddDataFields { get; set; }
    }

}
