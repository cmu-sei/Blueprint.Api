// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using AutoMapper;
using Blueprint.Api.Data;
using Blueprint.Api.Infrastructure.Extensions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Player.Api.Client;
using Microsoft.EntityFrameworkCore;
using Blueprint.Api.Data.Models;
using Blueprint.Api.Hubs;
using Blueprint.Api.Infrastructure.Authorization;
using Blueprint.Api.Infrastructure.Exceptions;
using Microsoft.AspNetCore.SignalR;

namespace Blueprint.Api.Services
{
    public interface IPlayerService
    {
        Task<ViewModels.Msel> PushToPlayerAsync(Guid mselId, CancellationToken ct);
        Task<ViewModels.Msel> PullFromPlayerAsync(Guid mselId, CancellationToken ct);
        Task<IEnumerable<ApplicationTemplate>> GetApplicationTemplatesAsync(CancellationToken ct);
    }

    public class PlayerService : IPlayerService
    {
        private readonly IPlayerApiClient _playerApiClient;
        private readonly BlueprintContext _context;
        private readonly IAuthorizationService _authorizationService;
        private readonly IMapper _mapper;
        private readonly ClaimsPrincipal _user;
        private readonly IHubContext<MainHub> _hubContext;
        private readonly IIntegrationQueue _integrationQueue;

        public PlayerService(
            IHttpContextAccessor httpContextAccessor,
            IPlayerApiClient playerApiClient,
            IAuthorizationService authorizationService,
            IPrincipal user,
            IUserClaimsService claimsService,
            BlueprintContext context,
            IHubContext<MainHub> hubContext,
            IIntegrationQueue integrationQueue,
            IMapper mapper)

        {
            _playerApiClient = playerApiClient;
            _user = user as ClaimsPrincipal;
            _context = context;
            _hubContext = hubContext;
            _authorizationService = authorizationService;
            _mapper = mapper;
            _integrationQueue = integrationQueue;
        }

        public async Task<ViewModels.Msel> PushToPlayerAsync(Guid mselId, CancellationToken ct)
        {
            // user must be a Content Developer or a MSEL owner
            if (!(await _authorizationService.AuthorizeAsync(_user, null, new ContentDeveloperRequirement())).Succeeded &&
                !(await MselOwnerRequirement.IsMet(_user.GetId(), mselId, _context)))
                throw new ForbiddenException();
            // get the MSEL and verify data state
            var msel = await _context.Msels
                .Include(m => m.PlayerApplications)
                .ThenInclude(pa => pa.PlayerApplicationTeams)
                .AsSplitQuery()
                .SingleOrDefaultAsync(m => m.Id == mselId);
            if (msel == null)
                throw new EntityNotFoundException<MselEntity>($"MSEL {mselId} was not found when attempting to create a Player View.");
            if (msel.PlayerViewId != null)
                throw new InvalidOperationException($"MSEL {mselId} is already associated to a Player View.");
            _integrationQueue.Add(mselId);
            return _mapper.Map<ViewModels.Msel>(msel); 
        }

        public async Task<ViewModels.Msel> PullFromPlayerAsync(Guid mselId, CancellationToken ct)
        {
            // user must be a Content Developer or a MSEL owner
            if (!(await _authorizationService.AuthorizeAsync(_user, null, new ContentDeveloperRequirement())).Succeeded &&
                !(await MselOwnerRequirement.IsMet(_user.GetId(), mselId, _context)))
                throw new ForbiddenException();
            // get the MSEL and verify data state
            var msel = await _context.Msels.FindAsync(mselId);
            if (msel == null)
                throw new EntityNotFoundException<MselEntity>($"MSEL {mselId} was not found when attempting to remove from Player.");
            if (msel.PlayerViewId == null)
                throw new InvalidOperationException($"MSEL {mselId} is not associated to a Player View.");
            // add msel to process queue
            _integrationQueue.Add(mselId);

            return _mapper.Map<ViewModels.Msel>(msel); 
        }

        public async Task<IEnumerable<ApplicationTemplate>> GetApplicationTemplatesAsync(CancellationToken ct)
        {
            var applicationTemplates = new List<ApplicationTemplate>();
            try
            {
                applicationTemplates = (List<ApplicationTemplate>)await _playerApiClient.GetApplicationTemplatesAsync(ct);
            }
            catch (System.Exception)
            {
            }
            return (IEnumerable<ApplicationTemplate>)applicationTemplates;
        }

    }
}
