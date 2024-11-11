// Copyright 2024 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Linq;
using System.Security.Claims;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using AutoMapper;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Blueprint.Api.Data;
using Blueprint.Api.Data.Models;
using Blueprint.Api.Infrastructure.Authorization;
using Blueprint.Api.Infrastructure.Exceptions;
using Blueprint.Api.Infrastructure.Extensions;
using Blueprint.Api.Infrastructure.Options;
using Blueprint.Api.ViewModels;

namespace Blueprint.Api.Services
{
    public interface ICatalogService
    {
        Task<IEnumerable<ViewModels.Catalog>> GetAsync(CancellationToken ct);
        Task<IEnumerable<ViewModels.Catalog>> GetMineAsync(CancellationToken ct);
        Task<IEnumerable<ViewModels.Catalog>> GetUserCatalogsAsync(Guid userId, CancellationToken ct);
        Task<ViewModels.Catalog> GetAsync(Guid id, CancellationToken ct);
        Task<ViewModels.Catalog> CreateAsync(ViewModels.Catalog catalog, CancellationToken ct);
        Task<ViewModels.Catalog> CopyAsync(Guid catalogId, CancellationToken ct);
        Task<ViewModels.Catalog> UpdateAsync(Guid id, ViewModels.Catalog catalog, CancellationToken ct);
        Task<bool> DeleteAsync(Guid id, CancellationToken ct);
        Task<Catalog> UploadJsonAsync(FileForm form, CancellationToken ct);
        Task<Tuple<MemoryStream, string>> DownloadJsonAsync(Guid catalogId, CancellationToken ct);
    }

    public class CatalogService : ICatalogService
    {
        private readonly BlueprintContext _context;
        private readonly IAuthorizationService _authorizationService;
        private readonly ClaimsPrincipal _user;
        private readonly IMapper _mapper;
        private readonly ILogger<CatalogService> _logger;
        private readonly ClientOptions _clientOptions;
        private readonly IScenarioEventService _scenarioEventService;
        private readonly IIntegrationQueue _integrationQueue;
        private readonly IPlayerService _playerService;
        private readonly IJoinQueue _joinQueue;

        public CatalogService(
            BlueprintContext context,
            ClientOptions clientOptions,
            IAuthorizationService authorizationService,
            IScenarioEventService scenarioEventService,
            IIntegrationQueue integrationQueue,
            IPlayerService playerService,
            IJoinQueue joinQueue,
            IPrincipal user,
            ILogger<CatalogService> logger,
            IMapper mapper)
        {
            _context = context;
            _clientOptions = clientOptions;
            _authorizationService = authorizationService;
            _scenarioEventService = scenarioEventService;
            _user = user as ClaimsPrincipal;
            _mapper = mapper;
            _logger = logger;
            _integrationQueue = integrationQueue;
            _playerService = playerService;
            _joinQueue = joinQueue;
        }

        public async Task<IEnumerable<ViewModels.Catalog>> GetAsync(CancellationToken ct)
        {
            if (!(await _authorizationService.AuthorizeAsync(_user, null, new ContentDeveloperRequirement())).Succeeded)
                throw new ForbiddenException();


            var catalogs = await _context.Catalogs.ToListAsync(ct);

            return _mapper.Map<IEnumerable<Catalog>>(catalogs);
        }

        public async Task<IEnumerable<ViewModels.Catalog>> GetMineAsync(CancellationToken ct)
        {
            var userId = _user.GetId();
            return await GetUserCatalogsAsync(userId, ct);
        }

        public async Task<IEnumerable<ViewModels.Catalog>> GetUserCatalogsAsync(Guid userId, CancellationToken ct)
        {
            var currentUserId = _user.GetId();
            if (currentUserId == userId)
            {
                if (!(await _authorizationService.AuthorizeAsync(_user, null, new BaseUserRequirement())).Succeeded)
                    throw new ForbiddenException();
            }
            else
            {
                if (!(await _authorizationService.AuthorizeAsync(_user, null, new FullRightsRequirement())).Succeeded)
                    throw new ForbiddenException();
            }
            // get the user's units
            var unitIdList = await _context.UnitUsers
                .Where(tu => tu.UserId == userId)
                .Select(tu => tu.UnitId)
                .ToListAsync(ct);
            // get the units' catalogs
            var unitCatalogList = await _context.CatalogUnits
                .Where(mu => unitIdList.Contains(mu.UnitId))
                .Select(mu => mu.Catalog)
                .ToListAsync(ct);
            // get catalogs created by user
            var myCatalogList = new List<CatalogEntity>();
            if ((await _authorizationService.AuthorizeAsync(_user, null, new ContentDeveloperRequirement())).Succeeded)
            {
                myCatalogList = await _context.Catalogs
                    .Where(m => m.CreatedBy == userId)
                    .ToListAsync(ct);
            }
            // combine lists
            var catalogList = unitCatalogList.Union(myCatalogList).OrderBy(m => m.Name);

            return _mapper.Map<IEnumerable<Catalog>>(catalogList);
        }

        public async Task<ViewModels.Catalog> GetAsync(Guid id, CancellationToken ct)
        {
            if (!await CatalogViewRequirement.IsMet(_user.GetId(), id, _context) &&
                !(await _authorizationService.AuthorizeAsync(_user, null, new ContentDeveloperRequirement())).Succeeded)
                throw new ForbiddenException();

            var catalogEntity = await _context.Catalogs
                .SingleOrDefaultAsync(sm => sm.Id == id, ct);

            return _mapper.Map<Catalog>(catalogEntity);
        }

        public async Task<ViewModels.Catalog> CreateAsync(ViewModels.Catalog catalog, CancellationToken ct)
        {
            if (!(await _authorizationService.AuthorizeAsync(_user, null, new ContentDeveloperRequirement())).Succeeded)
                throw new ForbiddenException();

            catalog.Id = catalog.Id != Guid.Empty ? catalog.Id : Guid.NewGuid();
            catalog.DateCreated = DateTime.UtcNow;
            catalog.CreatedBy = _user.GetId();
            catalog.DateModified = catalog.DateCreated;
            catalog.ModifiedBy = catalog.CreatedBy;
            var catalogEntity = _mapper.Map<CatalogEntity>(catalog);

            _context.Catalogs.Add(catalogEntity);
            await _context.SaveChangesAsync(ct);
            catalog = await GetAsync(catalogEntity.Id, ct);

            return catalog;
        }

        public async Task<ViewModels.Catalog> CopyAsync(Guid catalogId, CancellationToken ct)
        {
            if (!(await _authorizationService.AuthorizeAsync(_user, null, new ContentDeveloperRequirement())).Succeeded)
                throw new ForbiddenException();

            var newCatalogEntity = await _context.Catalogs
                .Include(m => m.CatalogInjects)
                .AsNoTracking()
                .SingleOrDefaultAsync(m => m.Id == catalogId);
            newCatalogEntity = await privateCatalogCopyAsync(newCatalogEntity, ct);
            var catalog = _mapper.Map<Catalog>(newCatalogEntity);

            return catalog;
        }

        private async Task<CatalogEntity> privateCatalogCopyAsync(CatalogEntity catalogEntity, CancellationToken ct)
        {
            // set new catalog entity values
            var currentUserId = _user.GetId();
            var username = (await _context.Users.SingleOrDefaultAsync(u => u.Id == currentUserId, ct)).Name;
            catalogEntity.Id = Guid.NewGuid();
            catalogEntity.DateCreated = DateTime.UtcNow;
            catalogEntity.CreatedBy = currentUserId;
            catalogEntity.DateModified = catalogEntity.DateCreated;
            catalogEntity.ModifiedBy = catalogEntity.CreatedBy;
            catalogEntity.Name = catalogEntity.Name + " - " + username;
            catalogEntity.IsPublic = false;
            // update all CatalogInjects to have a new ID, new catalog ID, and correct InjectId
            var injectIdCrossReference = new Dictionary<Guid, Guid>();
            foreach (var catalogInject in catalogEntity.CatalogInjects)
            {
                var injectExists = await _context.Injects
                    .AnyAsync(m => m.Id == catalogInject.InjectId, ct);
                if (injectExists)
                {
                    injectIdCrossReference[catalogInject.InjectId] = catalogInject.InjectId;
                    catalogInject.Inject = null;
                }
                else
                {
                    injectIdCrossReference[catalogInject.InjectId] = Guid.NewGuid();
                    // null the objects that would have caused exceptions by creating a duplicate
                    catalogInject.Inject.InjectType = null;
                    catalogInject.Inject.RequiresInject = null;
                    // loop through the inject data values
                    foreach (var dataValue in catalogInject.Inject.DataValues)
                    {
                        dataValue.Inject = null;
                        dataValue.DataField = null;
                        dataValue.ScenarioEvent = null;
                    }
                }
                catalogInject.Id = Guid.NewGuid();
                catalogInject.CatalogId = catalogEntity.Id;
                catalogInject.InjectId = injectIdCrossReference[catalogInject.InjectId];
            }
            // update the inject type and data fields, if necessary
            var dataFieldIdCrossReference = new Dictionary<Guid, Guid>();
            var existingInjectType = await _context.InjectTypes
                .Include(m => m.DataFields)
                .SingleOrDefaultAsync(m => m.Id == catalogEntity.InjectTypeId || m.Name == catalogEntity.InjectType.Name, ct);
            if (existingInjectType != null)
            {
                if (existingInjectType.Id != catalogEntity.InjectTypeId)
                {
                    var missingDataFields = "";
                    foreach (var dataField in catalogEntity.InjectType.DataFields)
                    {
                        var hasMatch = existingInjectType.DataFields.Any(m => m.Name == dataField.Name && m.DataType == dataField.DataType);
                        if (!hasMatch)
                        {
                            missingDataFields = missingDataFields + " '" + dataField.Name + "'";
                        }
                    }
                    if (missingDataFields != "")
                    {
                        throw new InvalidDataException("There is an existing Inject Type with the same name, but without matching Data Fields for (" + missingDataFields + ")");
                    }
                    catalogEntity.InjectTypeId = existingInjectType.Id;
                    foreach (var catalogInject in catalogEntity.CatalogInjects)
                    {
                        if (catalogInject.Inject != null)
                        {
                            catalogInject.Inject.InjectTypeId = existingInjectType.Id;
                        }
                    }
                }
                catalogEntity.InjectType = null;
            }
            else
            {
                var newId = Guid.NewGuid();
                catalogEntity.InjectTypeId = newId;
                catalogEntity.InjectType.Id = newId;
                foreach (var df in catalogEntity.InjectType.DataFields)
                {
                    df.Id = Guid.NewGuid();
                    df.InjectTypeId = newId;
                    df.InjectType = null;
                    foreach (var dataOption in df.DataOptions)
                    {
                        dataOption.Id = Guid.NewGuid();
                        dataOption.DataFieldId = df.Id;
                    }
                }

            }
            await _context.Catalogs.AddAsync(catalogEntity);
            await _context.SaveChangesAsync();
            return catalogEntity;
        }

        public async Task<ViewModels.Catalog> UpdateAsync(Guid id, ViewModels.Catalog catalog, CancellationToken ct)
        {
            // user must be a Content Developer or a Catalog owner
            if (!(await _authorizationService.AuthorizeAsync(_user, null, new ContentDeveloperRequirement())).Succeeded)
                throw new ForbiddenException();

            var catalogToUpdate = await _context.Catalogs.SingleOrDefaultAsync(v => v.Id == id, ct);
            if (catalogToUpdate == null)
                throw new EntityNotFoundException<Catalog>();

            // okay to update this catalog
            catalog.CreatedBy = catalogToUpdate.CreatedBy;
            catalog.DateCreated = catalogToUpdate.DateCreated;
            catalog.ModifiedBy = _user.GetId();
            catalog.DateModified = DateTime.UtcNow;
            _mapper.Map(catalog, catalogToUpdate);

            _context.Catalogs.Update(catalogToUpdate);
            await _context.SaveChangesAsync(ct);

            catalog = await GetAsync(catalogToUpdate.Id, ct);

            return catalog;
        }

        public async Task<bool> DeleteAsync(Guid id, CancellationToken ct)
        {
            // user must be a Content Developer or a Catalog owner
            if (!(await _authorizationService.AuthorizeAsync(_user, null, new ContentDeveloperRequirement())).Succeeded)
                throw new ForbiddenException();

            // delete the Catalog
            var catalogToDelete = await _context.Catalogs.SingleOrDefaultAsync(v => v.Id == id, ct);
            if (catalogToDelete == null)
                throw new EntityNotFoundException<Catalog>();

            _context.Catalogs.Remove(catalogToDelete);
            await _context.SaveChangesAsync(ct);

            return true;
        }

        public async Task<Tuple<MemoryStream, string>> DownloadJsonAsync(Guid catalogId, CancellationToken ct)
        {
            // user must be a Content Developer
            if (!(await _authorizationService.AuthorizeAsync(_user, null, new ContentDeveloperRequirement())).Succeeded)
                throw new ForbiddenException();

            var catalog = await _context.Catalogs
                .Include(m => m.CatalogInjects)
                .ThenInclude(n => n.Inject)
                .ThenInclude(p => p.DataValues)
                .Include(m => m.InjectType)
                .ThenInclude(n => n.DataFields)
                .ThenInclude(p => p.DataOptions)
                .AsSplitQuery()
                .SingleOrDefaultAsync(m => m.Id == catalogId);
            if (catalog == null)
            {
                throw new EntityNotFoundException<CatalogEntity>("Catalog not found " + catalogId);
            }

            var catalogJson = "";
            var options = new JsonSerializerOptions()
            {
                ReferenceHandler = ReferenceHandler.Preserve
            };
            catalogJson = JsonSerializer.Serialize(catalog, options);
            // convert string to stream
            byte[] byteArray = Encoding.ASCII.GetBytes(catalogJson);
            MemoryStream memoryStream = new MemoryStream(byteArray);
            var filename = catalog.Name.ToLower().EndsWith(".json") ? catalog.Name : catalog.Name + ".json";

            return System.Tuple.Create(memoryStream, filename);
        }

        public async Task<Catalog> UploadJsonAsync(FileForm form, CancellationToken ct)
        {
            // user must be a Content Developer
            if (!(await _authorizationService.AuthorizeAsync(_user, null, new ContentDeveloperRequirement())).Succeeded)
                throw new ForbiddenException();

            var uploadItem = form.ToUpload;
            var catalogJson = "";
            using (StreamReader reader = new StreamReader(uploadItem.OpenReadStream()))
            {
                // convert stream to string
                catalogJson = reader.ReadToEnd();
            }
            var options = new JsonSerializerOptions()
            {
                ReferenceHandler = ReferenceHandler.Preserve
            };
            var catalogEntity = JsonSerializer.Deserialize<CatalogEntity>(catalogJson, options);
            // make a copy of the catalog and add it to the database
            catalogEntity = await privateCatalogCopyAsync(catalogEntity, ct);

            return _mapper.Map<Catalog>(catalogEntity);
        }

    }
}
