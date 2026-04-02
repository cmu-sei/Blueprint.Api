// Copyright 2025 Carnegie Mellon University. All Rights Reserved.
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
using Blueprint.Api.Infrastructure.Extensions;
using Blueprint.Api.Infrastructure.Exceptions;
using Blueprint.Api.ViewModels;

namespace Blueprint.Api.Services
{
    public interface IProficiencyLevelService
    {
        Task<IEnumerable<ViewModels.ProficiencyLevel>> GetByScaleAsync(Guid scaleId, CancellationToken ct);
        Task<ViewModels.ProficiencyLevel> GetAsync(Guid id, CancellationToken ct);
        Task<ViewModels.ProficiencyLevel> CreateAsync(ViewModels.ProficiencyLevel proficiencyLevel, CancellationToken ct);
        Task<ViewModels.ProficiencyLevel> UpdateAsync(Guid id, ViewModels.ProficiencyLevel proficiencyLevel, CancellationToken ct);
        Task<bool> DeleteAsync(Guid id, CancellationToken ct);
    }

    public class ProficiencyLevelService : IProficiencyLevelService
    {
        private readonly BlueprintContext _context;
        private readonly ClaimsPrincipal _user;
        private readonly IMapper _mapper;

        public ProficiencyLevelService(BlueprintContext context, IPrincipal user, IMapper mapper)
        {
            _context = context;
            _user = user as ClaimsPrincipal;
            _mapper = mapper;
        }

        public async Task<IEnumerable<ViewModels.ProficiencyLevel>> GetByScaleAsync(Guid scaleId, CancellationToken ct)
        {
            var items = await _context.ProficiencyLevels
                .Where(x => x.ProficiencyScaleId == scaleId)
                .OrderBy(x => x.DisplayOrder)
                .ToListAsync(ct);

            return _mapper.Map<IEnumerable<ProficiencyLevel>>(items);
        }

        public async Task<ViewModels.ProficiencyLevel> GetAsync(Guid id, CancellationToken ct)
        {
            var item = await _context.ProficiencyLevels
                .SingleOrDefaultAsync(o => o.Id == id, ct);

            return _mapper.Map<ProficiencyLevel>(item);
        }

        public async Task<ViewModels.ProficiencyLevel> CreateAsync(ViewModels.ProficiencyLevel proficiencyLevel, CancellationToken ct)
        {
            proficiencyLevel.Id = proficiencyLevel.Id != Guid.Empty ? proficiencyLevel.Id : Guid.NewGuid();
            proficiencyLevel.CreatedBy = _user.GetId();
            var entity = _mapper.Map<ProficiencyLevelEntity>(proficiencyLevel);

            _context.ProficiencyLevels.Add(entity);
            await _context.SaveChangesAsync(ct);

            return await GetAsync(entity.Id, ct);
        }

        public async Task<ViewModels.ProficiencyLevel> UpdateAsync(Guid id, ViewModels.ProficiencyLevel proficiencyLevel, CancellationToken ct)
        {
            var entityToUpdate = await _context.ProficiencyLevels.SingleOrDefaultAsync(v => v.Id == id, ct);

            if (entityToUpdate == null)
                throw new EntityNotFoundException<ProficiencyLevel>();

            proficiencyLevel.ModifiedBy = _user.GetId();
            _mapper.Map(proficiencyLevel, entityToUpdate);

            _context.ProficiencyLevels.Update(entityToUpdate);
            await _context.SaveChangesAsync(ct);

            return _mapper.Map(entityToUpdate, proficiencyLevel);
        }

        public async Task<bool> DeleteAsync(Guid id, CancellationToken ct)
        {
            var entityToDelete = await _context.ProficiencyLevels.SingleOrDefaultAsync(v => v.Id == id, ct);

            if (entityToDelete == null)
                throw new EntityNotFoundException<ProficiencyLevel>();

            _context.ProficiencyLevels.Remove(entityToDelete);
            await _context.SaveChangesAsync(ct);

            return true;
        }
    }
}
