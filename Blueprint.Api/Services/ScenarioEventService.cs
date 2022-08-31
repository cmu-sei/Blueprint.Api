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
using Blueprint.Api.Infrastructure.Options;
using Blueprint.Api.Infrastructure.QueryParameters;
using Blueprint.Api.ViewModels;

namespace Blueprint.Api.Services
{
    public interface IScenarioEventService
    {
        Task<IEnumerable<ViewModels.ScenarioEvent>> GetAsync(ScenarioEventGet queryParameters, CancellationToken ct);
        Task<ViewModels.ScenarioEvent> GetAsync(Guid id, CancellationToken ct);
        Task<ViewModels.ScenarioEvent> CreateAsync(ViewModels.ScenarioEvent scenarioEvent, CancellationToken ct);
        Task<ViewModels.ScenarioEvent> UpdateAsync(Guid id, ViewModels.ScenarioEvent scenarioEvent, CancellationToken ct);
        // Task<Guid> AssignTeamAsync(Guid id, Guid teamId, CancellationToken ct);
        // Task<bool> UnassignTeamAsync(Guid id, Guid teamId, CancellationToken ct);
        Task<bool> DeleteAsync(Guid id, CancellationToken ct);
    }

    public class ScenarioEventService : IScenarioEventService
    {
        private readonly BlueprintContext _context;
        private readonly IAuthorizationService _authorizationService;
        private readonly ClaimsPrincipal _user;
        private readonly IMapper _mapper;
         private readonly DatabaseOptions _options;

        public ScenarioEventService(
            BlueprintContext context,
            IAuthorizationService authorizationService,
            IPrincipal user,
            IMapper mapper,
            DatabaseOptions options)
        {
            _context = context;
            _authorizationService = authorizationService;
            _user = user as ClaimsPrincipal;
            _mapper = mapper;
            _options = options;
        }

        public async Task<IEnumerable<ViewModels.ScenarioEvent>> GetAsync(ScenarioEventGet queryParameters, CancellationToken ct)
        {
            IQueryable<ScenarioEventEntity> scenarioEvents = null;

            // filter based on MSEL
            if (!String.IsNullOrEmpty(queryParameters.MselId))
            {
                Guid mselId;
                Guid.TryParse(queryParameters.MselId, out mselId);

                if (
                        !(await MselViewRequirement.IsMet(_user.GetId(), mselId, _context)) &&
                        !(await _authorizationService.AuthorizeAsync(_user, null, new ContentDeveloperRequirement())).Succeeded
                )
                {
                    var msel = await _context.Msels.FindAsync(mselId);
                    if (!msel.IsTemplate)
                        throw new ForbiddenException();
                }

                scenarioEvents = _context.ScenarioEvents
                    .Where(i => i.MselId == mselId)
                    .Include(se => se.DataValues);
            }
            if (scenarioEvents == null)
            {
                if (!(await _authorizationService.AuthorizeAsync(_user, null, new ContentDeveloperRequirement())).Succeeded)
                    throw new ForbiddenException();
                scenarioEvents = _context.ScenarioEvents;
            }
            else
            {
                // filter based on team
                if (!String.IsNullOrEmpty(queryParameters.TeamId) && scenarioEvents != null)
                {
                    Guid teamId;
                    Guid.TryParse(queryParameters.TeamId, out teamId);
                    var mselIdList = await _context.MselTeams.Where(mt => mt.TeamId == teamId).Select(m => m.Id).ToListAsync();
                    scenarioEvents = scenarioEvents.Where(i => mselIdList.Contains(i.MselId));
                }
            }
            // order the results
            scenarioEvents = scenarioEvents.OrderBy(n => n.RowIndex);

            return _mapper.Map<IEnumerable<ScenarioEvent>>(await scenarioEvents.ToListAsync());
        }

        public async Task<ViewModels.ScenarioEvent> GetAsync(Guid id, CancellationToken ct)
        {
            var item = await _context.ScenarioEvents
                .Include(se => se.DataValues)
                .SingleAsync(a => a.Id == id, ct);

            if (item == null)
                throw new EntityNotFoundException<ScenarioEventEntity>();

            if (!(await _authorizationService.AuthorizeAsync(_user, null, new ContentDeveloperRequirement())).Succeeded &&
                !(await MselViewRequirement.IsMet(_user.GetId(), item.MselId, _context)))
                throw new ForbiddenException();

            return _mapper.Map<ViewModels.ScenarioEvent>(item);
        }

        public async Task<ViewModels.ScenarioEvent> CreateAsync(ViewModels.ScenarioEvent scenarioEvent, CancellationToken ct)
        {
            // user must be a Content Developer or a MSEL owner
            if (!(await _authorizationService.AuthorizeAsync(_user, null, new ContentDeveloperRequirement())).Succeeded &&
                !(await MselOwnerRequirement.IsMet(_user.GetId(), scenarioEvent.MselId, _context)))
                throw new ForbiddenException();

            // create the scenario event
            scenarioEvent.Id = scenarioEvent.Id != Guid.Empty ? scenarioEvent.Id : Guid.NewGuid();
            scenarioEvent.DateCreated = DateTime.UtcNow;
            scenarioEvent.CreatedBy = _user.GetId();
            scenarioEvent.DateModified = null;
            scenarioEvent.ModifiedBy = null;
            var scenarioEventEntity = _mapper.Map<ScenarioEventEntity>(scenarioEvent);
            _context.ScenarioEvents.Add(scenarioEventEntity);
            // create the associated data values
            var dataFieldIdList = _context.DataFields
                .Where(df => df.MselId == scenarioEvent.MselId)
                .Select(df => df.Id);
            foreach (var dataFieldId in dataFieldIdList)
            {
                var dataValue = scenarioEvent.DataValues
                    .FirstOrDefault(dv => dv.DataFieldId == dataFieldId);
                if (dataValue == null)
                {
                    dataValue = new DataValue();
                    dataValue.DataFieldId = dataFieldId;
                }
                dataValue.Id = Guid.NewGuid();
                dataValue.ScenarioEventId = scenarioEvent.Id;
                dataValue.CreatedBy = scenarioEvent.CreatedBy;
                dataValue.DateCreated = scenarioEvent.DateCreated;
                dataValue.DateModified = null;
                dataValue.ModifiedBy = null;
                var dataValueEntity = _mapper.Map<DataValueEntity>(dataValue);
                _context.DataValues.Add(dataValueEntity);
            }

            await _context.SaveChangesAsync(ct);


            return _mapper.Map<ViewModels.ScenarioEvent>(scenarioEventEntity);
        }

        public async Task<ViewModels.ScenarioEvent> UpdateAsync(Guid id, ViewModels.ScenarioEvent scenarioEvent, CancellationToken ct)
        {
            var scenarioEventToUpdate = await _context.ScenarioEvents.SingleOrDefaultAsync(v => v.Id == id, ct);

            if (scenarioEventToUpdate == null)
                throw new EntityNotFoundException<ScenarioEventEntity>();

            // Content developers and MSEL owners can update anything, others require condition checks
            if (!(await _authorizationService.AuthorizeAsync(_user, null, new ContentDeveloperRequirement())).Succeeded &&
                !(await MselOwnerRequirement.IsMet(_user.GetId(), scenarioEvent.MselId, _context)))
            {
                // to change the assignedTeam, user must be a Content Developer or a MSEL owner
                if (scenarioEvent.AssignedTeamId != scenarioEventToUpdate.AssignedTeamId)
                    throw new ForbiddenException("Cannot change the Assigned Team.");
                // MSEL Approvers can change everything else
                if (!(await MselApproverRequirement.IsMet(_user.GetId(), scenarioEvent.MselId, _context)))
                {
                    // to change the status, user must be a MSEL approver
                    if (scenarioEvent.Status != scenarioEventToUpdate.Status)
                            throw new ForbiddenException("Cannot change the Status.");
                    // to change anything, user must be a MSEL editor
                    if (!(await MselEditorRequirement.IsMet(_user.GetId(), scenarioEvent.MselId, _context)))
                        throw new ForbiddenException();
                }
            }

            // start a transaction, because we may also update DataValues
            await _context.Database.BeginTransactionAsync();

            if (scenarioEvent.DataValues.Any())
            {
                await UpdateDataValues(scenarioEvent, ct);
            }
            scenarioEvent.CreatedBy = scenarioEventToUpdate.CreatedBy;
            scenarioEvent.DateCreated = scenarioEventToUpdate.DateCreated;
            scenarioEvent.ModifiedBy = _user.GetId();
            scenarioEvent.DateModified = DateTime.UtcNow;
            _mapper.Map(scenarioEvent, scenarioEventToUpdate);

            _context.ScenarioEvents.Update(scenarioEventToUpdate);
            await _context.SaveChangesAsync(ct);
            // commit the transaction
            await _context.Database.CommitTransactionAsync(ct);

            scenarioEvent = await GetAsync(id, ct);

            return scenarioEvent;
        }


        public async Task<bool> DeleteAsync(Guid id, CancellationToken ct)
        {
            var scenarioEventToDelete = await _context.ScenarioEvents.SingleOrDefaultAsync(v => v.Id == id, ct);

            if (scenarioEventToDelete == null)
                throw new EntityNotFoundException<ScenarioEventEntity>();

            // user must be a Content Developer or a MSEL owner
            if (!(await _authorizationService.AuthorizeAsync(_user, null, new ContentDeveloperRequirement())).Succeeded &&
                !(await MselOwnerRequirement.IsMet(_user.GetId(), scenarioEventToDelete.MselId, _context)))
                throw new ForbiddenException();

            _context.ScenarioEvents.Remove(scenarioEventToDelete);
            await _context.SaveChangesAsync(ct);

            return true;
        }

        private async Task UpdateDataValues(ScenarioEvent scenarioEvent, CancellationToken ct)
        {
            foreach (var dataValue in scenarioEvent.DataValues)
            {
                var dataValueToUpdate = await _context.DataValues
                    .FindAsync(dataValue.Id);
                if (dataValueToUpdate == null)
                    throw new ArgumentException("A DataValue could not be found for ID " + dataValue.Id.ToString());
                dataValueToUpdate.Value = dataValue.Value;
                await _context.SaveChangesAsync(ct);
            }
        }

    }

 }

