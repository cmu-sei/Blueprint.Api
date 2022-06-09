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
            if (!(await _authorizationService.AuthorizeAsync(_user, null, new BaseUserRequirement())).Succeeded)
                throw new ForbiddenException();

            IQueryable<ScenarioEventEntity> scenarioEvents = null;

            // filter based on MSEL
            if (!String.IsNullOrEmpty(queryParameters.MselId))
            {
                Guid mselId;
                Guid.TryParse(queryParameters.MselId, out mselId);
                if (!(await _authorizationService.AuthorizeAsync(_user, null, new MselUserRequirement(mselId))).Succeeded &&
                    !(await _authorizationService.AuthorizeAsync(_user, null, new FullRightsRequirement())).Succeeded)
                    throw new ForbiddenException();
                scenarioEvents = _context.ScenarioEvents
                    .Where(i => i.MselId == mselId)
                    .Include(se => se.DataValues);
            }
            // filter based on team
            if (!String.IsNullOrEmpty(queryParameters.TeamId))
            {
                Guid teamId;
                Guid.TryParse(queryParameters.TeamId, out teamId);
                if (!(await _authorizationService.AuthorizeAsync(_user, null, new TeamUserRequirement(teamId))).Succeeded &&
                    !(await _authorizationService.AuthorizeAsync(_user, null, new FullRightsRequirement())).Succeeded)
                    throw new ForbiddenException();
                var mselIdList = await _context.Msels.Where(m => m.TeamId == teamId).Select(m => m.Id).ToListAsync();
                if (scenarioEvents == null)
                {
                    scenarioEvents = _context.ScenarioEvents.Where(i => mselIdList.Contains(i.MselId));
                }
                else
                {
                    scenarioEvents = scenarioEvents.Where(i => mselIdList.Contains(i.MselId));
                }
            }
            // filter based on move
            if (!String.IsNullOrEmpty(queryParameters.MoveId))
            {
                // Guid moveId;
                // Guid.TryParse(queryParameters.MoveId, out moveId);
                // var move = await _context.Moves.FindAsync(moveId);
                // if (move == null)
                //     throw new EntityNotFoundException<MoveEntity>("Requested Move Entity (" + moveId + ") was not found.");
                // if (!(await _authorizationService.AuthorizeAsync(_user, null, new MselUserRequirement(move.MselId))).Succeeded &&
                //     !(await _authorizationService.AuthorizeAsync(_user, null, new FullRightsRequirement())).Succeeded)
                //     throw new ForbiddenException();
                // if (scenarioEvents == null)
                // {
                //     scenarioEvents = _context.ScenarioEvents.Where(i => i.MoveId == moveId);
                // }
                // else
                // {
                //     scenarioEvents = scenarioEvents.Where(i => i.MoveId == moveId);
                // }
            }
            if (scenarioEvents == null)
            {
                if (!(await _authorizationService.AuthorizeAsync(_user, null, new FullRightsRequirement())).Succeeded)
                    throw new ForbiddenException();
                scenarioEvents = _context.ScenarioEvents;
            }
            // order the results
            scenarioEvents = scenarioEvents.OrderBy(n => n.MoveNumber).ThenBy(n => n.Time);

            return _mapper.Map<IEnumerable<ScenarioEvent>>(await scenarioEvents.ToListAsync());
        }

        public async Task<ViewModels.ScenarioEvent> GetAsync(Guid id, CancellationToken ct)
        {
            var item = await _context.ScenarioEvents
                .SingleAsync(a => a.Id == id, ct);

            if (item == null)
                throw new EntityNotFoundException<ScenarioEventEntity>();

            var teamId = (await _context.Msels
                .SingleAsync(m => m.Id == item.MselId))
                .TeamId;

            if ((teamId != null && !(await _authorizationService.AuthorizeAsync(_user, null, new TeamUserRequirement((Guid)teamId))).Succeeded) &&
                !(await _authorizationService.AuthorizeAsync(_user, null, new BaseUserRequirement())).Succeeded)
                throw new ForbiddenException();

            return _mapper.Map<ViewModels.ScenarioEvent>(item);
        }

        public async Task<ViewModels.ScenarioEvent> CreateAsync(ViewModels.ScenarioEvent scenarioEvent, CancellationToken ct)
        {
            // user must be on the requested team and be able to submit
            if (
                !((await _authorizationService.AuthorizeAsync(_user, null, new CanSubmitRequirement())).Succeeded) &&
                !(await _authorizationService.AuthorizeAsync(_user, null, new ContentDeveloperRequirement())).Succeeded
            )
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
            // user must be on the requested team and be able to submit
            if (
                !((await _authorizationService.AuthorizeAsync(_user, null, new CanSubmitRequirement())).Succeeded) &&
                !(await _authorizationService.AuthorizeAsync(_user, null, new ContentDeveloperRequirement())).Succeeded
            )
                throw new ForbiddenException();

            var scenarioEventToUpdate = await _context.ScenarioEvents.SingleOrDefaultAsync(v => v.Id == id, ct);

            if (scenarioEventToUpdate == null)
                throw new EntityNotFoundException<ScenarioEventEntity>();

            scenarioEvent.CreatedBy = scenarioEventToUpdate.CreatedBy;
            scenarioEvent.DateCreated = scenarioEventToUpdate.DateCreated;
            scenarioEvent.ModifiedBy = _user.GetId();
            scenarioEvent.DateModified = DateTime.UtcNow;
            _mapper.Map(scenarioEvent, scenarioEventToUpdate);

            _context.ScenarioEvents.Update(scenarioEventToUpdate);
            await _context.SaveChangesAsync(ct);

            scenarioEvent = await GetAsync(scenarioEventToUpdate.Id, ct);

            return scenarioEvent;
        }

        public async Task<bool> DeleteAsync(Guid id, CancellationToken ct)
        {
            var scenarioEventToDelete = await _context.ScenarioEvents.SingleOrDefaultAsync(v => v.Id == id, ct);

            if (scenarioEventToDelete == null)
                throw new EntityNotFoundException<ScenarioEventEntity>();

            // user must be on the requested team and be able to submit
            if (
                !((await _authorizationService.AuthorizeAsync(_user, null, new CanSubmitRequirement())).Succeeded) &&
                !(await _authorizationService.AuthorizeAsync(_user, null, new ContentDeveloperRequirement())).Succeeded
            )
                throw new ForbiddenException();

            _context.ScenarioEvents.Remove(scenarioEventToDelete);
            await _context.SaveChangesAsync(ct);

            return true;
        }

    }

 }

