// Copyright 2023 Carnegie Mellon University. All Rights Reserved.
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
    public interface ICiteDutyService
    {
        Task<IEnumerable<ViewModels.CiteDuty>> GetTemplatesAsync(CancellationToken ct);
        Task<IEnumerable<ViewModels.CiteDuty>> GetByMselAsync(Guid mselId, bool hasSystemPermission, CancellationToken ct);
        Task<ViewModels.CiteDuty> GetAsync(Guid id, bool hasSystemPermission, CancellationToken ct);
        Task<ViewModels.CiteDuty> CreateAsync(ViewModels.CiteDuty citeDuty, bool hasMselPermission, bool hasCiteDutyPermission, CancellationToken ct);
        Task<ViewModels.CiteDuty> UpdateAsync(Guid id, ViewModels.CiteDuty citeDuty, bool hasMselPermission, bool hasCiteDutyPermission, CancellationToken ct);
        Task<bool> DeleteAsync(Guid id, bool hasMselPermission, bool hasCiteDutyPermission, CancellationToken ct);
    }

    public class CiteDutyService : ICiteDutyService
    {
        private readonly BlueprintContext _context;
        private readonly ClaimsPrincipal _user;
        private readonly IMapper _mapper;

        public CiteDutyService(
            BlueprintContext context,
            IPrincipal user,
            IMapper mapper)
        {
            _context = context;
            _user = user as ClaimsPrincipal;
            _mapper = mapper;
        }

        public async Task<IEnumerable<ViewModels.CiteDuty>> GetTemplatesAsync(CancellationToken ct)
        {
            var citeDutyEntities = await _context.CiteDuties
                .Where(citeDuty => citeDuty.IsTemplate)
                .ToListAsync(ct);

            return _mapper.Map<IEnumerable<CiteDuty>>(citeDutyEntities).ToList();;
        }

        public async Task<IEnumerable<ViewModels.CiteDuty>> GetByMselAsync(Guid mselId, bool hasSystemPermission, CancellationToken ct)
        {
            if (!hasSystemPermission && !(await MselViewRequirement.IsMet(_user.GetId(), mselId, _context)))
            {
                var msel = await _context.Msels.FindAsync(mselId);
                if (!msel.IsTemplate)
                    throw new ForbiddenException();
            }

            var citeDutyEntities = await _context.CiteDuties
                .Where(cr => cr.MselId == mselId)
                .Include(cr => cr.Team)
                .ToListAsync(ct);

            return _mapper.Map<IEnumerable<CiteDuty>>(citeDutyEntities).ToList();;
        }

        public async Task<ViewModels.CiteDuty> GetAsync(Guid id, bool hasSystemPermission, CancellationToken ct)
        {
            var item = await _context.CiteDuties
                .Include(cr => cr.Team)
                .SingleAsync(cr => cr.Id == id, ct);

            if (item == null)
                throw new EntityNotFoundException<DataValueEntity>("DataValue not found: " + id);

            // Templates (null MselId) can be viewed by anyone
            if (item.MselId.HasValue)
            {
                if (!hasSystemPermission && !await MselViewRequirement.IsMet(_user.GetId(), item.MselId, _context))
                    throw new ForbiddenException();
            }

            return _mapper.Map<CiteDuty>(item);
        }

        public async Task<ViewModels.CiteDuty> CreateAsync(ViewModels.CiteDuty citeDuty, bool hasMselPermission, bool hasCiteDutyPermission, CancellationToken ct)
        {
            if (citeDuty.MselId.HasValue)
            {
                if (!hasMselPermission && !await MselEditorRequirement.IsMet(_user.GetId(), citeDuty.MselId, _context))
                    throw new ForbiddenException();
            }
            else
            {
                if (!hasCiteDutyPermission)
                    throw new ForbiddenException();
            }
            citeDuty.Id = citeDuty.Id != Guid.Empty ? citeDuty.Id : Guid.NewGuid();
            citeDuty.CreatedBy = _user.GetId();
            var citeDutyEntity = _mapper.Map<CiteDutyEntity>(citeDuty);

            _context.CiteDuties.Add(citeDutyEntity);
            await _context.SaveChangesAsync(ct);
            citeDuty = await GetAsync(citeDutyEntity.Id, true, ct);

            return citeDuty;
        }

        public async Task<ViewModels.CiteDuty> UpdateAsync(Guid id, ViewModels.CiteDuty citeDuty, bool hasMselPermission, bool hasCiteDutyPermission, CancellationToken ct)
        {
            if (citeDuty.MselId.HasValue)
            {
                if (!hasMselPermission && !await MselEditorRequirement.IsMet(_user.GetId(), citeDuty.MselId, _context))
                    throw new ForbiddenException();
            }
            else
            {
                if (!hasCiteDutyPermission)
                    throw new ForbiddenException();
            }

            var citeDutyToUpdate = await _context.CiteDuties.SingleOrDefaultAsync(v => v.Id == id, ct);

            if (citeDutyToUpdate == null)
                throw new EntityNotFoundException<CiteDuty>();

            citeDuty.ModifiedBy = _user.GetId();
            _mapper.Map(citeDuty, citeDutyToUpdate);

            _context.CiteDuties.Update(citeDutyToUpdate);
            await _context.SaveChangesAsync(ct);

            citeDuty = await GetAsync(citeDutyToUpdate.Id, true, ct);

            return citeDuty;
        }

        public async Task<bool> DeleteAsync(Guid id, bool hasMselPermission, bool hasCiteDutyPermission, CancellationToken ct)
        {
            var citeDutyToDelete = await _context.CiteDuties.SingleOrDefaultAsync(v => v.Id == id, ct);

            if (citeDutyToDelete == null)
                throw new EntityNotFoundException<CiteDuty>();

            if (citeDutyToDelete.MselId.HasValue)
            {
                if (!hasMselPermission && !await MselEditorRequirement.IsMet(_user.GetId(), citeDutyToDelete.MselId, _context))
                    throw new ForbiddenException();
            }
            else
            {
                if (!hasCiteDutyPermission)
                    throw new ForbiddenException();
            }

            _context.CiteDuties.Remove(citeDutyToDelete);
            await _context.SaveChangesAsync(ct);

            return true;
        }

    }
}
