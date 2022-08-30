// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

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
using Blueprint.Api.Data;
using Blueprint.Api.Data.Models;
using Blueprint.Api.Infrastructure.Authorization;
using Blueprint.Api.Infrastructure.Exceptions;
using Blueprint.Api.Infrastructure.Extensions;
using Blueprint.Api.ViewModels;

namespace Blueprint.Api.Services
{
    public interface IOrganizationService
    {
        Task<IEnumerable<ViewModels.Organization>> GetTemplatesAsync(CancellationToken ct);
        Task<IEnumerable<ViewModels.Organization>> GetByMselAsync(Guid mselId, CancellationToken ct);
        Task<ViewModels.Organization> GetAsync(Guid id, CancellationToken ct);
        Task<ViewModels.Organization> CreateAsync(ViewModels.Organization organization, CancellationToken ct);
        Task<ViewModels.Organization> UpdateAsync(Guid id, ViewModels.Organization organization, CancellationToken ct);
        Task<bool> DeleteAsync(Guid id, CancellationToken ct);
    }

    public class OrganizationService : IOrganizationService
    {
        private readonly BlueprintContext _context;
        private readonly IAuthorizationService _authorizationService;
        private readonly ClaimsPrincipal _user;
        private readonly IMapper _mapper;

        public OrganizationService(
            BlueprintContext context,
            IAuthorizationService authorizationService,
            IPrincipal user,
            IMapper mapper)
        {
            _context = context;
            _authorizationService = authorizationService;
            _user = user as ClaimsPrincipal;
            _mapper = mapper;
        }

        public async Task<IEnumerable<ViewModels.Organization>> GetTemplatesAsync(CancellationToken ct)
        {
            if (!(await _authorizationService.AuthorizeAsync(_user, null, new BaseUserRequirement())).Succeeded)
                throw new ForbiddenException();

            var organizationEntities = await _context.Organizations
                .Where(organization => organization.IsTemplate)
                .ToListAsync();

            return _mapper.Map<IEnumerable<Organization>>(organizationEntities).ToList();;
        }

        public async Task<IEnumerable<ViewModels.Organization>> GetByMselAsync(Guid mselId, CancellationToken ct)
        {
            if (
                    !(await MselViewRequirement.IsMet(_user.GetId(), mselId, _context)) &&
                    !(await _authorizationService.AuthorizeAsync(_user, null, new ContentDeveloperRequirement())).Succeeded
               )
                throw new ForbiddenException();

            var organizationEntities = await _context.Organizations
                .Where(organization => organization.MselId == mselId)
                .ToListAsync();

            return _mapper.Map<IEnumerable<Organization>>(organizationEntities).ToList();;
        }

        public async Task<ViewModels.Organization> GetAsync(Guid id, CancellationToken ct)
        {
            if (!(await _authorizationService.AuthorizeAsync(_user, null, new BaseUserRequirement())).Succeeded)
                throw new ForbiddenException();

            var item = await _context.Organizations.SingleAsync(organization => organization.Id == id, ct);
            if (!item.IsTemplate &&
                !(await MselViewRequirement.IsMet(_user.GetId(), (Guid)item.MselId, _context)) &&
                !(await _authorizationService.AuthorizeAsync(_user, null, new ContentDeveloperRequirement())).Succeeded)
                throw new ForbiddenException();

            return _mapper.Map<Organization>(item);
        }

        public async Task<ViewModels.Organization> CreateAsync(ViewModels.Organization organization, CancellationToken ct)
        {
            // content developers can create.  Others need further evaluation.
            if (!(await _authorizationService.AuthorizeAsync(_user, null, new ContentDeveloperRequirement())).Succeeded)
            {
                // non-content developers cannot create an organization template
                if (organization.IsTemplate || organization.MselId == null)
                   throw new ForbiddenException();
                // non-msel owners cannot create an organization on the msel
                if (!(await MselOwnerRequirement.IsMet(_user.GetId(), (Guid)organization.MselId, _context)))
                   throw new ForbiddenException();
            }

            organization.Id = organization.Id != Guid.Empty ? organization.Id : Guid.NewGuid();
            organization.DateCreated = DateTime.UtcNow;
            organization.CreatedBy = _user.GetId();
            organization.DateModified = null;
            organization.ModifiedBy = null;
            var organizationEntity = _mapper.Map<OrganizationEntity>(organization);

            _context.Organizations.Add(organizationEntity);
            await _context.SaveChangesAsync(ct);
            organization = await GetAsync(organizationEntity.Id, ct);

            return organization;
        }

        public async Task<ViewModels.Organization> UpdateAsync(Guid id, ViewModels.Organization organization, CancellationToken ct)
        {
            // content developers can update.  Others need further evaluation.
            if (!(await _authorizationService.AuthorizeAsync(_user, null, new ContentDeveloperRequirement())).Succeeded)
            {
                // non-content developers cannot update an organization template
                if (organization.IsTemplate || organization.MselId == null)
                   throw new ForbiddenException();
                // non-msel owners cannot update an organization on the msel
                if (!(await MselOwnerRequirement.IsMet(_user.GetId(), (Guid)organization.MselId, _context)))
                   throw new ForbiddenException();
            }

            var organizationToUpdate = await _context.Organizations.SingleOrDefaultAsync(v => v.Id == id, ct);

            if (organizationToUpdate == null)
                throw new EntityNotFoundException<Organization>();

            organization.CreatedBy = organizationToUpdate.CreatedBy;
            organization.DateCreated = organizationToUpdate.DateCreated;
            organization.ModifiedBy = _user.GetId();
            organization.DateModified = DateTime.UtcNow;
            _mapper.Map(organization, organizationToUpdate);

            _context.Organizations.Update(organizationToUpdate);
            await _context.SaveChangesAsync(ct);

            organization = await GetAsync(organizationToUpdate.Id, ct);

            return organization;
        }

        public async Task<bool> DeleteAsync(Guid id, CancellationToken ct)
        {
            var organizationToDelete = await _context.Organizations.SingleOrDefaultAsync(v => v.Id == id, ct);

            if (
                (organizationToDelete.MselId == null && !(await _authorizationService.AuthorizeAsync(_user, null, new ContentDeveloperRequirement())).Succeeded) &&
                (organizationToDelete.MselId != null && !(await MselViewRequirement.IsMet(_user.GetId(), (Guid)organizationToDelete.MselId, _context)))
               )
                   throw new ForbiddenException();

            if (organizationToDelete == null)
                throw new EntityNotFoundException<Organization>();

            _context.Organizations.Remove(organizationToDelete);
            await _context.SaveChangesAsync(ct);

            return true;
        }

    }
}

