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
using Blueprint.Api.Data.Models;
using Blueprint.Api.Infrastructure.Authorization;
using Blueprint.Api.Infrastructure.Exceptions;
using Blueprint.Api.Infrastructure.Extensions;
using Blueprint.Api.ViewModels;

namespace Blueprint.Api.Services
{
    public interface IDataOptionService
    {
        Task<IEnumerable<ViewModels.DataOption>> GetByMselAsync(Guid mselId, bool hasSystemPermission, CancellationToken ct);
        Task<IEnumerable<ViewModels.DataOption>> GetByDataFieldAsync(Guid dataFieldId, bool hasSystemPermission, CancellationToken ct);
        Task<ViewModels.DataOption> GetAsync(Guid id, bool hasSystemPermission, CancellationToken ct);
        Task<ViewModels.DataOption> CreateAsync(ViewModels.DataOption dataOption, bool hasMselPermission, bool hasDataFieldPermission, CancellationToken ct);
        Task<ViewModels.DataOption> UpdateAsync(Guid id, ViewModels.DataOption dataOption, bool hasMselPermission, bool hasDataFieldPermission, CancellationToken ct);
        Task<bool> DeleteAsync(Guid id, bool hasMselPermission, bool hasDataFieldPermission, CancellationToken ct);
    }

    public class DataOptionService : IDataOptionService
    {
        private readonly BlueprintContext _context;
        private readonly ClaimsPrincipal _user;
        private readonly IMapper _mapper;

        public DataOptionService(
            BlueprintContext context,
            IPrincipal user,
            IMapper mapper)
        {
            _context = context;
            _user = user as ClaimsPrincipal;
            _mapper = mapper;
        }

        public async Task<IEnumerable<ViewModels.DataOption>> GetByMselAsync(Guid mselId, bool hasSystemPermission, CancellationToken ct)
        {
            if (!hasSystemPermission && !(await MselViewRequirement.IsMet(_user.GetId(), mselId, _context)))
                throw new ForbiddenException();

            var dataFieldIdList = await _context.DataFields
                .Where(df => df.MselId == mselId)
                .Select(df => df.Id)
                .ToListAsync(ct);
            var dataOptionEntities = await _context.DataOptions
                .Where(op => dataFieldIdList.Contains(op.DataFieldId))
                .ToListAsync();

            return _mapper.Map<IEnumerable<DataOption>>(dataOptionEntities).ToList();;
        }

        public async Task<IEnumerable<ViewModels.DataOption>> GetByDataFieldAsync(Guid dataFieldId, bool hasSystemPermission, CancellationToken ct)
        {
            var dataField = await _context.DataFields.SingleOrDefaultAsync(df => df.Id == dataFieldId, ct);
            if (dataField == null)
                throw new EntityNotFoundException<DataField>(dataFieldId.ToString());

            if (!(dataField.MselId == null) &&
                !hasSystemPermission &&
                !(await MselViewRequirement.IsMet(_user.GetId(), dataField.MselId, _context)))
                throw new ForbiddenException();

            var dataOptionEntities = await _context.DataOptions
                .Where(dataOption => dataOption.DataFieldId == dataFieldId)
                .ToListAsync();

            return _mapper.Map<IEnumerable<DataOption>>(dataOptionEntities).ToList();;
        }

        public async Task<ViewModels.DataOption> GetAsync(Guid id, bool hasSystemPermission, CancellationToken ct)
        {
            var dataOption = await _context.DataOptions.SingleOrDefaultAsync(dopt => dopt.Id == id, ct);
            if (dataOption == null)
                throw new EntityNotFoundException<DataOption>(id.ToString());

            var dataField = await _context.DataFields.SingleOrDefaultAsync(df => df.Id == dataOption.DataFieldId, ct);
            // Templates (null MselId) can be viewed by anyone
            if (dataField.MselId.HasValue)
            {
                if (!hasSystemPermission && !await MselViewRequirement.IsMet(_user.GetId(), dataField.MselId, _context))
                    throw new ForbiddenException();
            }

            return _mapper.Map<DataOption>(dataOption);
        }

        public async Task<ViewModels.DataOption> CreateAsync(ViewModels.DataOption dataOption, bool hasMselPermission, bool hasDataFieldPermission, CancellationToken ct)
        {
            var dataField = await _context.DataFields
                .FindAsync(dataOption.DataFieldId);
            if (dataField == null)
                throw new EntityNotFoundException<DataField>("DataField not found when creating a DataOption.  " + dataOption.DataFieldId.ToString());

            if (dataField.MselId.HasValue)
            {
                if (!hasMselPermission &&
                    !await MselOwnerRequirement.IsMet(_user.GetId(), dataField.MselId, _context) &&
                    !await MselEditorRequirement.IsMet(_user.GetId(), dataField.MselId, _context))
                    throw new ForbiddenException();
            }
            else
            {
                if (!hasDataFieldPermission)
                    throw new ForbiddenException();
            }

            dataOption.Id = dataOption.Id != Guid.Empty ? dataOption.Id : Guid.NewGuid();
            dataOption.CreatedBy = _user.GetId();
            var dataOptionEntity = _mapper.Map<DataOptionEntity>(dataOption);
            _context.DataOptions.Add(dataOptionEntity);
            await _context.SaveChangesAsync(ct);
            // update the dataField
            var dataFieldEntity = await _context.DataFields.FindAsync(dataOption.DataFieldId);
            dataField.ModifiedBy = dataFieldEntity.CreatedBy;
            await _context.SaveChangesAsync(ct);
            dataOption = await GetAsync(dataOptionEntity.Id, true, ct);

            return dataOption;
        }

        public async Task<ViewModels.DataOption> UpdateAsync(Guid id, ViewModels.DataOption dataOption, bool hasMselPermission, bool hasDataFieldPermission, CancellationToken ct)
        {
            var dataField = await _context.DataFields
                .FindAsync(dataOption.DataFieldId);
            if (dataField == null)
                throw new EntityNotFoundException<DataField>("DataField not found when updating a DataOption.  " + dataOption.DataFieldId.ToString());

            if (dataField.MselId.HasValue)
            {
                if (!hasMselPermission &&
                    !await MselOwnerRequirement.IsMet(_user.GetId(), dataField.MselId, _context) &&
                    !await MselEditorRequirement.IsMet(_user.GetId(), dataField.MselId, _context))
                    throw new ForbiddenException();
            }
            else
            {
                if (!hasDataFieldPermission)
                    throw new ForbiddenException();
            }

            var dataOptionToUpdate = await _context.DataOptions.SingleOrDefaultAsync(v => v.Id == id, ct);

            if (dataOptionToUpdate == null)
                throw new EntityNotFoundException<DataOption>();

            dataOption.ModifiedBy = _user.GetId();
            _mapper.Map(dataOption, dataOptionToUpdate);
            _context.DataOptions.Update(dataOptionToUpdate);
            await _context.SaveChangesAsync(ct);
            // updated the dataField
            var dataFieldEntity = await _context.DataFields.FindAsync(dataOption.DataFieldId);
            dataField.ModifiedBy = dataFieldEntity.ModifiedBy;
            await _context.SaveChangesAsync(ct);

            dataOption = await GetAsync(dataOptionToUpdate.Id, true, ct);

            return dataOption;
        }

        public async Task<bool> DeleteAsync(Guid id, bool hasMselPermission, bool hasDataFieldPermission, CancellationToken ct)
        {
            var dataOptionToDelete = await _context.DataOptions.SingleOrDefaultAsync(v => v.Id == id, ct);
            if (dataOptionToDelete == null)
                throw new EntityNotFoundException<DataOption>();

            var dataField = await _context.DataFields
                .SingleOrDefaultAsync(df => df.Id == dataOptionToDelete.DataFieldId, ct);
            if (dataField == null)
                throw new EntityNotFoundException<DataField>("DataField not found when deleting a DataOption.  " + dataOptionToDelete.DataFieldId.ToString());

            if (dataField.MselId.HasValue)
            {
                if (!hasMselPermission &&
                    !await MselOwnerRequirement.IsMet(_user.GetId(), dataField.MselId, _context) &&
                    !await MselEditorRequirement.IsMet(_user.GetId(), dataField.MselId, _context))
                    throw new ForbiddenException();
            }
            else
            {
                if (!hasDataFieldPermission)
                    throw new ForbiddenException();
            }

            _context.DataOptions.Remove(dataOptionToDelete);
            await _context.SaveChangesAsync(ct);
            // updated the dataField
            var dataFieldEntity = await _context.DataFields.FindAsync(dataOptionToDelete.DataFieldId);
            dataField.ModifiedBy = _user.GetId();
            await _context.SaveChangesAsync(ct);

            return true;
        }

    }
}
