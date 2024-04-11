// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using AutoMapper;
using Blueprint.Api.Data;
using Blueprint.Api.Data.Models;
using Blueprint.Api.Hubs;
using Blueprint.Api.Infrastructure.Authorization;
using Blueprint.Api.Infrastructure.Exceptions;
using Blueprint.Api.Infrastructure.Extensions;
using Player.Api.Client;
using Blueprint.Api.ViewModels;

namespace Blueprint.Api.Services
{
    public interface IPlayerService
    {
        Task<IEnumerable<ApplicationTemplate>> GetApplicationTemplatesAsync(CancellationToken ct);
        Task<IEnumerable<View>> GetMyViewsAsync(CancellationToken ct);
        Task PushApplication(PlayerApplication playerApplication, CancellationToken ct);
    }

    public class PlayerService : IPlayerService
    {
        private readonly IPlayerApiClient _playerApiClient;
        private readonly BlueprintContext _context;
        private readonly IAuthorizationService _authorizationService;
        private readonly IMapper _mapper;
        private readonly ClaimsPrincipal _user;
        private readonly IHubContext<MainHub> _hubContext;
        private readonly IAddApplicationQueue _addApplicationQueue;

        public PlayerService(
            IHttpContextAccessor httpContextAccessor,
            IPlayerApiClient playerApiClient,
            IAuthorizationService authorizationService,
            IPrincipal user,
            IUserClaimsService claimsService,
            BlueprintContext context,
            IHubContext<MainHub> hubContext,
            IAddApplicationQueue addApplicationQueue,
            IMapper mapper)

        {
            _playerApiClient = playerApiClient;
            _user = user as ClaimsPrincipal;
            _context = context;
            _hubContext = hubContext;
            _authorizationService = authorizationService;
            _addApplicationQueue = addApplicationQueue;
            _mapper = mapper;
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

        public async Task<IEnumerable<View>> GetMyViewsAsync(CancellationToken ct) {
            var views = new List<View>();
            try
            {
                views = (List<View>)await _playerApiClient.GetUserViewsAsync(_user.GetId(), ct);
            }
            catch (System.Exception)
            {
            }
            return views;
        }

        public async Task PushApplication(PlayerApplication application, CancellationToken ct)
        {
            var msel = await _context.Msels.SingleOrDefaultAsync(m => m.Id == application.MselId, ct);
            var playerApplication = new Application() {
                Name = application.Name,
                Embeddable = application.Embeddable,
                ViewId = (Guid)msel.PlayerViewId,
                Url = new Uri(application.Url),
                Icon = application.Icon,
                LoadInBackground = application.LoadInBackground
            };
            // get the Player Team ID
            var mselTeamIds = await _context.Teams
                .Where(t => t.MselId == msel.Id)
                .Select(t => t.Id)
                .ToListAsync(ct);
            var userId = _user.GetId();
            var playerTeamId = await _context.TeamUsers
                .Where(tu => tu.UserId == userId && mselTeamIds.Contains(tu.TeamId))
                .Select(tu => tu.Team.PlayerTeamId)
                .SingleOrDefaultAsync(ct);
            var addApplicationInformation = new AddApplicationInformation{
                Application = playerApplication,
                PlayerTeamId = (Guid)playerTeamId,
                DisplayOrder = msel.PlayerApplications.Count + 1
            };
            _addApplicationQueue.Add(addApplicationInformation);
        }

    }

}
