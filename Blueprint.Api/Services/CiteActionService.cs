// Copyright 2023 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
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
        Task<IEnumerable<ViewModels.CiteAction>> UploadJsonAsync(FileForm form, CancellationToken ct);
        Task<Tuple<MemoryStream, string>> DownloadJsonAsync(IEnumerable<Guid> ids, CancellationToken ct);
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
                if (!hasMselPermission &&
                    !await MselOwnerRequirement.IsMet(_user.GetId(), citeAction.MselId, _context) &&
                    !await MselEditorRequirement.IsMet(_user.GetId(), citeAction.MselId, _context))
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
                if (!hasMselPermission &&
                    !await MselOwnerRequirement.IsMet(_user.GetId(), citeAction.MselId, _context) &&
                    !await MselEditorRequirement.IsMet(_user.GetId(), citeAction.MselId, _context))
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
                if (!hasMselPermission &&
                    !await MselOwnerRequirement.IsMet(_user.GetId(), citeActionToDelete.MselId, _context) &&
                    !await MselEditorRequirement.IsMet(_user.GetId(), citeActionToDelete.MselId, _context))
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

        public async Task<Tuple<MemoryStream, string>> DownloadJsonAsync(IEnumerable<Guid> ids, CancellationToken ct)
        {
            var idList = ids?.ToList() ?? new List<Guid>();
            var citeActionEntities = await _context.CiteActions
                .Where(ca => idList.Contains(ca.Id))
                .ToListAsync(ct);
            var citeActions = _mapper.Map<IEnumerable<ViewModels.CiteAction>>(citeActionEntities).ToList();
            // Strip team navigation; templates have no team and the property pulls in extra context.
            foreach (var ca in citeActions)
            {
                ca.Team = null;
            }

            var json = JsonSerializer.Serialize(citeActions, new JsonSerializerOptions
            {
                ReferenceHandler = ReferenceHandler.Preserve,
                WriteIndented = true,
            });
            var memoryStream = new MemoryStream(Encoding.UTF8.GetBytes(json));

            return Tuple.Create(memoryStream, "cite-action-templates.json");
        }

        public async Task<IEnumerable<ViewModels.CiteAction>> UploadJsonAsync(FileForm form, CancellationToken ct)
        {
            var uploadItem = form.ToUpload;
            string json;
            using (var reader = new StreamReader(uploadItem.OpenReadStream()))
            {
                json = await reader.ReadToEndAsync();
            }
            var options = new JsonSerializerOptions
            {
                ReferenceHandler = ReferenceHandler.Preserve,
                PropertyNameCaseInsensitive = true,
            };
            var incoming = JsonSerializer.Deserialize<List<ViewModels.CiteAction>>(json, options) ?? new List<ViewModels.CiteAction>();

            var created = new List<ViewModels.CiteAction>();
            var userId = _user.GetId();
            foreach (var item in incoming)
            {
                item.Id = Guid.NewGuid();
                item.IsTemplate = true;
                item.MselId = null;
                item.TeamId = null;
                item.Team = null;
                item.CreatedBy = userId;
                item.DateCreated = DateTime.UtcNow;
                item.ModifiedBy = null;
                item.DateModified = null;
                var entity = _mapper.Map<CiteActionEntity>(item);
                _context.CiteActions.Add(entity);
                created.Add(_mapper.Map<ViewModels.CiteAction>(entity));
            }
            await _context.SaveChangesAsync(ct);

            return created;
        }

    }
}

