// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact inject@sei.cmu.edu for full terms.

using System;
using System.Collections.Generic;
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
        Task<ViewModels.CatalogInject> GetAsync(Guid id, CancellationToken ct);
        Task<ViewModels.CatalogInject> CreateAsync(ViewModels.CatalogInject catalogInject, CancellationToken ct);
        Task<bool> DeleteAsync(Guid id, CancellationToken ct);
        Task<bool> DeleteByIdsAsync(Guid catalogId, Guid injectId, CancellationToken ct);
    }

    public class CatalogInjectService : ICatalogInjectService
    {
        private readonly BlueprintContext _context;
        private readonly IAuthorizationService _authorizationService;
        private readonly ClaimsPrincipal _catalog;
        private readonly IMapper _mapper;
        private readonly ILogger<ICatalogInjectService> _logger;

        public CatalogInjectService(BlueprintContext context, IAuthorizationService authorizationService, IPrincipal catalog, ILogger<ICatalogInjectService> logger, IMapper mapper)
        {
            _context = context;
            _authorizationService = authorizationService;
            _catalog = catalog as ClaimsPrincipal;
            _mapper = mapper;
            _logger = logger;
        }

        public async Task<ViewModels.CatalogInject> GetAsync(Guid id, CancellationToken ct)
        {
            if (!(await _authorizationService.AuthorizeAsync(_catalog, null, new FullRightsRequirement())).Succeeded)
                throw new ForbiddenException();

            var item = await _context.CatalogInjects
                .SingleOrDefaultAsync(o => o.Id == id, ct);

            return _mapper.Map<CatalogInject>(item);
        }

        public async Task<ViewModels.CatalogInject> CreateAsync(ViewModels.CatalogInject catalogInject, CancellationToken ct)
        {
            if (!(await _authorizationService.AuthorizeAsync(_catalog, null, new FullRightsRequirement())).Succeeded)
                throw new ForbiddenException();

            catalogInject.Id = catalogInject.Id != Guid.Empty ? catalogInject.Id : Guid.NewGuid();
            var catalogInjectEntity = _mapper.Map<CatalogInjectEntity>(catalogInject);

            _context.CatalogInjects.Add(catalogInjectEntity);
            await _context.SaveChangesAsync(ct);
            _logger.LogWarning($"CatalogInject created by {_catalog.GetId()} = catalog: {catalogInject.CatalogId} and inject: {catalogInject.InjectId}");
            return await GetAsync(catalogInjectEntity.Id, ct);
        }

        public async Task<bool> DeleteAsync(Guid id, CancellationToken ct)
        {
            if (!(await _authorizationService.AuthorizeAsync(_catalog, null, new FullRightsRequirement())).Succeeded)
                throw new ForbiddenException();

            var catalogInjectToDelete = await _context.CatalogInjects.SingleOrDefaultAsync(v => v.Id == id, ct);

            if (catalogInjectToDelete == null)
                throw new EntityNotFoundException<CatalogInject>();

            _context.CatalogInjects.Remove(catalogInjectToDelete);
            await _context.SaveChangesAsync(ct);
            _logger.LogWarning($"CatalogInject deleted by {_catalog.GetId()} = catalog: {catalogInjectToDelete.CatalogId} and inject: {catalogInjectToDelete.InjectId}");
            return true;
        }

        public async Task<bool> DeleteByIdsAsync(Guid catalogId, Guid injectId, CancellationToken ct)
        {
            if (!(await _authorizationService.AuthorizeAsync(_catalog, null, new FullRightsRequirement())).Succeeded)
                throw new ForbiddenException();

            var catalogInjectToDelete = await _context.CatalogInjects.SingleOrDefaultAsync(v => v.CatalogId == catalogId && v.InjectId == injectId, ct);

            if (catalogInjectToDelete == null)
                throw new EntityNotFoundException<CatalogInject>();

            _context.CatalogInjects.Remove(catalogInjectToDelete);
            await _context.SaveChangesAsync(ct);
            _logger.LogWarning($"CatalogInject deleted by {_catalog.GetId()} = catalog: {catalogInjectToDelete.CatalogId} and inject: {catalogInjectToDelete.InjectId}");
            return true;
        }

    }
}
