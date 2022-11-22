// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using Blueprint.Api.Infrastructure.Extensions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Player.Api.Client;

namespace Blueprint.Api.Services
{
    public interface IPlayerService
    {
        Task<IEnumerable<View>> GetViewsAsync(CancellationToken ct);
        Task<IEnumerable<Team>> GetViewTeamsAsync(Guid viewId, CancellationToken ct);
        Task<IEnumerable<User>> GetViewTeamUsersAsync(Guid teamId, CancellationToken ct);
    }

    public class PlayerService : IPlayerService
    {
        private readonly IPlayerApiClient _playerApiClient;
        private readonly ClaimsPrincipal _user;

        public PlayerService(
            IHttpContextAccessor httpContextAccessor,
            IPlayerApiClient playerApiClient,
            IAuthorizationService authorizationService,
            IPrincipal user,
            IUserClaimsService claimsService)
        {
            _playerApiClient = playerApiClient;
            _user = user as ClaimsPrincipal;
        }

        public async Task<IEnumerable<View>> GetViewsAsync(CancellationToken ct)
        {
            var views = await _playerApiClient.GetUserViewsAsync(_user.GetId(), ct);
            return (IEnumerable<View>)views;
        }

        public async Task<IEnumerable<Team>> GetViewTeamsAsync(Guid viewId, CancellationToken ct)
        {
            var teams = await _playerApiClient.GetViewTeamsAsync(viewId, ct);
            return (IEnumerable<Team>)teams;
        }

        public async Task<IEnumerable<User>> GetViewTeamUsersAsync(Guid teamId, CancellationToken ct)
        {
            var users = await _playerApiClient.GetTeamUsersAsync(teamId, ct);
            return (IEnumerable<User>)users;
        }

    }
}
