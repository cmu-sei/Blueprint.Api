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
using Microsoft.EntityFrameworkCore;
using Blueprint.Api.Data;
using Blueprint.Api.Data.Enumerations;
using Blueprint.Api.Data.Models;
using Blueprint.Api.Infrastructure.Authorization;
using Blueprint.Api.Infrastructure.Exceptions;
using Blueprint.Api.Infrastructure.Extensions;
using Blueprint.Api.ViewModels;

namespace Blueprint.Api.Services
{
    public interface IDataValueService
    {
        Task<IEnumerable<ViewModels.DataValue>> GetByMselAsync(Guid mselId, bool hasSystemPermission, CancellationToken ct);
        Task<ViewModels.DataValue> GetAsync(Guid id, bool hasSystemPermission, CancellationToken ct);
        Task<ViewModels.DataValue> CreateAsync(ViewModels.DataValueCreate dataValue, bool hasMselPermission, bool hasDataFieldPermission, CancellationToken ct);
        Task<ViewModels.DataValue> UpdateAsync(Guid id, ViewModels.DataValueEdit dataValue, bool hasMselPermission, bool hasDataFieldPermission, CancellationToken ct);
        Task<bool> DeleteAsync(Guid id, bool hasMselPermission, bool hasDataFieldPermission, CancellationToken ct);
    }

    public class DataValueService : IDataValueService
    {
        private readonly BlueprintContext _context;
        private readonly ClaimsPrincipal _user;
        private readonly IMapper _mapper;
        private readonly IXApiService _xApiService;

        public DataValueService(
            BlueprintContext context,
            IPrincipal user,
            IMapper mapper,
            IXApiService xApiService)
        {
            _context = context;
            _user = user as ClaimsPrincipal;
            _mapper = mapper;
            _xApiService = xApiService;
        }

        public async Task<IEnumerable<ViewModels.DataValue>> GetByMselAsync(Guid mselId, bool hasSystemPermission, CancellationToken ct)
        {
            if (!hasSystemPermission && !await MselUserRequirement.IsMet(_user.GetId(), mselId, _context))
                throw new ForbiddenException();

            var scenarioEventIdList = await _context.ScenarioEvents
                .Where(se => se.MselId == mselId)
                .Select(se => se.Id)
                .ToListAsync(ct);
            // a value counts as this MSEL's only when its Data Field is the MSEL's too
            var dataValueEntities = await _context.DataValues
                .Where(dv => scenarioEventIdList.Contains((Guid)dv.ScenarioEventId) && dv.DataField.MselId == mselId)
                .ToListAsync(ct);

            return _mapper.Map<IEnumerable<DataValue>>(dataValueEntities).ToList();;
        }

        public async Task<ViewModels.DataValue> GetAsync(Guid id, bool hasSystemPermission, CancellationToken ct)
        {
            var item = await _context.DataValues
                .Include(dv => dv.DataField)
                .FirstOrDefaultAsync(dv => dv.Id ==id, ct);
            if (item == null)
                throw new EntityNotFoundException<DataValueEntity>("DataValue not found: " + id);

            if (!hasSystemPermission && !await MselViewRequirement.IsMet(_user.GetId(), item.DataField.MselId, _context))
                throw new ForbiddenException();

            return _mapper.Map<DataValue>(item);
        }

        public async Task<ViewModels.DataValue> CreateAsync(ViewModels.DataValueCreate dataValue, bool hasMselPermission, bool hasDataFieldPermission, CancellationToken ct)
        {
            // DataValues are always MSEL-specific (through ScenarioEvent), so a template field has no cells
            var mselId = await _context.DataFields
                .Where(df => df.Id == dataValue.DataFieldId)
                .Select(df => df.MselId)
                .FirstOrDefaultAsync(ct);
            if (mselId == null)
                throw new EntityNotFoundException<DataField>($"DataField {dataValue.DataFieldId} was not found on a MSEL.");

            if (!hasMselPermission &&
                !await MselOwnerRequirement.IsMet(_user.GetId(), mselId, _context) &&
                !await MselEditorRequirement.IsMet(_user.GetId(), mselId, _context))
                throw new ForbiddenException();

            // the cell's Scenario Event must be on the Data Field's MSEL
            if (!await _context.ScenarioEvents.AnyAsync(se => se.Id == dataValue.ScenarioEventId && se.MselId == mselId, ct))
                throw new EntityNotFoundException<ScenarioEvent>($"ScenarioEvent {dataValue.ScenarioEventId} was not found in MSEL {mselId}.");

            var dataValueEntity = _mapper.Map<DataValueEntity>(dataValue);
            dataValueEntity.Id = dataValue.Id != Guid.Empty ? dataValue.Id : Guid.NewGuid();
            dataValueEntity.CreatedBy = _user.GetId();

            _context.DataValues.Add(dataValueEntity);
            await _context.SaveChangesAsync(ct);
            // update the MSEL modified info
            await ServiceUtilities.SetMselModifiedAsync(mselId, dataValueEntity.CreatedBy, DateTime.UtcNow, _context, ct);

            return await GetAsync(dataValueEntity.Id, true, ct);
        }

        public async Task<ViewModels.DataValue> UpdateAsync(Guid id, ViewModels.DataValueEdit dataValue, bool hasMselPermission, bool hasDataFieldPermission, CancellationToken ct)
        {
            var dataValueToUpdate = await _context.DataValues.SingleOrDefaultAsync(v => v.Id == id, ct);
            if (dataValueToUpdate == null)
                throw new EntityNotFoundException<DataValue>($"ID = {id}");

            // a value whose field is a template's, not a MSEL's, is not a cell this endpoint edits
            var dataField = await _context.DataFields.SingleOrDefaultAsync(df => df.Id == dataValueToUpdate.DataFieldId);
            if (dataField == null || dataField.MselId == null)
                throw new EntityNotFoundException<DataField>($"For DataValue ID = {id} DataField ID = {dataValueToUpdate.DataFieldId}");

            // Users with EditMsels permission or MSEL Owners can change every DataValue
            if (!hasMselPermission && !(await MselOwnerRequirement.IsMet(_user.GetId(), dataField.MselId, _context)))
            {
                // to change the assignedTeam, user must have EditMsels permission or be a MSEL owner
                if (dataField.DataType == DataFieldType.Team)
                    throw new ForbiddenException("Cannot change the Assigned Team.");
                // Evaluators update checkboxes, as they assess, and only they do; a checkbox needs nothing else
                var isCheckbox = dataField.DataType == DataFieldType.Checkbox;
                if (isCheckbox &&
                    !(await EvaluatorRequirement.IsMet(_user.GetId(), dataField.MselId, _context)))
                    throw new ForbiddenException("Cannot change this value.");
                // MSEL Approvers can change everything else
                if (!isCheckbox && !(await MselApproverRequirement.IsMet(_user.GetId(), dataField.MselId, _context)))
                {
                    // to change the status, user must be a MSEL approver
                    if (dataField.DataType == DataFieldType.Status)
                            throw new ForbiddenException("Cannot change the Status.");
                    // to change anything, user must be a MSEL editor
                    if (!(await MselEditorRequirement.IsMet(_user.GetId(), dataField.MselId, _context)))
                        throw new ForbiddenException();
                }
            }

            var oldValue = dataValueToUpdate.Value;
            _mapper.Map(dataValue, dataValueToUpdate);
            dataValueToUpdate.ModifiedBy = _user.GetId();

            _context.DataValues.Update(dataValueToUpdate);
            await _context.SaveChangesAsync(ct);
            // update the MSEL modified info
            await ServiceUtilities.SetMselModifiedAsync(dataField.MselId, dataValueToUpdate.ModifiedBy, DateTime.UtcNow, _context, ct);

            // Emit xAPI statement for checkbox changes
            if (dataField.DataType == DataFieldType.Checkbox && oldValue != dataValueToUpdate.Value &&
                dataValueToUpdate.ScenarioEventId.HasValue && dataField.MselId.HasValue)
            {
                var scenarioEvent = await _context.ScenarioEvents.FindAsync(dataValueToUpdate.ScenarioEventId.Value);
                if (scenarioEvent != null)
                {
                    var isChecked = dataValueToUpdate.Value?.ToLower() == "true";
                    await _xApiService.RecordCheckboxChangeAsync(
                        dataField.MselId.Value,
                        scenarioEvent.Id,
                        dataField.Id,
                        dataField.Name,
                        isChecked,
                        ct);
                }
            }

            return await GetAsync(dataValueToUpdate.Id, true, ct);
        }

        public async Task<bool> DeleteAsync(Guid id, bool hasMselPermission, bool hasDataFieldPermission, CancellationToken ct)
        {
            var dataValueToDelete = await _context.DataValues.SingleOrDefaultAsync(v => v.Id == id, ct);
            if (dataValueToDelete == null)
                throw new EntityNotFoundException<DataValue>();

            var mselId = await _context.DataFields
                .Where(df => df.Id == dataValueToDelete.DataFieldId)
                .Select(df => df.MselId)
                .FirstOrDefaultAsync();
            // DataValues are always MSEL-specific (through ScenarioEvent)
            if (!hasMselPermission &&
                !await MselOwnerRequirement.IsMet(_user.GetId(), mselId, _context) &&
                !await MselEditorRequirement.IsMet(_user.GetId(), mselId, _context))
                throw new ForbiddenException();

            _context.DataValues.Remove(dataValueToDelete);
            await _context.SaveChangesAsync(ct);
            // update the MSEL modified info
            await ServiceUtilities.SetMselModifiedAsync(mselId, _user.GetId(), DateTime.UtcNow, _context, ct);

            return true;
        }

    }
}

