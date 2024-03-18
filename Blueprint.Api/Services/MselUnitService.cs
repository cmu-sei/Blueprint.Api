// Copyright 2024 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

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
using Microsoft.Extensions.Logging;
using Blueprint.Api.Data;
using Blueprint.Api.Data.Models;
using Blueprint.Api.Infrastructure.Authorization;
using Blueprint.Api.Infrastructure.Exceptions;
using Blueprint.Api.Infrastructure.Extensions;
using Blueprint.Api.ViewModels;

namespace Blueprint.Api.Services
{
    public interface IMselUnitService
    {
        Task<IEnumerable<ViewModels.MselUnit>> GetByMselAsync(Guid mselId, CancellationToken ct);
        Task<ViewModels.MselUnit> GetAsync(Guid id, CancellationToken ct);
        Task<ViewModels.MselUnit> CreateAsync(ViewModels.MselUnit mselUnit, CancellationToken ct);
        Task<ViewModels.MselUnit> UpdateAsync(Guid id, ViewModels.MselUnit mselUnit, CancellationToken ct);
        Task<bool> DeleteAsync(Guid id, CancellationToken ct);
        Task<bool> DeleteByIdsAsync(Guid mselId, Guid unitId, CancellationToken ct);
    }

    public class MselUnitService : IMselUnitService
    {
        private readonly BlueprintContext _context;
        private readonly IAuthorizationService _authorizationService;
        private readonly ClaimsPrincipal _user;
        private readonly IMapper _mapper;
        private readonly ILogger<IMselUnitService> _logger;

        public MselUnitService(BlueprintContext context, IAuthorizationService authorizationService, IPrincipal user, ILogger<IMselUnitService> logger, IMapper mapper)
        {
            _context = context;
            _authorizationService = authorizationService;
            _user = user as ClaimsPrincipal;
            _mapper = mapper;
            _logger = logger;
        }

        public async Task<IEnumerable<ViewModels.MselUnit>> GetByMselAsync(Guid mselId, CancellationToken ct)
        {
            var msel = await _context.Msels.SingleOrDefaultAsync(v => v.Id == mselId, ct);
            if (msel == null)
                throw new EntityNotFoundException<MselEntity>();

            // user must be a Content Developer or a MSEL owner
            if (!(await _authorizationService.AuthorizeAsync(_user, null, new ContentDeveloperRequirement())).Succeeded &&
                !(await MselOwnerRequirement.IsMet(_user.GetId(), msel.Id, _context)))
                throw new ForbiddenException();

            var items = await _context.MselUnits
                .Where(tc => tc.MselId == mselId)
                .Include(mt => mt.Unit)
                .ThenInclude(t => t.UnitUsers)
                .ThenInclude(tu => tu.User)
                .ToListAsync(ct);

            return _mapper.Map<IEnumerable<MselUnit>>(items);
        }

        public async Task<ViewModels.MselUnit> GetAsync(Guid id, CancellationToken ct)
        {
            var mselUnit = await _context.MselUnits.SingleOrDefaultAsync(v => v.Id == id, ct);
            if (mselUnit == null)
                throw new EntityNotFoundException<MselEntity>();

            // user must be a Content Developer or a MSEL Viewer
            if (!(await _authorizationService.AuthorizeAsync(_user, null, new ContentDeveloperRequirement())).Succeeded &&
                !(await MselViewRequirement.IsMet(_user.GetId(), mselUnit.MselId, _context)))
                throw new ForbiddenException();

            var item = await _context.MselUnits
                .Include(mt => mt.Unit)
                .ThenInclude(t => t.UnitUsers)
                .ThenInclude(tu => tu.User)
                .SingleOrDefaultAsync(o => o.Id == id, ct);

            return _mapper.Map<MselUnit>(item);
        }

        public async Task<ViewModels.MselUnit> CreateAsync(ViewModels.MselUnit mselUnit, CancellationToken ct)
        {
            var msel = await _context.Msels.SingleOrDefaultAsync(v => v.Id == mselUnit.MselId, ct);
            if (msel == null)
                throw new EntityNotFoundException<MselEntity>();

            var unit = await _context.Units.SingleOrDefaultAsync(v => v.Id == mselUnit.UnitId, ct);
            if (unit == null)
                throw new EntityNotFoundException<UnitEntity>();

            // user must be a Content Developer or a MSEL owner
            if (!(await _authorizationService.AuthorizeAsync(_user, null, new ContentDeveloperRequirement())).Succeeded &&
                !(await MselOwnerRequirement.IsMet(_user.GetId(), msel.Id, _context)))
                throw new ForbiddenException();

            if (await _context.MselUnits.AnyAsync(mt => mt.UnitId == unit.Id && mt.MselId == msel.Id))
                throw new ArgumentException("MSEL Unit already exists.");

            var mselUnitEntity = _mapper.Map<MselUnitEntity>(mselUnit);
            mselUnitEntity.Id = mselUnitEntity.Id != Guid.Empty ? mselUnitEntity.Id : Guid.NewGuid();

            _context.MselUnits.Add(mselUnitEntity);
            await _context.SaveChangesAsync(ct);
            _logger.LogWarning($"Unit {mselUnit.UnitId} added to MSEL {mselUnit.MselId} by {_user.GetId()}");
            return await GetAsync(mselUnitEntity.Id, ct);
        }

        public async Task<ViewModels.MselUnit> UpdateAsync(Guid id, ViewModels.MselUnit mselUnit, CancellationToken ct)
        {
            // user must be a Content Developer or a MSEL owner
            if (!(await _authorizationService.AuthorizeAsync(_user, null, new ContentDeveloperRequirement())).Succeeded &&
                !(await MselOwnerRequirement.IsMet(_user.GetId(), mselUnit.MselId, _context)))
                throw new ForbiddenException();

            var mselUnitToUpdate = await _context.MselUnits.SingleOrDefaultAsync(v => v.Id == id, ct);
            if (mselUnitToUpdate == null)
                throw new EntityNotFoundException<MselUnit>();

            _mapper.Map(mselUnit, mselUnitToUpdate);

            _context.MselUnits.Update(mselUnitToUpdate);
            await _context.SaveChangesAsync(ct);

            mselUnit = await GetAsync(mselUnitToUpdate.Id, ct);
            return mselUnit;
        }

        public async Task<bool> DeleteAsync(Guid id, CancellationToken ct)
        {
            var mselUnitToDelete = await _context.MselUnits.SingleOrDefaultAsync(v => v.Id == id, ct);
            if (mselUnitToDelete == null)
                throw new EntityNotFoundException<MselUnit>();

            // user must be a Content Developer or a MSEL owner
            if (!(await _authorizationService.AuthorizeAsync(_user, null, new ContentDeveloperRequirement())).Succeeded &&
                !(await MselOwnerRequirement.IsMet(_user.GetId(), mselUnitToDelete.MselId, _context)))
                throw new ForbiddenException();

            _context.MselUnits.Remove(mselUnitToDelete);
            await _context.SaveChangesAsync(ct);
            _logger.LogWarning($"Unit {mselUnitToDelete.UnitId} removed from MSEL {mselUnitToDelete.MselId} by {_user.GetId()}");
            return true;
        }

        public async Task<bool> DeleteByIdsAsync(Guid mselId, Guid unitId, CancellationToken ct)
        {
            var mselUnitToDelete = await _context.MselUnits.SingleOrDefaultAsync(v => (v.UnitId == unitId) && (v.MselId == mselId), ct);
            if (mselUnitToDelete == null)
                throw new EntityNotFoundException<MselUnit>();

            // user must be a Content Developer or a MSEL owner
            if (!(await _authorizationService.AuthorizeAsync(_user, null, new ContentDeveloperRequirement())).Succeeded &&
                !(await MselOwnerRequirement.IsMet(_user.GetId(), mselUnitToDelete.MselId, _context)))
                throw new ForbiddenException();

            _context.MselUnits.Remove(mselUnitToDelete);
            await _context.SaveChangesAsync(ct);
            _logger.LogWarning($"Unit {mselUnitToDelete.UnitId} removed from MSEL {mselUnitToDelete.MselId} by {_user.GetId()}");
            return true;
        }

    }
}

