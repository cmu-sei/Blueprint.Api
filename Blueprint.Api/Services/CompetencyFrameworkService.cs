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
    public interface ICompetencyFrameworkService
    {
        Task<IEnumerable<ViewModels.CompetencyFramework>> GetAsync(CancellationToken ct);
        Task<ViewModels.CompetencyFramework> GetAsync(Guid id, CancellationToken ct);
        Task<ViewModels.CompetencyFramework> CreateAsync(ViewModels.CompetencyFramework competencyFramework, CancellationToken ct);
        Task<ViewModels.CompetencyFramework> UpdateAsync(Guid id, ViewModels.CompetencyFramework competencyFramework, CancellationToken ct);
        Task<bool> DeleteAsync(Guid id, CancellationToken ct);
    }

    public class CompetencyFrameworkService : ICompetencyFrameworkService
    {
        private readonly BlueprintContext _context;
        private readonly ClaimsPrincipal _user;
        private readonly IMapper _mapper;

        public CompetencyFrameworkService(BlueprintContext context, IPrincipal user, IMapper mapper)
        {
            _context = context;
            _user = user as ClaimsPrincipal;
            _mapper = mapper;
        }

        public async Task<IEnumerable<ViewModels.CompetencyFramework>> GetAsync(CancellationToken ct)
        {
            var items = await _context.CompetencyFrameworks
                .Include(x => x.ProficiencyScales)
                    .ThenInclude(x => x.ProficiencyLevels)
                .AsSplitQuery()
                .ToListAsync(ct);

            return _mapper.Map<IEnumerable<CompetencyFramework>>(items);
        }

        public async Task<ViewModels.CompetencyFramework> GetAsync(Guid id, CancellationToken ct)
        {
            var item = await _context.CompetencyFrameworks
                .Include(x => x.CompetencyElements)
                .Include(x => x.ProficiencyScales)
                    .ThenInclude(x => x.ProficiencyLevels)
                .AsSplitQuery()
                .SingleOrDefaultAsync(o => o.Id == id, ct);

            return _mapper.Map<CompetencyFramework>(item);
        }

        public async Task<ViewModels.CompetencyFramework> CreateAsync(ViewModels.CompetencyFramework competencyFramework, CancellationToken ct)
        {
            competencyFramework.Id = competencyFramework.Id != Guid.Empty ? competencyFramework.Id : Guid.NewGuid();
            competencyFramework.CreatedBy = _user.GetId();
            var competencyFrameworkEntity = _mapper.Map<CompetencyFrameworkEntity>(competencyFramework);

            _context.CompetencyFrameworks.Add(competencyFrameworkEntity);
            await _context.SaveChangesAsync(ct);

            return await GetAsync(competencyFrameworkEntity.Id, ct);
        }

        public async Task<ViewModels.CompetencyFramework> UpdateAsync(Guid id, ViewModels.CompetencyFramework competencyFramework, CancellationToken ct)
        {
            var competencyFrameworkToUpdate = await _context.CompetencyFrameworks.SingleOrDefaultAsync(v => v.Id == id, ct);

            if (competencyFrameworkToUpdate == null)
                throw new EntityNotFoundException<CompetencyFramework>();

            competencyFramework.ModifiedBy = _user.GetId();
            _mapper.Map(competencyFramework, competencyFrameworkToUpdate);

            _context.CompetencyFrameworks.Update(competencyFrameworkToUpdate);
            await _context.SaveChangesAsync(ct);

            return _mapper.Map(competencyFrameworkToUpdate, competencyFramework);
        }

        public async Task<bool> DeleteAsync(Guid id, CancellationToken ct)
        {
            var competencyFrameworkToDelete = await _context.CompetencyFrameworks.SingleOrDefaultAsync(v => v.Id == id, ct);

            if (competencyFrameworkToDelete == null)
                throw new EntityNotFoundException<CompetencyFramework>();

            _context.CompetencyFrameworks.Remove(competencyFrameworkToDelete);
            await _context.SaveChangesAsync(ct);

            return true;
        }
    }
}
