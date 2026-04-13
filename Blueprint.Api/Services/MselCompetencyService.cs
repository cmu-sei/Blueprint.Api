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
    public interface IMselCompetencyService
    {
        Task<IEnumerable<ViewModels.MselCompetency>> GetByMselAsync(Guid mselId, bool hasSystemPermission, CancellationToken ct);
        Task<ViewModels.MselCompetency> GetAsync(Guid id, bool hasSystemPermission, CancellationToken ct);
        Task<ViewModels.MselCompetency> CreateAsync(ViewModels.MselCompetency mselCompetency, bool hasSystemPermission, CancellationToken ct);
        Task<bool> DeleteAsync(Guid id, bool hasSystemPermission, CancellationToken ct);
        Task<bool> DeleteByIdsAsync(Guid mselId, Guid competencyId, bool hasSystemPermission, CancellationToken ct);
    }

    public class MselCompetencyService : IMselCompetencyService
    {
        private readonly BlueprintContext _context;
        private readonly ClaimsPrincipal _user;
        private readonly IMapper _mapper;
        private readonly ILogger<IMselCompetencyService> _logger;

        public MselCompetencyService(BlueprintContext context, IPrincipal user, ILogger<IMselCompetencyService> logger, IMapper mapper)
        {
            _context = context;
            _user = user as ClaimsPrincipal;
            _mapper = mapper;
            _logger = logger;
        }

        public async Task<IEnumerable<ViewModels.MselCompetency>> GetByMselAsync(Guid mselId, bool hasSystemPermission, CancellationToken ct)
        {
            var msel = await _context.Msels.SingleOrDefaultAsync(v => v.Id == mselId, ct);
            if (msel == null)
                throw new EntityNotFoundException<MselEntity>();

            if (!hasSystemPermission && !(await MselViewRequirement.IsMet(_user.GetId(), msel.Id, _context)))
                throw new ForbiddenException();

            var items = await _context.MselCompetencies
                .Where(mc => mc.MselId == mselId)
                .Include(mc => mc.Competency)
                    .ThenInclude(c => c.Relationships)
                .Include(mc => mc.Competency)
                    .ThenInclude(c => c.InverseRelationships)
                .AsSplitQuery()
                .ToListAsync(ct);

            var result = _mapper.Map<IEnumerable<MselCompetency>>(items).ToList();

            // Populate RelatedIdNumbers on each competency
            var idNumberMap = items
                .Where(mc => mc.Competency?.IdNumber != null)
                .ToDictionary(mc => mc.CompetencyId, mc => mc.Competency.IdNumber);

            foreach (var mc in result)
            {
                var entity = items.First(i => i.Id == mc.Id);
                var outbound = (entity.Competency.Relationships ?? new HashSet<CompetencyRelationshipEntity>())
                    .Select(r => idNumberMap.GetValueOrDefault(r.RelatedCompetencyId))
                    .Where(n => n != null);
                var inverse = (entity.Competency.InverseRelationships ?? new HashSet<CompetencyRelationshipEntity>())
                    .Select(r => idNumberMap.GetValueOrDefault(r.CompetencyId))
                    .Where(n => n != null);
                mc.Competency.RelatedIdNumbers = outbound.Union(inverse).Distinct().ToList();
            }

            return result;
        }

        public async Task<ViewModels.MselCompetency> GetAsync(Guid id, bool hasSystemPermission, CancellationToken ct)
        {
            var item = await _context.MselCompetencies
                .Include(mc => mc.Competency)
                    .ThenInclude(c => c.Relationships)
                .Include(mc => mc.Competency)
                    .ThenInclude(c => c.InverseRelationships)
                .AsSplitQuery()
                .SingleOrDefaultAsync(mc => mc.Id == id, ct);

            if (item == null)
                throw new EntityNotFoundException<MselCompetency>();

            if (!hasSystemPermission && !(await MselViewRequirement.IsMet(_user.GetId(), item.MselId, _context)))
                throw new ForbiddenException();

            var result = _mapper.Map<MselCompetency>(item);

            // Populate RelatedIdNumbers — resolve IDs within same MSEL pool
            var poolItems = await _context.MselCompetencies
                .Where(mc => mc.MselId == item.MselId)
                .Include(mc => mc.Competency)
                .ToListAsync(ct);
            var idNumberMap = poolItems
                .Where(mc => mc.Competency?.IdNumber != null)
                .ToDictionary(mc => mc.CompetencyId, mc => mc.Competency.IdNumber);

            var outbound = (item.Competency.Relationships ?? new HashSet<CompetencyRelationshipEntity>())
                .Select(r => idNumberMap.GetValueOrDefault(r.RelatedCompetencyId))
                .Where(n => n != null);
            var inverse = (item.Competency.InverseRelationships ?? new HashSet<CompetencyRelationshipEntity>())
                .Select(r => idNumberMap.GetValueOrDefault(r.CompetencyId))
                .Where(n => n != null);
            result.Competency.RelatedIdNumbers = outbound.Union(inverse).Distinct().ToList();

            return result;
        }

        public async Task<ViewModels.MselCompetency> CreateAsync(ViewModels.MselCompetency mselCompetency, bool hasSystemPermission, CancellationToken ct)
        {
            var msel = await _context.Msels.SingleOrDefaultAsync(v => v.Id == mselCompetency.MselId, ct);
            if (msel == null)
                throw new EntityNotFoundException<MselEntity>();

            var competency = await _context.Competencies.SingleOrDefaultAsync(v => v.Id == mselCompetency.CompetencyId, ct);
            if (competency == null)
                throw new EntityNotFoundException<CompetencyEntity>();

            if (!hasSystemPermission && !(await MselOwnerRequirement.IsMet(_user.GetId(), msel.Id, _context)))
                throw new ForbiddenException();

            if (await _context.MselCompetencies.AnyAsync(mc => mc.CompetencyId == competency.Id && mc.MselId == msel.Id, ct))
                throw new ArgumentException("MSEL Competency already exists.");

            var entity = _mapper.Map<MselCompetencyEntity>(mselCompetency);
            entity.Id = entity.Id != Guid.Empty ? entity.Id : Guid.NewGuid();

            _context.MselCompetencies.Add(entity);
            await _context.SaveChangesAsync(ct);
            _logger.LogWarning($"Competency {mselCompetency.CompetencyId} added to MSEL {mselCompetency.MselId} by {_user.GetId()}");
            return await GetAsync(entity.Id, true, ct);
        }

        public async Task<bool> DeleteAsync(Guid id, bool hasSystemPermission, CancellationToken ct)
        {
            var item = await _context.MselCompetencies.SingleOrDefaultAsync(v => v.Id == id, ct);
            if (item == null)
                throw new EntityNotFoundException<MselCompetency>();

            if (!hasSystemPermission && !(await MselOwnerRequirement.IsMet(_user.GetId(), item.MselId, _context)))
                throw new ForbiddenException();

            _context.MselCompetencies.Remove(item);
            await _context.SaveChangesAsync(ct);
            _logger.LogWarning($"Competency {item.CompetencyId} removed from MSEL {item.MselId} by {_user.GetId()}");
            return true;
        }

        public async Task<bool> DeleteByIdsAsync(Guid mselId, Guid competencyId, bool hasSystemPermission, CancellationToken ct)
        {
            var item = await _context.MselCompetencies.SingleOrDefaultAsync(v => v.MselId == mselId && v.CompetencyId == competencyId, ct);
            if (item == null)
                throw new EntityNotFoundException<MselCompetency>();

            if (!hasSystemPermission && !(await MselOwnerRequirement.IsMet(_user.GetId(), item.MselId, _context)))
                throw new ForbiddenException();

            _context.MselCompetencies.Remove(item);
            await _context.SaveChangesAsync(ct);
            _logger.LogWarning($"Competency {item.CompetencyId} removed from MSEL {item.MselId} by {_user.GetId()}");
            return true;
        }
    }
}
