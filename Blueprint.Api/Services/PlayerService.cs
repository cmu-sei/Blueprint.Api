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
        Task<ApplicationInstance> PushApplication(PlayerApplication playerApplication, CancellationToken ct);
    }

    public class PlayerService : IPlayerService
    {
        private readonly IPlayerApiClient _playerApiClient;
        private readonly BlueprintContext _context;
        private readonly IAuthorizationService _authorizationService;
        private readonly IMapper _mapper;
        private readonly ClaimsPrincipal _user;
        private readonly IHubContext<MainHub> _hubContext;

        public PlayerService(
            IHttpContextAccessor httpContextAccessor,
            IPlayerApiClient playerApiClient,
            IAuthorizationService authorizationService,
            IPrincipal user,
            IUserClaimsService claimsService,
            BlueprintContext context,
            IHubContext<MainHub> hubContext,
            IMapper mapper)

        {
            _playerApiClient = playerApiClient;
            _user = user as ClaimsPrincipal;
            _context = context;
            _hubContext = hubContext;
            _authorizationService = authorizationService;
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

        public async Task<ApplicationInstance> PushApplication(PlayerApplication application, CancellationToken ct)
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
            playerApplication = await _playerApiClient.CreateApplicationAsync((Guid)msel.PlayerViewId, playerApplication, ct);
            // create the Player Team Application
            var mselTeamIds = await _context.Teams
                .Where(t => t.MselId == msel.Id)
                .Select(t => t.Id)
                .ToListAsync(ct);
            var userId = _user.GetId();
            var playerTeamId = await _context.TeamUsers
                .Where(tu => tu.UserId == userId && mselTeamIds.Contains(tu.TeamId))
                .Select(tu => tu.Team.PlayerTeamId)
                .SingleOrDefaultAsync(ct);
            var applicationInstanceForm = new ApplicationInstanceForm() {
                TeamId = (Guid)playerTeamId,
                ApplicationId = playerApplication.Id,
                DisplayOrder = msel.PlayerApplications.Count
            };
            var applicationInstance = await _playerApiClient.CreateApplicationInstanceAsync(applicationInstanceForm.TeamId, applicationInstanceForm, ct);

            return applicationInstance;
        }

    }

}
