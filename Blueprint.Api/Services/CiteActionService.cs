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
using Blueprint.Api.Data.Models;
using Blueprint.Api.Infrastructure.Authorization;
using Blueprint.Api.Infrastructure.Exceptions;
using Blueprint.Api.Infrastructure.Extensions;
using Blueprint.Api.ViewModels;

namespace Blueprint.Api.Services
{
    public interface ICiteActionService
    {
        Task<IEnumerable<ViewModels.CiteAction>> GetTemplatesAsync(CancellationToken ct);
        Task<IEnumerable<ViewModels.CiteAction>> GetByMselAsync(Guid mselId, bool hasSystemPermission, CancellationToken ct);
        Task<ViewModels.CiteAction> GetAsync(Guid id, bool hasSystemPermission, CancellationToken ct);
        Task<ViewModels.CiteAction> CreateAsync(ViewModels.CiteAction citeAction, bool hasMselPermission, bool hasCiteActionPermission, CancellationToken ct);
        Task<ViewModels.CiteAction> UpdateAsync(Guid id, ViewModels.CiteAction citeAction, bool hasMselPermission, bool hasCiteActionPermission, CancellationToken ct);
        Task<bool> DeleteAsync(Guid id, bool hasMselPermission, bool hasCiteActionPermission, CancellationToken ct);
    }

    public class CiteActionService : ICiteActionService
    {
        private readonly BlueprintContext _context;
        private readonly ClaimsPrincipal _user;
        private readonly IMapper _mapper;

        public CiteActionService(
            BlueprintContext context,
            IPrincipal user,
            IMapper mapper)
        {
            _context = context;
            _user = user as ClaimsPrincipal;
            _mapper = mapper;
        }

        public async Task<IEnumerable<ViewModels.CiteAction>> GetTemplatesAsync(CancellationToken ct)
        {
            var citeActionEntities = await _context.CiteActions
                .Where(citeAction => citeAction.IsTemplate)
                .ToListAsync(ct);

            return _mapper.Map<IEnumerable<CiteAction>>(citeActionEntities).ToList();;
        }

        public async Task<IEnumerable<ViewModels.CiteAction>> GetByMselAsync(Guid mselId, bool hasSystemPermission, CancellationToken ct)
        {
            if (!hasSystemPermission && !(await MselViewRequirement.IsMet(_user.GetId(), mselId, _context)))
            {
                var msel = await _context.Msels.FindAsync(mselId);
                if (!msel.IsTemplate)
                    throw new ForbiddenException();
            }

            var citeActionEntities = await _context.CiteActions
                .Where(ca => ca.MselId == mselId)
                .Include(ca => ca.Team)
                .ToListAsync(ct);

            return _mapper.Map<IEnumerable<CiteAction>>(citeActionEntities).ToList();;
        }

        public async Task<ViewModels.CiteAction> GetAsync(Guid id, bool hasSystemPermission, CancellationToken ct)
        {
            var item = await _context.CiteActions
                .Include(ca => ca.Team)
                .SingleAsync(ca => ca.Id == id, ct);

            if (item == null)
                throw new EntityNotFoundException<DataValueEntity>("DataValue not found: " + id);

            // Templates (null MselId) can be viewed by anyone
            if (item.MselId.HasValue)
            {
                if (!hasSystemPermission && !await MselViewRequirement.IsMet(_user.GetId(), item.MselId, _context))
                    throw new ForbiddenException();
            }

            return _mapper.Map<CiteAction>(item);
        }

        public async Task<ViewModels.CiteAction> CreateAsync(ViewModels.CiteAction citeAction, bool hasMselPermission, bool hasCiteActionPermission, CancellationToken ct)
        {
            if (citeAction.MselId.HasValue)
            {
                if (!hasMselPermission && !await MselEditorRequirement.IsMet(_user.GetId(), citeAction.MselId, _context))
                    throw new ForbiddenException();
            }
            else
            {
                if (!hasCiteActionPermission)
                    throw new ForbiddenException();
            }
            citeAction.Id = citeAction.Id != Guid.Empty ? citeAction.Id : Guid.NewGuid();
            citeAction.CreatedBy = _user.GetId();
            var citeActionEntity = _mapper.Map<CiteActionEntity>(citeAction);

            _context.CiteActions.Add(citeActionEntity);
            await _context.SaveChangesAsync(ct);
            citeAction = await GetAsync(citeActionEntity.Id, true, ct);

            return citeAction;
        }

        public async Task<ViewModels.CiteAction> UpdateAsync(Guid id, ViewModels.CiteAction citeAction, bool hasMselPermission, bool hasCiteActionPermission, CancellationToken ct)
        {
            if (citeAction.MselId.HasValue)
            {
                if (!hasMselPermission && !await MselEditorRequirement.IsMet(_user.GetId(), citeAction.MselId, _context))
                    throw new ForbiddenException();
            }
            else
            {
                if (!hasCiteActionPermission)
                    throw new ForbiddenException();
            }

            var citeActionToUpdate = await _context.CiteActions.SingleOrDefaultAsync(v => v.Id == id, ct);

            if (citeActionToUpdate == null)
                throw new EntityNotFoundException<CiteAction>();

            citeAction.ModifiedBy = _user.GetId();
            _mapper.Map(citeAction, citeActionToUpdate);

            _context.CiteActions.Update(citeActionToUpdate);
            await _context.SaveChangesAsync(ct);

            citeAction = await GetAsync(citeActionToUpdate.Id, true, ct);

            return citeAction;
        }

        public async Task<bool> DeleteAsync(Guid id, bool hasMselPermission, bool hasCiteActionPermission, CancellationToken ct)
        {
            var citeActionToDelete = await _context.CiteActions.SingleOrDefaultAsync(v => v.Id == id, ct);

            if (citeActionToDelete == null)
                throw new EntityNotFoundException<CiteAction>();

            if (citeActionToDelete.MselId.HasValue)
            {
                if (!hasMselPermission && !await MselEditorRequirement.IsMet(_user.GetId(), citeActionToDelete.MselId, _context))
                    throw new ForbiddenException();
            }
            else
            {
                if (!hasCiteActionPermission)
                    throw new ForbiddenException();
            }

            _context.CiteActions.Remove(citeActionToDelete);
            await _context.SaveChangesAsync(ct);

            return true;
        }

    }
}

