// Copyright 2024 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using AutoMapper;
using AutoMapper.QueryableExtensions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Blueprint.Api.Data;
using Blueprint.Api.Data.Models;
using Blueprint.Api.Infrastructure.Extensions;
using Blueprint.Api.Infrastructure.Exceptions;
using Blueprint.Api.ViewModels;

namespace Blueprint.Api.Services
{
    public interface IUnitService
    {
        Task<IEnumerable<ViewModels.Unit>> GetAsync(CancellationToken ct);
        Task<ViewModels.Unit> GetAsync(Guid id, CancellationToken ct);
        Task<IEnumerable<ViewModels.Unit>> GetMineAsync(CancellationToken ct);
        Task<IEnumerable<ViewModels.Unit>> GetByUserAsync(Guid userId, CancellationToken ct);
        Task<ViewModels.Unit> CreateAsync(ViewModels.Unit unit, CancellationToken ct);
        Task<ViewModels.Unit> UpdateAsync(Guid id, ViewModels.Unit unit, CancellationToken ct);
        Task<bool> DeleteAsync(Guid id, CancellationToken ct);
    }

    public class UnitService : IUnitService
    {
        private readonly BlueprintContext _context;
        private readonly ClaimsPrincipal _user;
        private readonly IMapper _mapper;
        private readonly ILogger<IUnitService> _logger;

        public UnitService(BlueprintContext context, IPrincipal unit, ILogger<IUnitService> logger, IMapper mapper)
        {
            _context = context;
            _user = unit as ClaimsPrincipal;
            _mapper = mapper;
            _logger = logger;
        }

        public async Task<IEnumerable<ViewModels.Unit>> GetAsync(CancellationToken ct)
        {
            var items = await _context.Units
                .ToArrayAsync(ct);
            return _mapper.Map<IEnumerable<Unit>>(items);
        }

        public async Task<ViewModels.Unit> GetAsync(Guid id, CancellationToken ct)
        {
            var item = await _context.Units
                .SingleOrDefaultAsync(o => o.Id == id, ct);
            return _mapper.Map<Unit>(item);
        }

        public async Task<IEnumerable<ViewModels.Unit>> GetMineAsync(CancellationToken ct)
        {
            var items = await _context.UnitUsers
                .Where(w => w.UserId == _user.GetId())
                .Include(tu => tu.Unit)
                .Select(x => x.Unit)
                .ToListAsync(ct);

            return _mapper.Map<IEnumerable<Unit>>(items);
        }

        public async Task<IEnumerable<ViewModels.Unit>> GetByUserAsync(Guid userId, CancellationToken ct)
        {
            var items = await _context.UnitUsers
                .Where(w => w.UserId == userId)
                .Select(x => x.Unit)
                .ToListAsync(ct);

            return _mapper.Map<IEnumerable<Unit>>(items);
        }

        public async Task<ViewModels.Unit> CreateAsync(ViewModels.Unit unit, CancellationToken ct)
        {
            unit.Id = unit.Id != Guid.Empty ? unit.Id : Guid.NewGuid();
            unit.CreatedBy = _user.GetId();
            var unitEntity = _mapper.Map<UnitEntity>(unit);

            _context.Units.Add(unitEntity);
            await _context.SaveChangesAsync(ct);
            _logger.LogWarning($"Unit ({unitEntity.Id}) created by {_user.GetId()}");
            return await GetAsync(unitEntity.Id, ct);
        }

        public async Task<ViewModels.Unit> UpdateAsync(Guid id, ViewModels.Unit unit, CancellationToken ct)
        {
            // Don't allow changing your own Id
            if (id == _user.GetId() && id != unit.Id)
            {
                throw new ForbiddenException("You cannot change your own Id");
            }

            var unitToUpdate = await _context.Units.SingleOrDefaultAsync(v => v.Id == id, ct);

            if (unitToUpdate == null)
                throw new EntityNotFoundException<Unit>();

            unit.ModifiedBy = _user.GetId();
            _mapper.Map(unit, unitToUpdate);

            _context.Units.Update(unitToUpdate);
            await _context.SaveChangesAsync(ct);
            _logger.LogWarning($"Unit ({unitToUpdate.Id}) updated by {_user.GetId()}");
            return await GetAsync(id, ct);
        }

        public async Task<bool> DeleteAsync(Guid id, CancellationToken ct)
        {
            if (id == _user.GetId())
            {
                throw new ForbiddenException("You cannot delete your own account");
            }

            var unitToDelete = await _context.Units.SingleOrDefaultAsync(v => v.Id == id, ct);

            if (unitToDelete == null)
                throw new EntityNotFoundException<Unit>();

            _context.Units.Remove(unitToDelete);
            await _context.SaveChangesAsync(ct);
            _logger.LogWarning($"Unit ({unitToDelete.Id}) deleted by {_user.GetId()}");
            return true;
        }

    }
}

