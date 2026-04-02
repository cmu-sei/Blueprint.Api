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
    public interface ICompetencyElementService
    {
        Task<IEnumerable<ViewModels.CompetencyElement>> GetByFrameworkAsync(Guid frameworkId, CancellationToken ct);
        Task<ViewModels.CompetencyElement> GetAsync(Guid id, CancellationToken ct);
        Task<ViewModels.CompetencyElement> CreateAsync(ViewModels.CompetencyElement competencyElement, CancellationToken ct);
        Task<ViewModels.CompetencyElement> UpdateAsync(Guid id, ViewModels.CompetencyElement competencyElement, CancellationToken ct);
        Task<bool> DeleteAsync(Guid id, CancellationToken ct);
    }

    public class CompetencyElementService : ICompetencyElementService
    {
        private readonly BlueprintContext _context;
        private readonly ClaimsPrincipal _user;
        private readonly IMapper _mapper;

        public CompetencyElementService(BlueprintContext context, IPrincipal user, IMapper mapper)
        {
            _context = context;
            _user = user as ClaimsPrincipal;
            _mapper = mapper;
        }

        public async Task<IEnumerable<ViewModels.CompetencyElement>> GetByFrameworkAsync(Guid frameworkId, CancellationToken ct)
        {
            var items = await _context.CompetencyElements
                .Where(x => x.CompetencyFrameworkId == frameworkId)
                .Include(x => x.Children)
                .ToListAsync(ct);

            return _mapper.Map<IEnumerable<CompetencyElement>>(items);
        }

        public async Task<ViewModels.CompetencyElement> GetAsync(Guid id, CancellationToken ct)
        {
            var item = await _context.CompetencyElements
                .Include(x => x.Children)
                .SingleOrDefaultAsync(o => o.Id == id, ct);

            return _mapper.Map<CompetencyElement>(item);
        }

        public async Task<ViewModels.CompetencyElement> CreateAsync(ViewModels.CompetencyElement competencyElement, CancellationToken ct)
        {
            competencyElement.Id = competencyElement.Id != Guid.Empty ? competencyElement.Id : Guid.NewGuid();
            competencyElement.CreatedBy = _user.GetId();
            var entity = _mapper.Map<CompetencyElementEntity>(competencyElement);

            _context.CompetencyElements.Add(entity);
            await _context.SaveChangesAsync(ct);

            return await GetAsync(entity.Id, ct);
        }

        public async Task<ViewModels.CompetencyElement> UpdateAsync(Guid id, ViewModels.CompetencyElement competencyElement, CancellationToken ct)
        {
            var entityToUpdate = await _context.CompetencyElements.SingleOrDefaultAsync(v => v.Id == id, ct);

            if (entityToUpdate == null)
                throw new EntityNotFoundException<CompetencyElement>();

            competencyElement.ModifiedBy = _user.GetId();
            _mapper.Map(competencyElement, entityToUpdate);

            _context.CompetencyElements.Update(entityToUpdate);
            await _context.SaveChangesAsync(ct);

            return _mapper.Map(entityToUpdate, competencyElement);
        }

        public async Task<bool> DeleteAsync(Guid id, CancellationToken ct)
        {
            var entityToDelete = await _context.CompetencyElements.SingleOrDefaultAsync(v => v.Id == id, ct);

            if (entityToDelete == null)
                throw new EntityNotFoundException<CompetencyElement>();

            _context.CompetencyElements.Remove(entityToDelete);
            await _context.SaveChangesAsync(ct);

            return true;
        }
    }
}
