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
        Task<ViewModels.DataValue> CreateAsync(ViewModels.DataValue dataValue, bool hasMselPermission, bool hasDataFieldPermission, CancellationToken ct);
        Task<ViewModels.DataValue> UpdateAsync(Guid id, ViewModels.DataValue dataValue, bool hasMselPermission, bool hasDataFieldPermission, CancellationToken ct);
        Task<bool> DeleteAsync(Guid id, bool hasMselPermission, bool hasDataFieldPermission, CancellationToken ct);
    }

    public class DataValueService : IDataValueService
    {
        private readonly BlueprintContext _context;
        private readonly ClaimsPrincipal _user;
        private readonly IMapper _mapper;

        public DataValueService(
            BlueprintContext context,
            IPrincipal user,
            IMapper mapper)
        {
            _context = context;
            _user = user as ClaimsPrincipal;
            _mapper = mapper;
        }

        public async Task<IEnumerable<ViewModels.DataValue>> GetByMselAsync(Guid mselId, bool hasSystemPermission, CancellationToken ct)
        {
            if (!hasSystemPermission && !await MselUserRequirement.IsMet(_user.GetId(), mselId, _context))
                throw new ForbiddenException();

            var scenarioEventIdList = await _context.ScenarioEvents
                .Where(se => se.MselId == mselId)
                .Select(se => se.Id)
                .ToListAsync(ct);
            var dataValueEntities = await _context.DataValues
                .Where(dv => scenarioEventIdList.Contains((Guid)dv.ScenarioEventId))
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

        public async Task<ViewModels.DataValue> CreateAsync(ViewModels.DataValue dataValue, bool hasMselPermission, bool hasDataFieldPermission, CancellationToken ct)
        {
            var mselId = await _context.DataFields
                .Where(df => df.Id == dataValue.DataFieldId)
                .Select(df => df.MselId)
                .FirstOrDefaultAsync(ct);
            // DataValues are always MSEL-specific (through ScenarioEvent)
            if (!hasMselPermission && !await MselEditorRequirement.IsMet(_user.GetId(), mselId, _context))
                throw new ForbiddenException();

            dataValue.Id = dataValue.Id != Guid.Empty ? dataValue.Id : Guid.NewGuid();
            dataValue.CreatedBy = _user.GetId();
            var DataValueEntity = _mapper.Map<DataValueEntity>(dataValue);

            _context.DataValues.Add(DataValueEntity);
            await _context.SaveChangesAsync(ct);
            // update the MSEL modified info
            await ServiceUtilities.SetMselModifiedAsync(mselId, dataValue.CreatedBy, DateTime.UtcNow, _context, ct);
            dataValue = await GetAsync(DataValueEntity.Id, true, ct);

            return dataValue;
        }

        public async Task<ViewModels.DataValue> UpdateAsync(Guid id, ViewModels.DataValue dataValue, bool hasMselPermission, bool hasDataFieldPermission, CancellationToken ct)
        {
            var dataValueToUpdate = await _context.DataValues.SingleOrDefaultAsync(v => v.Id == id, ct);
            if (dataValueToUpdate == null)
                throw new EntityNotFoundException<DataValue>($"ID = {id}");

            var dataField = await _context.DataFields.SingleOrDefaultAsync(df => df.Id == dataValueToUpdate.DataFieldId);
            if (dataField == null)
                throw new EntityNotFoundException<DataField>($"For DataValue ID = {id} DataField ID = {dataValueToUpdate.DataFieldId}");

            // Users with EditMsels permission or MSEL Owners can change every DataValue
            if (!hasMselPermission && !(await MselOwnerRequirement.IsMet(_user.GetId(), dataField.MselId, _context)))
            {
                // to change the assignedTeam, user must have EditMsels permission or be a MSEL owner
                if (dataField.DataType == DataFieldType.Team)
                    throw new ForbiddenException("Cannot change the Assigned Team.");
                // Evaluators can update checkboxes
                if (dataField.DataType == DataFieldType.Checkbox &&
                    !(await EvaluatorRequirement.IsMet(_user.GetId(), dataField.MselId, _context)))
                    throw new ForbiddenException("Cannot change this value.");
                // MSEL Approvers can change everything else
                if (!(await MselApproverRequirement.IsMet(_user.GetId(), dataField.MselId, _context)))
                {
                    // to change the status, user must be a MSEL approver
                    if (dataField.DataType == DataFieldType.Status)
                            throw new ForbiddenException("Cannot change the Status.");
                    // to change anything, user must be a MSEL editor
                    if (!(await MselEditorRequirement.IsMet(_user.GetId(), dataField.MselId, _context)))
                        throw new ForbiddenException();
                }
            }

            dataValue.ModifiedBy = _user.GetId();
            _mapper.Map(dataValue, dataValueToUpdate);

            _context.DataValues.Update(dataValueToUpdate);
            await _context.SaveChangesAsync(ct);
            // update the MSEL modified info
            await ServiceUtilities.SetMselModifiedAsync(dataField.MselId, dataValue.ModifiedBy, DateTime.UtcNow, _context, ct);

            dataValue = await GetAsync(dataValueToUpdate.Id, true, ct);

            return dataValue;
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
            if (!hasMselPermission && !await MselEditorRequirement.IsMet(_user.GetId(), mselId, _context))
                throw new ForbiddenException();

            _context.DataValues.Remove(dataValueToDelete);
            await _context.SaveChangesAsync(ct);
            // update the MSEL modified info
            await ServiceUtilities.SetMselModifiedAsync(mselId, _user.GetId(), DateTime.UtcNow, _context, ct);

            return true;
        }

    }
}

