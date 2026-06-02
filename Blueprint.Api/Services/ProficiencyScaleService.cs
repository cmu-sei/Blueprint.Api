// Copyright 2025 Carnegie Mellon University. All Rights Reserved.
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
using Blueprint.Api.Infrastructure.Extensions;
using Blueprint.Api.Infrastructure.Exceptions;
using Blueprint.Api.ViewModels;

namespace Blueprint.Api.Services
{
    public interface IProficiencyScaleService
    {
        Task<IEnumerable<ViewModels.ProficiencyScale>> GetAllAsync(CancellationToken ct);
        Task<ViewModels.ProficiencyScale> GetAsync(Guid id, CancellationToken ct);
        Task<ViewModels.ProficiencyScale> CreateAsync(ViewModels.ProficiencyScale proficiencyScale, CancellationToken ct);
        Task<ViewModels.ProficiencyScale> UpdateAsync(Guid id, ViewModels.ProficiencyScale proficiencyScale, CancellationToken ct);
        Task<bool> DeleteAsync(Guid id, CancellationToken ct);
        Task<IEnumerable<ViewModels.ProficiencyScale>> UploadJsonAsync(FileForm form, CancellationToken ct);
        Task<Tuple<MemoryStream, string>> DownloadJsonAsync(IEnumerable<Guid> ids, CancellationToken ct);
    }

    public class ProficiencyScaleService : IProficiencyScaleService
    {
        private readonly BlueprintContext _context;
        private readonly ClaimsPrincipal _user;
        private readonly IMapper _mapper;

        public ProficiencyScaleService(BlueprintContext context, IPrincipal user, IMapper mapper)
        {
            _context = context;
            _user = user as ClaimsPrincipal;
            _mapper = mapper;
        }

        public async Task<IEnumerable<ViewModels.ProficiencyScale>> GetAllAsync(CancellationToken ct)
        {
            var items = await _context.ProficiencyScales
                .Include(x => x.ProficiencyLevels)
                .OrderBy(x => x.Name)
                .ToListAsync(ct);

            return _mapper.Map<IEnumerable<ProficiencyScale>>(items);
        }

        public async Task<ViewModels.ProficiencyScale> GetAsync(Guid id, CancellationToken ct)
        {
            var item = await _context.ProficiencyScales
                .Include(x => x.ProficiencyLevels)
                .SingleOrDefaultAsync(o => o.Id == id, ct);

            return _mapper.Map<ProficiencyScale>(item);
        }

        public async Task<ViewModels.ProficiencyScale> CreateAsync(ViewModels.ProficiencyScale proficiencyScale, CancellationToken ct)
        {
            proficiencyScale.Id = proficiencyScale.Id != Guid.Empty ? proficiencyScale.Id : Guid.NewGuid();
            proficiencyScale.CreatedBy = _user.GetId();
            var entity = _mapper.Map<ProficiencyScaleEntity>(proficiencyScale);

            _context.ProficiencyScales.Add(entity);
            await _context.SaveChangesAsync(ct);

            return await GetAsync(entity.Id, ct);
        }

        public async Task<ViewModels.ProficiencyScale> UpdateAsync(Guid id, ViewModels.ProficiencyScale proficiencyScale, CancellationToken ct)
        {
            var entityToUpdate = await _context.ProficiencyScales.SingleOrDefaultAsync(v => v.Id == id, ct);

            if (entityToUpdate == null)
                throw new EntityNotFoundException<ProficiencyScale>();

            proficiencyScale.ModifiedBy = _user.GetId();
            _mapper.Map(proficiencyScale, entityToUpdate);

            _context.ProficiencyScales.Update(entityToUpdate);
            await _context.SaveChangesAsync(ct);

            return _mapper.Map(entityToUpdate, proficiencyScale);
        }

        public async Task<bool> DeleteAsync(Guid id, CancellationToken ct)
        {
            var entityToDelete = await _context.ProficiencyScales.SingleOrDefaultAsync(v => v.Id == id, ct);

            if (entityToDelete == null)
                throw new EntityNotFoundException<ProficiencyScale>();

            _context.ProficiencyScales.Remove(entityToDelete);
            await _context.SaveChangesAsync(ct);

            return true;
        }

        public async Task<Tuple<MemoryStream, string>> DownloadJsonAsync(IEnumerable<Guid> ids, CancellationToken ct)
        {
            var idList = ids?.ToList() ?? new List<Guid>();
            var entities = await _context.ProficiencyScales
                .Where(s => idList.Contains(s.Id))
                .Include(s => s.ProficiencyLevels)
                .ToListAsync(ct);
            var scales = _mapper.Map<IEnumerable<ViewModels.ProficiencyScale>>(entities).ToList();

            var json = JsonSerializer.Serialize(scales, new JsonSerializerOptions
            {
                ReferenceHandler = ReferenceHandler.Preserve,
                WriteIndented = true,
            });
            var memoryStream = new MemoryStream(Encoding.UTF8.GetBytes(json));

            return Tuple.Create(memoryStream, "proficiency-scale-export.json");
        }

        public async Task<IEnumerable<ViewModels.ProficiencyScale>> UploadJsonAsync(FileForm form, CancellationToken ct)
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
            var incoming = JsonSerializer.Deserialize<List<ViewModels.ProficiencyScale>>(json, options) ?? new List<ViewModels.ProficiencyScale>();

            var created = new List<ViewModels.ProficiencyScale>();
            var userId = _user.GetId();
            var dateNow = DateTime.UtcNow;
            foreach (var item in incoming)
            {
                var newScaleId = Guid.NewGuid();
                var incomingLevels = item.ProficiencyLevels?.ToList() ?? new List<ViewModels.ProficiencyLevel>();
                item.Id = newScaleId;
                item.ProficiencyLevels = new HashSet<ViewModels.ProficiencyLevel>();
                item.CreatedBy = userId;
                item.DateCreated = dateNow;
                item.ModifiedBy = null;
                item.DateModified = null;
                var entity = _mapper.Map<ProficiencyScaleEntity>(item);
                _context.ProficiencyScales.Add(entity);

                foreach (var level in incomingLevels)
                {
                    var levelEntity = new ProficiencyLevelEntity
                    {
                        Id = Guid.NewGuid(),
                        ProficiencyScaleId = newScaleId,
                        Name = level.Name,
                        Value = level.Value,
                        Description = level.Description,
                        DisplayOrder = level.DisplayOrder,
                        CreatedBy = userId,
                        DateCreated = dateNow,
                    };
                    _context.ProficiencyLevels.Add(levelEntity);
                }

                created.Add(_mapper.Map<ViewModels.ProficiencyScale>(entity));
            }
            await _context.SaveChangesAsync(ct);

            return created;
        }
    }
}
