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
        Task<IEnumerable<ViewModels.Organization>> GetByMselAsync(Guid mselId, bool hasSystemPermission, CancellationToken ct);
        Task<ViewModels.Organization> GetAsync(Guid id, bool hasSystemPermission, CancellationToken ct);
        Task<ViewModels.Organization> CreateAsync(ViewModels.Organization organization, bool hasMselPermission, bool hasOrganizationPermission, CancellationToken ct);
        Task<ViewModels.Organization> UpdateAsync(Guid id, ViewModels.Organization organization, bool hasMselPermission, bool hasOrganizationPermission, CancellationToken ct);
        Task<bool> DeleteAsync(Guid id, bool hasMselPermission, bool hasOrganizationPermission, CancellationToken ct);
    }

    public class OrganizationService : IOrganizationService
    {
        private readonly BlueprintContext _context;
        private readonly ClaimsPrincipal _user;
        private readonly IMapper _mapper;

        public OrganizationService(
            BlueprintContext context,
            IPrincipal user,
            IMapper mapper)
        {
            _context = context;
            _user = user as ClaimsPrincipal;
            _mapper = mapper;
        }

        public async Task<IEnumerable<ViewModels.Organization>> GetTemplatesAsync(CancellationToken ct)
        {
            var organizationEntities = await _context.Organizations
                .Where(organization => organization.Msel == null)
                .ToListAsync(ct);

            return _mapper.Map<IEnumerable<Organization>>(organizationEntities).ToList();;
        }

        public async Task<IEnumerable<ViewModels.Organization>> GetByMselAsync(Guid mselId, bool hasSystemPermission, CancellationToken ct)
        {
            if (!hasSystemPermission && !(await MselViewRequirement.IsMet(_user.GetId(), mselId, _context)))
            {
                var msel = await _context.Msels.FindAsync(mselId);
                if (!msel.IsTemplate)
                    throw new ForbiddenException();
            }

            var organizationEntities = await _context.Organizations
                .Where(organization => organization.MselId == mselId)
                .ToListAsync(ct);

            return _mapper.Map<IEnumerable<Organization>>(organizationEntities).ToList();;
        }

        public async Task<ViewModels.Organization> GetAsync(Guid id, bool hasSystemPermission, CancellationToken ct)
        {
            var item = await _context.Organizations.SingleAsync(organization => organization.Id == id, ct);
            if (item == null)
                throw new EntityNotFoundException<DataValueEntity>("DataValue not found: " + id);

            // Templates (null MselId) can be viewed by anyone
            if (item.MselId.HasValue)
            {
                if (!hasSystemPermission && !await MselViewRequirement.IsMet(_user.GetId(), item.MselId, _context))
                    throw new ForbiddenException();
            }

            return _mapper.Map<Organization>(item);
        }

        public async Task<ViewModels.Organization> CreateAsync(ViewModels.Organization organization, bool hasMselPermission, bool hasOrganizationPermission, CancellationToken ct)
        {
            if (organization.MselId.HasValue)
            {
                if (!hasMselPermission && !await MselEditorRequirement.IsMet(_user.GetId(), organization.MselId, _context))
                    throw new ForbiddenException();
            }
            else
            {
                if (!hasOrganizationPermission)
                    throw new ForbiddenException();
            }

            // start a transaction, because we may also update other data fields
            await _context.Database.BeginTransactionAsync(ct);
            organization.Id = organization.Id != Guid.Empty ? organization.Id : Guid.NewGuid();
            organization.CreatedBy = _user.GetId();
            var organizationEntity = _mapper.Map<OrganizationEntity>(organization);
            _context.Organizations.Add(organizationEntity);
            await _context.SaveChangesAsync(ct);
            // update the MSEL modified info
            if (organization.MselId != null)
            {
                await ServiceUtilities.SetMselModifiedAsync((Guid)organization.MselId, organization.CreatedBy, organization.DateCreated, _context, ct);
            }
            // commit the transaction
            await _context.Database.CommitTransactionAsync(ct);
            organization = await GetAsync(organizationEntity.Id, true, ct);

            return organization;
        }

        public async Task<ViewModels.Organization> UpdateAsync(Guid id, ViewModels.Organization organization, bool hasMselPermission, bool hasOrganizationPermission, CancellationToken ct)
        {
            if (organization.MselId.HasValue)
            {
                if (!hasMselPermission && !await MselEditorRequirement.IsMet(_user.GetId(), organization.MselId, _context))
                    throw new ForbiddenException();
            }
            else
            {
                if (!hasOrganizationPermission)
                    throw new ForbiddenException();
            }

            var organizationToUpdate = await _context.Organizations.SingleOrDefaultAsync(v => v.Id == id, ct);

            if (organizationToUpdate == null)
                throw new EntityNotFoundException<Organization>();

            // start a transaction, because we may also update other data fields
            await _context.Database.BeginTransactionAsync(ct);
            organization.ModifiedBy = _user.GetId();
            _mapper.Map(organization, organizationToUpdate);
            _context.Organizations.Update(organizationToUpdate);
            await _context.SaveChangesAsync(ct);
            // update the MSEL modified info
            if (organization.MselId != null)
            {
                await ServiceUtilities.SetMselModifiedAsync((Guid)organizationToUpdate.MselId, organizationToUpdate.ModifiedBy, organizationToUpdate.DateModified, _context, ct);
            }
            // commit the transaction
            await _context.Database.CommitTransactionAsync(ct);
            organization = await GetAsync(organizationToUpdate.Id, true, ct);

            return organization;
        }

        public async Task<bool> DeleteAsync(Guid id, bool hasMselPermission, bool hasOrganizationPermission, CancellationToken ct)
        {
            var organizationToDelete = await _context.Organizations.SingleOrDefaultAsync(v => v.Id == id, ct);

            if (organizationToDelete == null)
                throw new EntityNotFoundException<Organization>();

            if (organizationToDelete.MselId.HasValue)
            {
                if (!hasMselPermission && !await MselEditorRequirement.IsMet(_user.GetId(), organizationToDelete.MselId, _context))
                    throw new ForbiddenException();
            }
            else
            {
                if (!hasOrganizationPermission)
                    throw new ForbiddenException();
            }

            // start a transaction, because we may also update other data fields
            await _context.Database.BeginTransactionAsync(ct);
            _context.Organizations.Remove(organizationToDelete);
            await _context.SaveChangesAsync(ct);
            // update the MSEL modified info
            if (organizationToDelete.MselId != null)
            {
                await ServiceUtilities.SetMselModifiedAsync((Guid)organizationToDelete.MselId, _user.GetId(), DateTime.UtcNow, _context, ct);
            }
            // commit the transaction
            await _context.Database.CommitTransactionAsync(ct);

            return true;
        }

    }
}

