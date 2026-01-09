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
    public interface ICatalogUnitService
    {
        Task<IEnumerable<ViewModels.CatalogUnit>> GetByCatalogAsync(Guid catalogId, CancellationToken ct);
        Task<ViewModels.CatalogUnit> GetAsync(Guid id, bool hasSystemPermission, CancellationToken ct);
        Task<ViewModels.CatalogUnit> CreateAsync(ViewModels.CatalogUnit catalogUnit, CancellationToken ct);
        Task<ViewModels.CatalogUnit> UpdateAsync(Guid id, ViewModels.CatalogUnit catalogUnit, CancellationToken ct);
        Task<bool> DeleteAsync(Guid id, CancellationToken ct);
        Task<bool> DeleteByIdsAsync(Guid catalogId, Guid unitId, CancellationToken ct);
    }

    public class CatalogUnitService : ICatalogUnitService
    {
        private readonly BlueprintContext _context;
        private readonly ClaimsPrincipal _user;
        private readonly IMapper _mapper;
        private readonly ILogger<ICatalogUnitService> _logger;

        public CatalogUnitService(BlueprintContext context, IPrincipal user, ILogger<ICatalogUnitService> logger, IMapper mapper)
        {
            _context = context;
            _user = user as ClaimsPrincipal;
            _mapper = mapper;
            _logger = logger;
        }

        public async Task<IEnumerable<ViewModels.CatalogUnit>> GetByCatalogAsync(Guid catalogId, CancellationToken ct)
        {
            var catalog = await _context.Catalogs.SingleOrDefaultAsync(v => v.Id == catalogId, ct);
            if (catalog == null)
                throw new EntityNotFoundException<CatalogEntity>();

            var items = await _context.CatalogUnits
                .Where(tc => tc.CatalogId == catalogId)
                .Include(mt => mt.Unit)
                .ThenInclude(t => t.UnitUsers)
                .ThenInclude(tu => tu.User)
                .ToListAsync(ct);

            return _mapper.Map<IEnumerable<CatalogUnit>>(items);
        }

        public async Task<ViewModels.CatalogUnit> GetAsync(Guid id, bool hasSystemPermission, CancellationToken ct)
        {
            var catalogUnit = await _context.CatalogUnits.SingleOrDefaultAsync(v => v.Id == id, ct);
            if (catalogUnit == null)
                throw new EntityNotFoundException<CatalogEntity>();

            // user must have ViewMsels permission or be a Catalog Viewer
            if (!hasSystemPermission && !(await CatalogViewRequirement.IsMet(_user.GetId(), catalogUnit.CatalogId, _context)))
                throw new ForbiddenException();

            var item = await _context.CatalogUnits
                .Include(mt => mt.Unit)
                .ThenInclude(t => t.UnitUsers)
                .ThenInclude(tu => tu.User)
                .SingleOrDefaultAsync(o => o.Id == id, ct);

            return _mapper.Map<CatalogUnit>(item);
        }

        public async Task<ViewModels.CatalogUnit> CreateAsync(ViewModels.CatalogUnit catalogUnit, CancellationToken ct)
        {
            var catalog = await _context.Catalogs.SingleOrDefaultAsync(v => v.Id == catalogUnit.CatalogId, ct);
            if (catalog == null)
                throw new EntityNotFoundException<CatalogEntity>();

            var unit = await _context.Units.SingleOrDefaultAsync(v => v.Id == catalogUnit.UnitId, ct);
            if (unit == null)
                throw new EntityNotFoundException<UnitEntity>();

            if (await _context.CatalogUnits.AnyAsync(mt => mt.UnitId == unit.Id && mt.CatalogId == catalog.Id))
                throw new ArgumentException("Catalog Unit already exists.");

            var catalogUnitEntity = _mapper.Map<CatalogUnitEntity>(catalogUnit);
            catalogUnitEntity.Id = catalogUnitEntity.Id != Guid.Empty ? catalogUnitEntity.Id : Guid.NewGuid();

            _context.CatalogUnits.Add(catalogUnitEntity);
            await _context.SaveChangesAsync(ct);
            _logger.LogWarning($"Unit {catalogUnit.UnitId} added to Catalog {catalogUnit.CatalogId} by {_user.GetId()}");
            return await GetAsync(catalogUnitEntity.Id, true, ct);
        }

        public async Task<ViewModels.CatalogUnit> UpdateAsync(Guid id, ViewModels.CatalogUnit catalogUnit, CancellationToken ct)
        {
            var catalogUnitToUpdate = await _context.CatalogUnits.SingleOrDefaultAsync(v => v.Id == id, ct);
            if (catalogUnitToUpdate == null)
                throw new EntityNotFoundException<CatalogUnit>();

            _mapper.Map(catalogUnit, catalogUnitToUpdate);

            _context.CatalogUnits.Update(catalogUnitToUpdate);
            await _context.SaveChangesAsync(ct);

            catalogUnit = await GetAsync(catalogUnitToUpdate.Id, true, ct);
            return catalogUnit;
        }

        public async Task<bool> DeleteAsync(Guid id, CancellationToken ct)
        {
            var catalogUnitToDelete = await _context.CatalogUnits.SingleOrDefaultAsync(v => v.Id == id, ct);
            if (catalogUnitToDelete == null)
                throw new EntityNotFoundException<CatalogUnit>();

            _context.CatalogUnits.Remove(catalogUnitToDelete);
            await _context.SaveChangesAsync(ct);
            _logger.LogWarning($"Unit {catalogUnitToDelete.UnitId} removed from Catalog {catalogUnitToDelete.CatalogId} by {_user.GetId()}");
            return true;
        }

        public async Task<bool> DeleteByIdsAsync(Guid catalogId, Guid unitId, CancellationToken ct)
        {
            var catalogUnitToDelete = await _context.CatalogUnits.SingleOrDefaultAsync(v => (v.UnitId == unitId) && (v.CatalogId == catalogId), ct);
            if (catalogUnitToDelete == null)
                throw new EntityNotFoundException<CatalogUnit>();

            _context.CatalogUnits.Remove(catalogUnitToDelete);
            await _context.SaveChangesAsync(ct);
            _logger.LogWarning($"Unit {catalogUnitToDelete.UnitId} removed from Catalog {catalogUnitToDelete.CatalogId} by {_user.GetId()}");
            return true;
        }

    }
}
