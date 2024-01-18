// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Data;
using System.Linq;
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
        Task<IEnumerable<View>> GetViewsAsync(CancellationToken ct);
        Task<IEnumerable<Team>> GetViewTeamsAsync(Guid viewId, CancellationToken ct);
        Task<IEnumerable<User>> GetViewTeamUsersAsync(Guid teamId, CancellationToken ct);
        Task<IEnumerable<ApplicationTemplate>> GetApplicationTemplatesAsync(CancellationToken ct);
        Task AddPlayerTeamsToMselAsync(Guid mselId, CancellationToken ct);
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
            // start a transaction, because we will modify many database items
            await _context.Database.BeginTransactionAsync();
            // create the Player View
            await _hubContext.Clients.Group(mselId.ToString()).SendAsync(MainHubMethods.MselPushStatusChange, msel.Id + ",Pushing View to Player", null, ct);
            await CreateViewAsync(msel, ct);
            // create the Player Teams
            await _hubContext.Clients.Group(mselId.ToString()).SendAsync(MainHubMethods.MselPushStatusChange, msel.Id + ",Pushing Teams to Player", null, ct);
            var playerTeamDictionary = await CreateTeamsAsync(msel, ct);
            // create the Player Applications
            await _hubContext.Clients.Group(mselId.ToString()).SendAsync(MainHubMethods.MselPushStatusChange, msel.Id + ",Pushing Applications to Player", null, ct);
            await CreateApplicationsAsync(msel, playerTeamDictionary, ct);
            // commit the transaction
            await _hubContext.Clients.Group(mselId.ToString()).SendAsync(MainHubMethods.MselPushStatusChange, msel.Id + ",Commit to Player", null, ct);
            await _context.Database.CommitTransactionAsync(ct);
            await _hubContext.Clients.Group(mselId.ToString()).SendAsync(MainHubMethods.MselPushStatusChange, msel.Id + ", ", null, ct);

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
            // delete
            try
            {
                await _playerApiClient.DeleteViewAsync((Guid)msel.PlayerViewId, ct);
            }
            catch (System.Exception)
            {
            }
            // update the MSEL
            msel.PlayerViewId = null;
            // save the changes
            await _context.SaveChangesAsync(ct);

            return _mapper.Map<ViewModels.Msel>(msel); 
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

        //
        // Helper methods
        //

        // Create a Player View for this MSEL
        private async Task CreateViewAsync(MselEntity msel, CancellationToken ct)
        {
            ViewForm viewForm = new ViewForm() {
                Name = msel.Name,
                Description = msel.Description,
                Status = ViewStatus.Active,
                CreateAdminTeam = true
            };
            var newView = await _playerApiClient.CreateViewAsync(viewForm, ct);
            // update the MSEL
            msel.PlayerViewId = newView.Id;
            await _context.SaveChangesAsync(ct);
        }

        // Create Player Teams for this MSEL
        private async Task<Dictionary<Guid, Guid>> CreateTeamsAsync(MselEntity msel, CancellationToken ct)
        {
            var playerTeamDictionary = new Dictionary<Guid, Guid>();
            // get the Player teams, Player Users, and the Player TeamUsers
            var playerUserIds = (await _playerApiClient.GetUsersAsync(ct)).Select(u => u.Id);
            // get the teams for this MSEL and loop through them
            var mselTeams = await _context.MselTeams
                .Where(mt => mt.MselId == msel.Id)
                .Include(mt => mt.Team)
                .ToListAsync();
            foreach (var mselTeam in mselTeams)
            {
                // create team in Player
                var playerTeamForm = new TeamForm() {
                    Name = mselTeam.Team.Name
                };
                var playerTeam = await _playerApiClient.CreateTeamAsync((Guid)msel.PlayerViewId, playerTeamForm, ct);
                playerTeamDictionary.Add(mselTeam.Team.Id, playerTeam.Id);
                // get all of the users for this team and loop through them
                var users = await _context.TeamUsers
                    .Where(tu => tu.TeamId == mselTeam.Team.Id)
                    .Select(tu => tu.User)
                    .ToListAsync(ct);
                foreach (var user in users)
                {
                    // if this user is not in Player, add it
                    if (!playerUserIds.Contains(user.Id))
                    {
                        var newUser = new User() {
                            Id = user.Id,
                            Name = user.Name
                        };
                        await _playerApiClient.CreateUserAsync(newUser, ct);
                    }
                    // create Player TeamUsers
                    await _playerApiClient.AddUserToTeamAsync(playerTeam.Id, user.Id, ct);
                }
            }

            return playerTeamDictionary;
        }

        // Create Player Applications for this MSEL
        private async Task CreateApplicationsAsync(MselEntity msel, Dictionary<Guid, Guid> playerTeamDictionary, CancellationToken ct)
        {
            foreach (var application in msel.PlayerApplications)
            {
                var playerApplication = new Application() {
                    Name = application.Name,
                    Embeddable = application.Embeddable,
                    ViewId = (Guid)msel.PlayerViewId,
                    Url = new Uri(application.Url),
                    Icon = application.Icon,
                    LoadInBackground = application.LoadInBackground
                };
                playerApplication = await _playerApiClient.CreateApplicationAsync((Guid)msel.PlayerViewId, playerApplication, ct);
                // create the Player Team Applications
                var applicationTeams = await _context.PlayerApplicationTeams
                    .Where(ct => ct.PlayerApplicationId == application.Id)
                    .ToListAsync(ct);
                foreach (var applicationTeam in applicationTeams)
                {
                    var applicationInstanceForm = new ApplicationInstanceForm() {
                        TeamId = playerTeamDictionary[applicationTeam.TeamId],
                        ApplicationId = (Guid)playerApplication.Id
                    };
                    await _playerApiClient.CreateApplicationInstanceAsync(applicationInstanceForm.TeamId, applicationInstanceForm, ct);
                }
            }
        }

    }
}
