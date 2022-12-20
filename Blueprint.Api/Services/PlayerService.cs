// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Data;
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
using Blueprint.Api.Infrastructure.Authorization;
using Blueprint.Api.Infrastructure.Exceptions;

namespace Blueprint.Api.Services
{
    public interface IPlayerService
    {
        Task<IEnumerable<View>> GetViewsAsync(CancellationToken ct);
        Task<IEnumerable<Team>> GetViewTeamsAsync(Guid viewId, CancellationToken ct);
        Task<IEnumerable<User>> GetViewTeamUsersAsync(Guid teamId, CancellationToken ct);
        Task AddPlayerTeamsToMselAsync(Guid mselId, CancellationToken ct);
    }

    public class PlayerService : IPlayerService
    {
        private readonly IPlayerApiClient _playerApiClient;
        private readonly BlueprintContext _context;
        private readonly IAuthorizationService _authorizationService;
        private readonly IMapper _mapper;
        private readonly ClaimsPrincipal _user;

        public PlayerService(
            IHttpContextAccessor httpContextAccessor,
            IPlayerApiClient playerApiClient,
            IAuthorizationService authorizationService,
            IPrincipal user,
            IUserClaimsService claimsService,
            BlueprintContext context,
            IMapper mapper)

        {
            _playerApiClient = playerApiClient;
            _user = user as ClaimsPrincipal;
            _context = context;
            _authorizationService = authorizationService;
            _mapper = mapper;
        }

        public async Task<IEnumerable<View>> GetViewsAsync(CancellationToken ct)
        {
            var views = new List<View>();
            try
            {
                views = (List<View>)await _playerApiClient.GetUserViewsAsync(_user.GetId(), ct);
            }
            catch (System.Exception)
            {
            }
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

        public async Task AddPlayerTeamsToMselAsync(Guid mselId, CancellationToken ct)
        {
            var msel = await _context.Msels.SingleOrDefaultAsync(v => v.Id == mselId, ct);
            if (msel == null)
                throw new EntityNotFoundException<MselEntity>();

            if (msel.PlayerViewId == null)
                throw new DataException($"The MSEL ({mselId}) is not linked to a Player View.");

            // user must be a Content Developer or a MSEL owner
            if (!(await _authorizationService.AuthorizeAsync(_user, null, new ContentDeveloperRequirement())).Succeeded &&
                !(await MselOwnerRequirement.IsMet(_user.GetId(), msel.Id, _context)))
                throw new ForbiddenException();

            var playerTeams = await GetViewTeamsAsync((Guid)msel.PlayerViewId, ct);
            foreach (var playerTeam in playerTeams)
            {
                var team = await _context.Teams.FirstOrDefaultAsync(t => t.Name == playerTeam.Name || t.ShortName == playerTeam.Name);
                if (team == null)
                {
                    team = new TeamEntity();
                    team.PlayerTeamId = playerTeam.Id;
                    team.Name = playerTeam.Name;
                    team.ShortName =playerTeam.Name;
                    team.IsParticipantTeam = true;
                    _context.Teams.Add(team);
                }
                else
                {
                    team.PlayerTeamId = playerTeam.Id;
                    team.IsParticipantTeam = true;
                    _context.Teams.Update(team);
                }
                var mselTeam = await _context.MselTeams.FirstOrDefaultAsync(mt => mt.Team.PlayerTeamId == playerTeam.Id && mt.MselId == mselId);
                if (mselTeam == null)
                {
                    mselTeam = new MselTeamEntity();
                    mselTeam.TeamId = team.Id;
                    mselTeam.MselId = mselId;
                    _context.MselTeams.Add(mselTeam);
                }
            }
            await _context.SaveChangesAsync(ct);
        }

    }
}
