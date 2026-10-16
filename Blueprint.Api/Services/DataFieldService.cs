// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using AutoMapper;
using Microsoft.EntityFrameworkCore;
using Blueprint.Api.Data;
using Blueprint.Api.Data.Models;
using Blueprint.Api.Infrastructure.Authorization;
using Blueprint.Api.Infrastructure.Exceptions;
using Blueprint.Api.Infrastructure.Extensions;
using Blueprint.Api.ViewModels;

namespace Blueprint.Api.Services
{
    public interface IDataFieldService
    {
        Task<IEnumerable<ViewModels.DataField>> GetTemplatesAsync(CancellationToken ct);
        Task<IEnumerable<ViewModels.DataField>> GetByMselAsync(Guid mselId, bool hasSystemPermission, CancellationToken ct);
        Task<IEnumerable<ViewModels.DataField>> GetByInjectTypeAsync(Guid injectTypeId, CancellationToken ct);
        Task<ViewModels.DataField> GetAsync(Guid id, bool hasSystemPermission, CancellationToken ct);
        Task<ViewModels.DataField> CreateAsync(ViewModels.DataFieldCreate dataField, bool hasMselPermission, bool hasDataFieldPermission, CancellationToken ct);
        Task<ViewModels.DataField> UpdateAsync(Guid id, ViewModels.DataFieldEdit dataField, bool hasMselPermission, bool hasDataFieldPermission, CancellationToken ct);
        Task<Guid> DeleteAsync(Guid id, bool hasMselPermission, bool hasDataFieldPermission, CancellationToken ct);
        Task<IEnumerable<ViewModels.DataField>> UploadJsonAsync(FileForm form, CancellationToken ct);
        Task<Tuple<MemoryStream, string>> DownloadJsonAsync(IEnumerable<Guid> ids, CancellationToken ct);
    }

    public class DataFieldService : IDataFieldService
    {
        private readonly BlueprintContext _context;
        private readonly ClaimsPrincipal _user;
        private readonly IMapper _mapper;

        public DataFieldService(
            BlueprintContext context,
            IPrincipal user,
            IMapper mapper)
        {
            _context = context;
            _user = user as ClaimsPrincipal;
            _mapper = mapper;
        }

        public async Task<IEnumerable<ViewModels.DataField>> GetTemplatesAsync(CancellationToken ct)
        {
            var dataFieldEntities = await _context.DataFields
                .Where(dataField => dataField.IsTemplate && dataField.MselId == null)
                .Include(dataField => dataField.DataOptions)
                .ToListAsync(ct);

            return _mapper.Map<IEnumerable<DataField>>(dataFieldEntities).ToList();;
        }

        public async Task<IEnumerable<ViewModels.DataField>> GetByMselAsync(Guid mselId, bool hasSystemPermission, CancellationToken ct)
        {
            if (!hasSystemPermission && !(await MselViewRequirement.IsMet(_user.GetId(), mselId, _context)))
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
            var dataFieldEntities = await _context.DataFields
                .Where(dataField => dataField.InjectTypeId == injectTypeId)
                .Include(df => df.DataOptions)
                .ToListAsync(ct);

            return _mapper.Map<IEnumerable<DataField>>(dataFieldEntities).ToList();;
        }

        public async Task<ViewModels.DataField> GetAsync(Guid id, bool hasSystemPermission, CancellationToken ct)
        {
            var item = await _context.DataFields
                .Include(dataField => dataField.DataOptions)
                .SingleAsync(dataField => dataField.Id == id, ct);

            if (item == null)
                throw new EntityNotFoundException<DataValueEntity>("DataValue not found: " + id);

            // Templates (null MselId) can be viewed by anyone
            if (item.MselId.HasValue)
            {
                if (!hasSystemPermission && !await MselViewRequirement.IsMet(_user.GetId(), item.MselId, _context))
                    throw new ForbiddenException();
            }

            return _mapper.Map<DataField>(item);
        }

        public async Task<ViewModels.DataField> CreateAsync(ViewModels.DataFieldCreate dataField, bool hasMselPermission, bool hasDataFieldPermission, CancellationToken ct)
        {
            var userId = _user.GetId();
            // a data field belongs to at most one of a MSEL, an inject type and the template list
            var ownerCount = (dataField.MselId.HasValue ? 1 : 0) + (dataField.InjectTypeId.HasValue ? 1 : 0) + (dataField.IsTemplate ? 1 : 0);
            if (ownerCount > 1)
                throw new ConflictException("A data field can belong to only one of a MSEL, an inject type or the template list.");

            if (dataField.MselId.HasValue)
            {
                if (!await _context.Msels.AnyAsync(m => m.Id == dataField.MselId, ct))
                    throw new EntityNotFoundException<Msel>();

                if (!hasMselPermission &&
                    !await MselOwnerRequirement.IsMet(userId, dataField.MselId, _context) &&
                    !await MselEditorRequirement.IsMet(userId, dataField.MselId, _context))
                    throw new ForbiddenException();
            }
            else
            {
                if (!hasDataFieldPermission)
                    throw new ForbiddenException();

                if (dataField.InjectTypeId.HasValue && !await _context.InjectTypes.AnyAsync(it => it.Id == dataField.InjectTypeId, ct))
                    throw new EntityNotFoundException<InjectType>();
            }

            // start a transaction
            await _context.Database.BeginTransactionAsync();
            var dataFieldEntity = _mapper.Map<DataFieldEntity>(dataField);
            dataFieldEntity.Id = dataField.Id != Guid.Empty ? dataField.Id : Guid.NewGuid();
            dataFieldEntity.CreatedBy = userId;
            _context.DataFields.Add(dataFieldEntity);
            // add the DataOptions
            foreach (var dataOption in dataField.DataOptions)
            {
                var newDataOption = _mapper.Map<DataOptionEntity>(dataOption);
                newDataOption.Id = Guid.NewGuid();
                newDataOption.DataFieldId = dataFieldEntity.Id;
                newDataOption.CreatedBy = userId;
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
                await ServiceUtilities.SetMselModifiedAsync(dataFieldEntity.MselId, userId, DateTime.UtcNow, _context, ct);
            }
            else if (dataFieldEntity.InjectTypeId != null)
            {
                // reorder the data fields
                await Reorder(dataFieldEntity, false, ct);
            }
            // commit the transaction
            await _context.Database.CommitTransactionAsync(ct);

            return _mapper.Map<DataField>(dataFieldEntity);
        }

        public async Task<ViewModels.DataField> UpdateAsync(Guid id, ViewModels.DataFieldEdit dataField, bool hasMselPermission, bool hasDataFieldPermission, CancellationToken ct)
        {
            var dataFieldToUpdate = await _context.DataFields
                .Include(df => df.DataOptions)
                .SingleOrDefaultAsync(v => v.Id == id, ct);
            if (dataFieldToUpdate == null)
                throw new EntityNotFoundException<DataField>();

            // the field's stored owner decides: a MSEL's owners and editors, or ManageDataFields for the rest
            var userId = _user.GetId();
            if (dataFieldToUpdate.MselId.HasValue)
            {
                if (!hasMselPermission &&
                    !await MselOwnerRequirement.IsMet(userId, dataFieldToUpdate.MselId, _context) &&
                    !await MselEditorRequirement.IsMet(userId, dataFieldToUpdate.MselId, _context))
                    throw new ForbiddenException();
            }
            else
            {
                if (!hasDataFieldPermission)
                    throw new ForbiddenException();
            }

            // start a transaction
            await _context.Database.BeginTransactionAsync();
            // update DataOptions, only ever this field's own
            var existingDataOptions = dataFieldToUpdate.DataOptions.ToList();
            foreach (var dataOption in existingDataOptions)
            {
                if (!dataField.DataOptions.Any(d => d.Id == dataOption.Id))
                {
                    _context.DataOptions.Remove(dataOption);
                }
            }
            foreach (var dataOption in dataField.DataOptions)
            {
                var existingDataOption = existingDataOptions
                    .SingleOrDefault(d => d.Id == dataOption.Id);
                if (existingDataOption != null)
                {
                    _mapper.Map(dataOption, existingDataOption);
                    existingDataOption.ModifiedBy = userId;
                }
                else
                {
                    var newDataOption = _mapper.Map<DataOptionEntity>(dataOption);
                    newDataOption.Id = Guid.NewGuid();
                    newDataOption.DataFieldId = id;
                    newDataOption.CreatedBy = userId;
                    _context.DataOptions.Add(newDataOption);
                }
            }
            await _context.SaveChangesAsync(ct);
            // get the initial and final display order for this data field
            var updateDisplayOrder = dataFieldToUpdate.DisplayOrder != dataField.DisplayOrder;
            var newIndexIsGreater = dataField.DisplayOrder > dataFieldToUpdate.DisplayOrder;
            // update values
            _mapper.Map(dataField, dataFieldToUpdate);
            dataFieldToUpdate.ModifiedBy = userId;
            _context.DataFields.Update(dataFieldToUpdate);
            await _context.SaveChangesAsync(ct);
            // reorder the data fields if necessary
            if (updateDisplayOrder)
            {
                await Reorder(dataFieldToUpdate, newIndexIsGreater, ct);
            }
            // update the MSEL modified info
            await ServiceUtilities.SetMselModifiedAsync(dataFieldToUpdate.MselId, userId, DateTime.UtcNow, _context, ct);
            // commit the transaction
            await _context.Database.CommitTransactionAsync(ct);

            return _mapper.Map<DataField>(dataFieldToUpdate);
        }

        public async Task<Guid> DeleteAsync(Guid id, bool hasMselPermission, bool hasDataFieldPermission, CancellationToken ct)
        {
            var dataFieldToDelete = await _context.DataFields.SingleOrDefaultAsync(v => v.Id == id, ct);
            if (dataFieldToDelete == null)
                throw new EntityNotFoundException<DataField>();

            if (dataFieldToDelete.MselId.HasValue)
            {
                if (!hasMselPermission &&
                    !await MselOwnerRequirement.IsMet(_user.GetId(), dataFieldToDelete.MselId, _context) &&
                    !await MselEditorRequirement.IsMet(_user.GetId(), dataFieldToDelete.MselId, _context))
                    throw new ForbiddenException();
            }
            else
            {
                if (!hasDataFieldPermission)
                    throw new ForbiddenException();
            }

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

        public async Task<Tuple<MemoryStream, string>> DownloadJsonAsync(IEnumerable<Guid> ids, CancellationToken ct)
        {
            var idList = ids?.ToList() ?? new List<Guid>();
            // only fields that belong to no MSEL are exported; a MSEL's fields are read through the MSEL
            var dataFieldEntities = await _context.DataFields
                .Where(df => idList.Contains(df.Id) && df.MselId == null)
                .Include(df => df.DataOptions)
                .ToListAsync(ct);
            var dataFields = _mapper.Map<IEnumerable<ViewModels.DataField>>(dataFieldEntities).ToList();

            var json = JsonSerializer.Serialize(dataFields, new JsonSerializerOptions
            {
                ReferenceHandler = ReferenceHandler.Preserve,
                WriteIndented = true,
            });
            var memoryStream = new MemoryStream(Encoding.UTF8.GetBytes(json));

            return Tuple.Create(memoryStream, "data-field-templates.json");
        }

        public async Task<IEnumerable<ViewModels.DataField>> UploadJsonAsync(FileForm form, CancellationToken ct)
        {
            var uploadItem = form.ToUpload;
            string json;
            using (var reader = new StreamReader(uploadItem.OpenReadStream()))
            {
                json = await reader.ReadToEndAsync();
            }
            var options = new JsonSerializerOptions
            {
                ReferenceHandler = ReferenceHandler.Preserve,
                PropertyNameCaseInsensitive = true,
            };
            var incoming = JsonSerializer.Deserialize<List<ViewModels.DataField>>(json, options) ?? new List<ViewModels.DataField>();

            var created = new List<ViewModels.DataField>();
            var userId = _user.GetId();
            var dateNow = DateTime.UtcNow;
            foreach (var item in incoming)
            {
                var newId = Guid.NewGuid();
                item.Id = newId;
                item.IsTemplate = true;
                item.MselId = null;
                item.InjectTypeId = null;
                item.CreatedBy = userId;
                item.DateCreated = dateNow;
                item.ModifiedBy = null;
                item.DateModified = null;
                var incomingOptions = item.DataOptions?.ToList() ?? new List<ViewModels.DataOption>();
                item.DataOptions = new HashSet<ViewModels.DataOption>();
                var entity = _mapper.Map<DataFieldEntity>(item);
                _context.DataFields.Add(entity);
                foreach (var opt in incomingOptions)
                {
                    var optionEntity = new DataOptionEntity
                    {
                        Id = Guid.NewGuid(),
                        DataFieldId = newId,
                        DisplayOrder = opt.DisplayOrder,
                        OptionName = opt.OptionName,
                        OptionValue = opt.OptionValue,
                        CreatedBy = userId,
                        DateCreated = dateNow,
                    };
                    _context.DataOptions.Add(optionEntity);
                }
                created.Add(_mapper.Map<ViewModels.DataField>(entity));
            }
            await _context.SaveChangesAsync(ct);

            return created;
        }

        private async Task AddNewDataValues(DataFieldEntity dataFieldEntity, CancellationToken ct)
        {
            var scenarioEventIds = await _context.ScenarioEvents
                .Where(se => se.MselId == dataFieldEntity.MselId)
                .Select(se => se.Id)
                .ToListAsync(ct);
            var userId = _user.GetId();
            foreach (var scenarioEventId in scenarioEventIds)
            {
                var dataValueEntity = new DataValueEntity() {
                    ScenarioEventId = scenarioEventId,
                    DataFieldId = dataFieldEntity.Id,
                    CreatedBy = userId
                };
                _context.DataValues.Add(dataValueEntity);
            }
            await _context.SaveChangesAsync(ct);
        }

    }
}
