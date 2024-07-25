// Copyright 2024 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact inject@sei.cmu.edu for full terms.

using System;
using System.Collections.Generic;
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
using Blueprint.Api.ViewModels;

namespace Blueprint.Api.Services
{
    public interface ICatalogInjectService
    {
        Task<IEnumerable<ViewModels.CatalogInject>> GetByCatalogAsync(Guid catalogId, CancellationToken ct);
        Task<ViewModels.CatalogInject> GetAsync(Guid id, CancellationToken ct);
        Task<ViewModels.CatalogInject> CreateAsync(ViewModels.CatalogInject catalogInject, CancellationToken ct);
        Task<Guid> DeleteAsync(Guid id, CancellationToken ct);
        Task<Guid> DeleteByIdsAsync(Guid catalogId, Guid injectId, CancellationToken ct);
    }

    public class CatalogInjectService : ICatalogInjectService
    {
        private readonly BlueprintContext _context;
        private readonly IAuthorizationService _authorizationService;
        private readonly ClaimsPrincipal _user;
        private readonly IMapper _mapper;
        private readonly ILogger<ICatalogInjectService> _logger;

        public CatalogInjectService(BlueprintContext context, IAuthorizationService authorizationService, IPrincipal user, ILogger<ICatalogInjectService> logger, IMapper mapper)
        {
            _context = context;
            _authorizationService = authorizationService;
            _user = user as ClaimsPrincipal;
            _mapper = mapper;
            _logger = logger;
        }

        public async Task<IEnumerable<ViewModels.CatalogInject>> GetByCatalogAsync(Guid catalogId, CancellationToken ct)
        {
            var catalog = await _context.Catalogs.SingleOrDefaultAsync(v => v.Id == catalogId, ct);
            if (catalog == null)
                throw new EntityNotFoundException<CatalogEntity>();

            // user must be a Content Developer or a Catalog viewer
            if (!(await _authorizationService.AuthorizeAsync(_user, null, new ContentDeveloperRequirement())).Succeeded &&
                !await CatalogViewRequirement.IsMet(_user.GetId(), catalogId, _context))
                throw new ForbiddenException();

            var items = await _context.CatalogInjects
                .Where(ci => ci.CatalogId == catalogId)
                .Include(m => m.Inject)
                .ThenInclude(n => n.DataValues)
                .AsSplitQuery()
                .ToListAsync(ct);

            return _mapper.Map<IEnumerable<CatalogInject>>(items);
        }

        public async Task<ViewModels.CatalogInject> GetAsync(Guid id, CancellationToken ct)
        {
            if (!(await _authorizationService.AuthorizeAsync(_user, null, new FullRightsRequirement())).Succeeded)
                throw new ForbiddenException();

            var item = await _context.CatalogInjects
                .SingleOrDefaultAsync(o => o.Id == id, ct);

            return _mapper.Map<CatalogInject>(item);
        }

        public async Task<ViewModels.CatalogInject> CreateAsync(ViewModels.CatalogInject catalogInject, CancellationToken ct)
        {
            if (!(await _authorizationService.AuthorizeAsync(_user, null, new FullRightsRequirement())).Succeeded)
                throw new ForbiddenException();

            catalogInject.Id = catalogInject.Id != Guid.Empty ? catalogInject.Id : Guid.NewGuid();
            var catalogInjectEntity = _mapper.Map<CatalogInjectEntity>(catalogInject);

            _context.CatalogInjects.Add(catalogInjectEntity);
            await _context.SaveChangesAsync(ct);
            _logger.LogWarning($"CatalogInject created by {_user.GetId()} = catalog: {catalogInject.CatalogId} and inject: {catalogInject.InjectId}");
            // update the catalog modified info
            var catalog = await _context.Catalogs
                .SingleOrDefaultAsync(m => m.Id == catalogInject.CatalogId);
            catalog.ModifiedBy = _user.GetId();
            catalog.DateModified = DateTime.UtcNow;
            await _context.SaveChangesAsync(ct);
            return await GetAsync(catalogInjectEntity.Id, ct);
        }

        public async Task<Guid> DeleteAsync(Guid id, CancellationToken ct)
        {
            if (!(await _authorizationService.AuthorizeAsync(_user, null, new FullRightsRequirement())).Succeeded)
                throw new ForbiddenException();

            var catalogInjectToDelete = await _context.CatalogInjects.SingleOrDefaultAsync(v => v.Id == id, ct);

            if (catalogInjectToDelete == null)
                throw new EntityNotFoundException<CatalogInject>();

            _context.CatalogInjects.Remove(catalogInjectToDelete);
            await _context.SaveChangesAsync(ct);
            _logger.LogWarning($"CatalogInject deleted by {_user.GetId()} = catalog: {catalogInjectToDelete.CatalogId} and inject: {catalogInjectToDelete.InjectId}");
            // update the catalog modified info
            var catalog = await _context.Catalogs
                .SingleOrDefaultAsync(m => m.Id == catalogInjectToDelete.CatalogId);
            catalog.ModifiedBy = _user.GetId();
            catalog.DateModified = DateTime.UtcNow;
            await _context.SaveChangesAsync(ct);
            return id;
        }

        public async Task<Guid> DeleteByIdsAsync(Guid catalogId, Guid injectId, CancellationToken ct)
        {
            if (!(await _authorizationService.AuthorizeAsync(_user, null, new FullRightsRequirement())).Succeeded)
                throw new ForbiddenException();

            var catalogInjectToDelete = await _context.CatalogInjects.SingleOrDefaultAsync(v => v.CatalogId == catalogId && v.InjectId == injectId, ct);

            if (catalogInjectToDelete == null)
                throw new EntityNotFoundException<CatalogInject>();

            _context.CatalogInjects.Remove(catalogInjectToDelete);
            await _context.SaveChangesAsync(ct);
            _logger.LogWarning($"CatalogInject deleted by {_user.GetId()} = catalog: {catalogInjectToDelete.CatalogId} and inject: {catalogInjectToDelete.InjectId}");
            // update the catalog modified info
            var catalog = await _context.Catalogs
                .SingleOrDefaultAsync(m => m.Id == catalogId);
            catalog.ModifiedBy = _user.GetId();
            catalog.DateModified = DateTime.UtcNow;
            await _context.SaveChangesAsync(ct);
            return catalogInjectToDelete.Id;
        }

    }
}
