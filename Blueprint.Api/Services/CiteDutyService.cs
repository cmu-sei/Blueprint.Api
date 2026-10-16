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
        Task<ViewModels.CiteDuty> CreateAsync(ViewModels.CiteDutyCreate citeDuty, bool hasMselPermission, bool hasCiteDutyPermission, CancellationToken ct);
        Task<ViewModels.CiteDuty> UpdateAsync(Guid id, ViewModels.CiteDutyEdit citeDuty, bool hasMselPermission, bool hasCiteDutyPermission, CancellationToken ct);
        Task<bool> DeleteAsync(Guid id, bool hasMselPermission, bool hasCiteDutyPermission, CancellationToken ct);
        Task<IEnumerable<ViewModels.CiteDuty>> UploadJsonAsync(FileForm form, CancellationToken ct);
        Task<Tuple<MemoryStream, string>> DownloadJsonAsync(IEnumerable<Guid> ids, CancellationToken ct);
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
                .Where(citeDuty => citeDuty.IsTemplate && citeDuty.MselId == null)
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

        public async Task<ViewModels.CiteDuty> CreateAsync(ViewModels.CiteDutyCreate citeDuty, bool hasMselPermission, bool hasCiteDutyPermission, CancellationToken ct)
        {
            // a template belongs to no MSEL, so a MSEL's CITE duty cannot be listed as one
            if (citeDuty.MselId.HasValue && citeDuty.IsTemplate)
                throw new ConflictException("A CITE duty cannot belong to a MSEL and be a template.");

            if (citeDuty.MselId.HasValue)
            {
                if (!hasMselPermission &&
                    !await MselOwnerRequirement.IsMet(_user.GetId(), citeDuty.MselId, _context) &&
                    !await MselEditorRequirement.IsMet(_user.GetId(), citeDuty.MselId, _context))
                    throw new ForbiddenException();
            }
            else
            {
                if (!hasCiteDutyPermission)
                    throw new ForbiddenException();
            }

            // a CITE team must be one of the MSEL's own teams, and a template names no team at all
            if (citeDuty.TeamId.HasValue)
                await ServiceUtilities.EnsureTeamIsInMselAsync(citeDuty.TeamId.Value, citeDuty.MselId, _context, ct);

            var citeDutyEntity = _mapper.Map<CiteDutyEntity>(citeDuty);
            citeDutyEntity.Id = citeDuty.Id != Guid.Empty ? citeDuty.Id : Guid.NewGuid();
            citeDutyEntity.CreatedBy = _user.GetId();

            _context.CiteDuties.Add(citeDutyEntity);
            await _context.SaveChangesAsync(ct);
            var created = await GetAsync(citeDutyEntity.Id, true, ct);

            return created;
        }

        public async Task<ViewModels.CiteDuty> UpdateAsync(Guid id, ViewModels.CiteDutyEdit citeDuty, bool hasMselPermission, bool hasCiteDutyPermission, CancellationToken ct)
        {
            var citeDutyToUpdate = await _context.CiteDuties.SingleOrDefaultAsync(v => v.Id == id, ct);

            if (citeDutyToUpdate == null)
                throw new EntityNotFoundException<CiteDuty>();

            // decided on the stored row: a MSEL's CITE duty needs the MSEL permission or an owner or editor
            // of that MSEL, and a template needs the template permission
            if (citeDutyToUpdate.MselId.HasValue)
            {
                if (!hasMselPermission &&
                    !await MselOwnerRequirement.IsMet(_user.GetId(), citeDutyToUpdate.MselId, _context) &&
                    !await MselEditorRequirement.IsMet(_user.GetId(), citeDutyToUpdate.MselId, _context))
                    throw new ForbiddenException();
            }
            else
            {
                if (!hasCiteDutyPermission)
                    throw new ForbiddenException();
            }

            // a CITE team must be one of the MSEL's own teams, and a template names no team at all
            if (citeDuty.TeamId.HasValue)
                await ServiceUtilities.EnsureTeamIsInMselAsync(citeDuty.TeamId.Value, citeDutyToUpdate.MselId, _context, ct);

            _mapper.Map(citeDuty, citeDutyToUpdate);
            citeDutyToUpdate.ModifiedBy = _user.GetId();

            _context.CiteDuties.Update(citeDutyToUpdate);
            await _context.SaveChangesAsync(ct);

            var updated = await GetAsync(citeDutyToUpdate.Id, true, ct);

            return updated;
        }

        public async Task<bool> DeleteAsync(Guid id, bool hasMselPermission, bool hasCiteDutyPermission, CancellationToken ct)
        {
            var citeDutyToDelete = await _context.CiteDuties.SingleOrDefaultAsync(v => v.Id == id, ct);

            if (citeDutyToDelete == null)
                throw new EntityNotFoundException<CiteDuty>();

            if (citeDutyToDelete.MselId.HasValue)
            {
                if (!hasMselPermission &&
                    !await MselOwnerRequirement.IsMet(_user.GetId(), citeDutyToDelete.MselId, _context) &&
                    !await MselEditorRequirement.IsMet(_user.GetId(), citeDutyToDelete.MselId, _context))
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

        public async Task<Tuple<MemoryStream, string>> DownloadJsonAsync(IEnumerable<Guid> ids, CancellationToken ct)
        {
            var idList = ids?.ToList() ?? new List<Guid>();
            // only templates are exported: a MSEL's own rows are read through the MSEL
            var citeDutyEntities = await _context.CiteDuties
                .Where(cd => idList.Contains(cd.Id) && cd.MselId == null)
                .ToListAsync(ct);
            var citeDuties = _mapper.Map<IEnumerable<ViewModels.CiteDuty>>(citeDutyEntities).ToList();
            // Strip team navigation; templates have no team and the property pulls in extra context.
            foreach (var cd in citeDuties)
            {
                cd.Team = null;
            }

            var json = JsonSerializer.Serialize(citeDuties, new JsonSerializerOptions
            {
                ReferenceHandler = ReferenceHandler.Preserve,
                WriteIndented = true,
            });
            var memoryStream = new MemoryStream(Encoding.UTF8.GetBytes(json));

            return Tuple.Create(memoryStream, "cite-duty-templates.json");
        }

        public async Task<IEnumerable<ViewModels.CiteDuty>> UploadJsonAsync(FileForm form, CancellationToken ct)
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
            var incoming = JsonSerializer.Deserialize<List<ViewModels.CiteDuty>>(json, options) ?? new List<ViewModels.CiteDuty>();

            var created = new List<ViewModels.CiteDuty>();
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
                var entity = _mapper.Map<CiteDutyEntity>(item);
                _context.CiteDuties.Add(entity);
                created.Add(_mapper.Map<ViewModels.CiteDuty>(entity));
            }
            await _context.SaveChangesAsync(ct);

            return created;
        }

    }
}
