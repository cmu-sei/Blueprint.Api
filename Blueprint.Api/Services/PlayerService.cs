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
using Blueprint.Api.Data;
using Blueprint.Api.Data.Models;
using Blueprint.Api.Hubs;
using Blueprint.Api.Infrastructure.Authorization;
using Blueprint.Api.Infrastructure.Exceptions;
using Blueprint.Api.Infrastructure.Extensions;
using Blueprint.Api.Infrastructure.Options;
using Player.Api.Client;
using Blueprint.Api.ViewModels;
using Blueprint.Api.Infrastructure.Mappings;

namespace Blueprint.Api.Services
{
    public interface IPlayerService
    {
        Task<IEnumerable<ApplicationTemplate>> GetApplicationTemplatesAsync(CancellationToken ct);
        Task<IEnumerable<View>> GetMyViewsAsync(CancellationToken ct);
        Task PushApplication(PlayerApplication playerApplication, CancellationToken ct);
        Task EnsurePushableAsync(Guid mselId, string url, CancellationToken ct);
    }

    public class PlayerService : IPlayerService
    {
        private readonly IPlayerApiClient _playerApiClient;
        private readonly BlueprintContext _context;
        private readonly IAuthorizationService _authorizationService;
        private readonly ClaimsPrincipal _user;
        private readonly IHubContext<MainHub> _hubContext;
        private readonly IAddApplicationQueue _addApplicationQueue;
        private readonly ClientOptions _clientOptions;

        public PlayerService(
            IHttpContextAccessor httpContextAccessor,
            IPlayerApiClient playerApiClient,
            IAuthorizationService authorizationService,
            IPrincipal user,
            IUserClaimsService claimsService,
            BlueprintContext context,
            IHubContext<MainHub> hubContext,
            IAddApplicationQueue addApplicationQueue,
            ClientOptions clientOptions)

        {
            _playerApiClient = playerApiClient;
            _user = user as ClaimsPrincipal;
            _context = context;
            _hubContext = hubContext;
            _authorizationService = authorizationService;
            _addApplicationQueue = addApplicationQueue;
            _clientOptions = clientOptions;
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

        /// <summary>
        /// Refuses an application that cannot be pushed to the MSEL's Player view: the MSEL has none, or the url,
        /// filled in as the full push fills it, is one Player would refuse or Blueprint cannot send. Checked on
        /// the request rather than in the background worker, which could only log the failure.
        /// </summary>
        public async Task EnsurePushableAsync(Guid mselId, string url, CancellationToken ct)
        {
            var msel = await _context.Msels.AsNoTracking().SingleOrDefaultAsync(m => m.Id == mselId, ct);
            ResolvePushable(msel, url);
        }

        // the url filled in for the push, or a ConflictException
        private string ResolvePushable(MselEntity msel, string url)
        {
            if (msel?.PlayerViewId == null)
                throw new ConflictException("The MSEL has not been pushed to Player, so the application cannot be added to its view.");

            var resolved = PlayerApplicationUrls.Resolve(url, msel, _clientOptions);
            var problem = PlayerApplicationUrls.ResolvedProblem(resolved);
            if (problem != null)
                throw new ConflictException(problem);

            return resolved;
        }

        public async Task PushApplication(PlayerApplication application, CancellationToken ct)
        {
            var msel = await _context.Msels.SingleOrDefaultAsync(m => m.Id == application.MselId, ct);
            var url = ResolvePushable(msel, application.Url);

            var playerApplication = new Application() {
                Name = application.Name,
                Embeddable = application.Embeddable,
                ViewId = (Guid)msel.PlayerViewId,
                Url = PlayerApplicationUrls.ToPlayerUri(url),
                Icon = PlayerApplicationUrls.Resolve(application.Icon, msel, _clientOptions),
                LoadInBackground = application.LoadInBackground
            };
            // get the Player Team ID
            var mselTeamIds = await _context.Teams
                .Where(t => t.MselId == msel.Id)
                .Select(t => t.Id)
                .ToListAsync(ct);
            var userId = _user.GetId();
            var teamId = await _context.TeamUsers
                .Where(tu => tu.UserId == userId && mselTeamIds.Contains(tu.TeamId))
                .Select(tu => tu.TeamId)
                .SingleOrDefaultAsync(ct);
            var addApplicationInformation = new AddApplicationInformation{
                Application = playerApplication,
                TeamId = teamId,
                DisplayOrder = msel.PlayerApplications.Count + 1
            };
            _addApplicationQueue.Add(addApplicationInformation);
        }

    }

}
