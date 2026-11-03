// Copyright 2024 Carnegie Mellon University. All Rights Reserved.
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
    public interface IPlayerApplicationService
    {
        Task<IEnumerable<ViewModels.PlayerApplication>> GetTemplatesAsync(CancellationToken ct);
        Task<IEnumerable<ViewModels.PlayerApplication>> GetByMselAsync(Guid mselId, bool hasSystemPermission, CancellationToken ct);
        Task<ViewModels.PlayerApplication> GetAsync(Guid id, bool hasSystemPermission, CancellationToken ct);
        Task<ViewModels.PlayerApplication> CreateAsync(ViewModels.PlayerApplicationCreate playerApplication, bool hasSystemPermission, bool hasPlayerApplicationPermission, CancellationToken ct);
        Task<ViewModels.PlayerApplication> CreateAndPushAsync(ViewModels.PlayerApplicationCreate playerApplication, bool hasSystemPermission, CancellationToken ct);
        Task<ViewModels.PlayerApplication> UpdateAsync(Guid id, ViewModels.PlayerApplicationEdit playerApplication, bool hasSystemPermission, bool hasPlayerApplicationPermission, CancellationToken ct);
        Task<bool> DeleteAsync(Guid id, bool hasSystemPermission, bool hasPlayerApplicationPermission, CancellationToken ct);
        Task<IEnumerable<ViewModels.PlayerApplication>> UploadJsonAsync(FileForm form, CancellationToken ct);
        Task<Tuple<MemoryStream, string>> DownloadJsonAsync(IEnumerable<Guid> ids, CancellationToken ct);
    }

    public class PlayerApplicationService : IPlayerApplicationService
    {
        private readonly BlueprintContext _context;
        private readonly IPlayerService _playerService;
        private readonly ClaimsPrincipal _user;
        private readonly IMapper _mapper;

        public PlayerApplicationService(
            BlueprintContext context,
            IPlayerService playerService,
            IPrincipal user,
            IMapper mapper)
        {
            _context = context;
            _playerService = playerService;
            _user = user as ClaimsPrincipal;
            _mapper = mapper;
        }

        public async Task<IEnumerable<ViewModels.PlayerApplication>> GetTemplatesAsync(CancellationToken ct)
        {
            var playerApplicationEntities = await _context.PlayerApplications
                .Where(playerApplication => playerApplication.IsTemplate && playerApplication.MselId == null)
                .ToListAsync(ct);

            return _mapper.Map<IEnumerable<PlayerApplication>>(playerApplicationEntities).ToList();
        }

        public async Task<IEnumerable<ViewModels.PlayerApplication>> GetByMselAsync(Guid mselId, bool hasSystemPermission, CancellationToken ct)
        {
            if (!hasSystemPermission && !(await MselViewRequirement.IsMet(_user.GetId(), mselId, _context)))
            {
                var msel = await _context.Msels.FindAsync(mselId);
                if (!msel.IsTemplate)
                    throw new ForbiddenException();
            }

            var playerApplicationEntities = await _context.PlayerApplications
                .Where(playerApplication => playerApplication.MselId == mselId)
                .ToListAsync(ct);

            return _mapper.Map<IEnumerable<PlayerApplication>>(playerApplicationEntities).ToList();;
        }

        public async Task<ViewModels.PlayerApplication> GetAsync(Guid id, bool hasSystemPermission, CancellationToken ct)
        {
            var item = await _context.PlayerApplications.SingleAsync(playerApplication => playerApplication.Id == id, ct);

            if (item == null)
                throw new EntityNotFoundException<DataValueEntity>("DataValue not found: " + id);

            // Templates (null MselId) can be viewed by anyone
            if (item.MselId.HasValue)
            {
                if (!hasSystemPermission && !await MselUserRequirement.IsMet(_user.GetId(), item.MselId, _context))
                    throw new ForbiddenException();
            }

            return _mapper.Map<PlayerApplication>(item);
        }

        public async Task<ViewModels.PlayerApplication> CreateAsync(ViewModels.PlayerApplicationCreate playerApplication, bool hasSystemPermission, bool hasPlayerApplicationPermission, CancellationToken ct)
        {
            // a template belongs to no MSEL, so a MSEL's application cannot be listed as one
            if (playerApplication.MselId.HasValue && playerApplication.IsTemplate)
                throw new ConflictException("A Player application cannot belong to a MSEL and be a template.");

            if (playerApplication.MselId.HasValue)
            {
                if (!hasSystemPermission && !await MselOwnerRequirement.IsMet(_user.GetId(), playerApplication.MselId, _context))
                    throw new ForbiddenException();
            }
            else
            {
                if (!hasPlayerApplicationPermission)
                    throw new ForbiddenException();
                // an application with no MSEL can only be a template
                playerApplication.IsTemplate = true;
            }

            var playerApplicationEntity = _mapper.Map<PlayerApplicationEntity>(playerApplication);
            playerApplicationEntity.Id = playerApplication.Id != Guid.Empty ? playerApplication.Id : Guid.NewGuid();
            playerApplicationEntity.CreatedBy = _user.GetId();

            _context.PlayerApplications.Add(playerApplicationEntity);
            await _context.SaveChangesAsync(ct);

            return await GetAsync(playerApplicationEntity.Id, true, ct);
        }

        public async Task<ViewModels.PlayerApplication> CreateAndPushAsync(ViewModels.PlayerApplicationCreate playerApplication, bool hasSystemPermission, CancellationToken ct)
        {
            // a template has no MSEL, so there is no Player view to push it to
            if (!playerApplication.MselId.HasValue || playerApplication.IsTemplate)
                throw new ConflictException("A Player application template cannot be pushed to Player.");
            // authorization will be checked in CreateAsync
            var item = await CreateAsync(playerApplication, hasSystemPermission, false, ct);
            // push the application to Player
            await _playerService.PushApplication(item, ct);

            return item;
        }

        public async Task<ViewModels.PlayerApplication> UpdateAsync(Guid id, ViewModels.PlayerApplicationEdit playerApplication, bool hasSystemPermission, bool hasPlayerApplicationPermission, CancellationToken ct)
        {
            var playerApplicationToUpdate = await _context.PlayerApplications.SingleOrDefaultAsync(v => v.Id == id, ct);

            if (playerApplicationToUpdate == null)
                throw new EntityNotFoundException<PlayerApplication>();

            // decided on the stored row: a MSEL's application needs the MSEL permission or an owner of that
            // MSEL, and a template needs the template permission
            if (playerApplicationToUpdate.MselId.HasValue)
            {
                if (!hasSystemPermission && !await MselOwnerRequirement.IsMet(_user.GetId(), playerApplicationToUpdate.MselId, _context))
                    throw new ForbiddenException();
            }
            else
            {
                if (!hasPlayerApplicationPermission)
                    throw new ForbiddenException();
            }

            _mapper.Map(playerApplication, playerApplicationToUpdate);
            playerApplicationToUpdate.ModifiedBy = _user.GetId();

            _context.PlayerApplications.Update(playerApplicationToUpdate);
            await _context.SaveChangesAsync(ct);

            return await GetAsync(playerApplicationToUpdate.Id, true, ct);
        }

        public async Task<bool> DeleteAsync(Guid id, bool hasSystemPermission, bool hasPlayerApplicationPermission, CancellationToken ct)
        {
            var playerApplicationToDelete = await _context.PlayerApplications.SingleOrDefaultAsync(v => v.Id == id, ct);

            if (playerApplicationToDelete == null)
                throw new EntityNotFoundException<PlayerApplication>();

            if (playerApplicationToDelete.MselId.HasValue)
            {
                if (!hasSystemPermission && !await MselOwnerRequirement.IsMet(_user.GetId(), playerApplicationToDelete.MselId, _context))
                    throw new ForbiddenException();
            }
            else
            {
                if (!hasPlayerApplicationPermission)
                    throw new ForbiddenException();
            }

            _context.PlayerApplications.Remove(playerApplicationToDelete);
            await _context.SaveChangesAsync(ct);

            return true;
        }

        public async Task<Tuple<MemoryStream, string>> DownloadJsonAsync(IEnumerable<Guid> ids, CancellationToken ct)
        {
            var idList = ids?.ToList() ?? new List<Guid>();
            // only templates are exported: a MSEL's own rows are read through the MSEL
            var playerApplicationEntities = await _context.PlayerApplications
                .Where(pa => idList.Contains(pa.Id) && pa.MselId == null)
                .ToListAsync(ct);
            var playerApplications = _mapper.Map<IEnumerable<ViewModels.PlayerApplication>>(playerApplicationEntities).ToList();

            var json = JsonSerializer.Serialize(playerApplications, new JsonSerializerOptions
            {
                ReferenceHandler = ReferenceHandler.Preserve,
                WriteIndented = true,
            });
            var memoryStream = new MemoryStream(Encoding.UTF8.GetBytes(json));

            return Tuple.Create(memoryStream, "player-application-templates.json");
        }

        public async Task<IEnumerable<ViewModels.PlayerApplication>> UploadJsonAsync(FileForm form, CancellationToken ct)
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
            var incoming = JsonSerializer.Deserialize<List<ViewModels.PlayerApplication>>(json, options) ?? new List<ViewModels.PlayerApplication>();

            var created = new List<ViewModels.PlayerApplication>();
            var userId = _user.GetId();
            foreach (var item in incoming)
            {
                item.Id = Guid.NewGuid();
                item.IsTemplate = true;
                item.MselId = null;
                item.CreatedBy = userId;
                item.DateCreated = DateTime.UtcNow;
                item.ModifiedBy = null;
                item.DateModified = null;
                var entity = _mapper.Map<PlayerApplicationEntity>(item);
                _context.PlayerApplications.Add(entity);
                created.Add(_mapper.Map<ViewModels.PlayerApplication>(entity));
            }
            await _context.SaveChangesAsync(ct);

            return created;
        }

    }
}
