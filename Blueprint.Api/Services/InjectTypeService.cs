// Copyright 2024 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact injectType@sei.cmu.edu for full terms.

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
    public interface IInjectTypeService
    {
        Task<IEnumerable<ViewModels.InjectType>> GetAsync(CancellationToken ct);
        Task<ViewModels.InjectType> GetAsync(Guid id, CancellationToken ct);
        // Task<IEnumerable<ViewModels.InjectType>> GetByUserIdAsync(Guid userId, CancellationToken ct);
        Task<ViewModels.InjectType> CreateAsync(ViewModels.InjectType injectType, CancellationToken ct);
        Task<ViewModels.InjectType> UpdateAsync(Guid id, ViewModels.InjectType injectType, CancellationToken ct);
        Task<bool> DeleteAsync(Guid id, CancellationToken ct);
        Task<IEnumerable<ViewModels.InjectType>> UploadJsonAsync(FileForm form, CancellationToken ct);
        Task<Tuple<MemoryStream, string>> DownloadJsonAsync(IEnumerable<Guid> ids, CancellationToken ct);
    }

    public class InjectTypeService : IInjectTypeService
    {
        private readonly BlueprintContext _context;
        private readonly ClaimsPrincipal _user;
        private readonly IMapper _mapper;

        public InjectTypeService(BlueprintContext context, IPrincipal user, IMapper mapper)
        {
            _context = context;
            _user = user as ClaimsPrincipal;
            _mapper = mapper;
        }

        public async Task<IEnumerable<ViewModels.InjectType>> GetAsync(CancellationToken ct)
        {
            var items = await _context.InjectTypes
                .ToListAsync(ct);

            return _mapper.Map<IEnumerable<InjectType>>(items);
        }

        public async Task<ViewModels.InjectType> GetAsync(Guid id, CancellationToken ct)
        {
            var item = await _context.InjectTypes
                .SingleOrDefaultAsync(o => o.Id == id, ct);

            return _mapper.Map<InjectType>(item);
        }

        public async Task<ViewModels.InjectType> CreateAsync(ViewModels.InjectType injectType, CancellationToken ct)
        {
            injectType.Id = injectType.Id != Guid.Empty ? injectType.Id : Guid.NewGuid();
            injectType.CreatedBy = _user.GetId();
            var injectTypeEntity = _mapper.Map<InjectTypeEntity>(injectType);

            _context.InjectTypes.Add(injectTypeEntity);
            await _context.SaveChangesAsync(ct);

            return await GetAsync(injectTypeEntity.Id, ct);
        }

        public async Task<ViewModels.InjectType> UpdateAsync(Guid id, ViewModels.InjectType injectType, CancellationToken ct)
        {
            var injectTypeToUpdate = await _context.InjectTypes.SingleOrDefaultAsync(v => v.Id == id, ct);

            if (injectTypeToUpdate == null)
                throw new EntityNotFoundException<InjectType>();

            injectType.ModifiedBy = _user.GetId();
            _mapper.Map(injectType, injectTypeToUpdate);

            _context.InjectTypes.Update(injectTypeToUpdate);
            await _context.SaveChangesAsync(ct);

            return _mapper.Map(injectTypeToUpdate, injectType);
        }

        public async Task<bool> DeleteAsync(Guid id, CancellationToken ct)
        {
            var injectTypeToDelete = await _context.InjectTypes.SingleOrDefaultAsync(v => v.Id == id, ct);

            if (injectTypeToDelete == null)
                throw new EntityNotFoundException<InjectType>();

            _context.InjectTypes.Remove(injectTypeToDelete);
            await _context.SaveChangesAsync(ct);

            return true;
        }

        public async Task<Tuple<MemoryStream, string>> DownloadJsonAsync(IEnumerable<Guid> ids, CancellationToken ct)
        {
            var idList = ids?.ToList() ?? new List<Guid>();
            var injectTypeEntities = await _context.InjectTypes
                .Where(it => idList.Contains(it.Id))
                .Include(it => it.DataFields)
                    .ThenInclude(df => df.DataOptions)
                .AsSplitQuery()
                .ToListAsync(ct);
            var injectTypes = _mapper.Map<IEnumerable<ViewModels.InjectType>>(injectTypeEntities).ToList();

            var json = JsonSerializer.Serialize(injectTypes, new JsonSerializerOptions
            {
                ReferenceHandler = ReferenceHandler.Preserve,
                WriteIndented = true,
            });
            var memoryStream = new MemoryStream(Encoding.UTF8.GetBytes(json));

            return Tuple.Create(memoryStream, "inject-type-export.json");
        }

        public async Task<IEnumerable<ViewModels.InjectType>> UploadJsonAsync(FileForm form, CancellationToken ct)
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
            var incoming = JsonSerializer.Deserialize<List<ViewModels.InjectType>>(json, options) ?? new List<ViewModels.InjectType>();

            var created = new List<ViewModels.InjectType>();
            var userId = _user.GetId();
            var dateNow = DateTime.UtcNow;
            foreach (var item in incoming)
            {
                var newInjectTypeId = Guid.NewGuid();
                // Capture and detach the children before mapping; the DataField mapper ignores DataOptions,
                // and we rebuild both child layers by hand to assign fresh GUIDs.
                var incomingDataFields = item.DataFields?.ToList() ?? new List<ViewModels.DataField>();
                item.Id = newInjectTypeId;
                item.DataFields = new HashSet<ViewModels.DataField>();
                item.CreatedBy = userId;
                item.DateCreated = dateNow;
                item.ModifiedBy = null;
                item.DateModified = null;
                var entity = _mapper.Map<InjectTypeEntity>(item);
                _context.InjectTypes.Add(entity);

                foreach (var df in incomingDataFields)
                {
                    var newDataFieldId = Guid.NewGuid();
                    var incomingOptions = df.DataOptions?.ToList() ?? new List<ViewModels.DataOption>();
                    df.Id = newDataFieldId;
                    df.InjectTypeId = newInjectTypeId;
                    df.MselId = null;
                    df.IsTemplate = false;
                    df.CreatedBy = userId;
                    df.DateCreated = dateNow;
                    df.ModifiedBy = null;
                    df.DateModified = null;
                    df.DataOptions = new HashSet<ViewModels.DataOption>();
                    var dataFieldEntity = _mapper.Map<DataFieldEntity>(df);
                    _context.DataFields.Add(dataFieldEntity);

                    foreach (var opt in incomingOptions)
                    {
                        var optionEntity = new DataOptionEntity
                        {
                            Id = Guid.NewGuid(),
                            DataFieldId = newDataFieldId,
                            DisplayOrder = opt.DisplayOrder,
                            OptionName = opt.OptionName,
                            OptionValue = opt.OptionValue,
                            CreatedBy = userId,
                            DateCreated = dateNow,
                        };
                        _context.DataOptions.Add(optionEntity);
                    }
                }

                created.Add(_mapper.Map<ViewModels.InjectType>(entity));
            }
            await _context.SaveChangesAsync(ct);

            return created;
        }

    }
}

