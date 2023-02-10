// Copyright 2023 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using AutoMapper;
using Blueprint.Api.Infrastructure.Authorization;
using Blueprint.Api.Infrastructure.Exceptions;
using Blueprint.Api.Infrastructure.Extensions;
using Blueprint.Api.Infrastructure.Options;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Security.Claims;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using Blueprint.Api.Data;
using Blueprint.Api.Data.Models;
using Cite.Api.Client;

namespace Blueprint.Api.Services
{
    public interface ICiteService
    {
        Task<IEnumerable<ScoringModel>> GetScoringModelsAsync(CancellationToken ct);
        Task<ViewModels.Msel> PushToCiteAsync(Guid mselId, CancellationToken ct);
        Task<ViewModels.Msel> PullFromCiteAsync(Guid mselId, CancellationToken ct);
    }

    public class CiteService : ICiteService
    {
        private readonly ICiteApiClient _citeApiClient;
        private readonly ResourceOwnerAuthorizationOptions _resourceOwnerAuthorizationOptions;
        private readonly ClaimsPrincipal _user;
        private readonly IAuthorizationService _authorizationService;
        private readonly BlueprintContext _context;
        protected readonly IMapper _mapper;
        private readonly ILogger<CiteService> _logger;

        public CiteService(
            ICiteApiClient citeApiClient,
            IPrincipal user,
            BlueprintContext mselContext,
            IMapper mapper,
            IAuthorizationService authorizationService,
            ILogger<CiteService> logger,
            ResourceOwnerAuthorizationOptions resourceOwnerAuthorizationOptions)
        {
            _citeApiClient = citeApiClient;
            _resourceOwnerAuthorizationOptions = resourceOwnerAuthorizationOptions;
            _user = user as ClaimsPrincipal;
            _authorizationService = authorizationService;
            _context = mselContext;
            _mapper = mapper;
            _logger = logger;
        }

        public async Task<IEnumerable<ScoringModel>> GetScoringModelsAsync(CancellationToken ct)
        {
            var scoringModels = new List<ScoringModel>();
            try
            {
                scoringModels = (List<ScoringModel>)await _citeApiClient.GetScoringModelsAsync("", "", false);
            }
            catch (System.Exception)
            {
            }
            return (IEnumerable<ScoringModel>)scoringModels;

        }

        public async Task<ViewModels.Msel> PushToCiteAsync(Guid mselId, CancellationToken ct)
        {
            // user must be a Content Developer or a MSEL owner
            if (!(await _authorizationService.AuthorizeAsync(_user, null, new ContentDeveloperRequirement())).Succeeded &&
                !(await MselOwnerRequirement.IsMet(_user.GetId(), mselId, _context)))
                throw new ForbiddenException();
            // get the MSEL and verify data state
            var msel = await _context.Msels
                .Include(m => m.CiteActions)
                .Include(m => m.CiteRoles)
                .SingleOrDefaultAsync(m => m.Id == mselId);
            if (msel == null)
                throw new EntityNotFoundException<MselEntity>($"MSEL {mselId} was not found when attempting to create a collection.");
            if (msel.CiteEvaluationId != null)
                throw new InvalidOperationException($"MSEL {mselId} is already associated to a Cite Evaluation.");
            // start a transaction, because we will modify many database items
            await _context.Database.BeginTransactionAsync();
            // create the Cite Evaluation
            await CreateEvaluationAsync(msel, ct);
            // create the Cite Teams
            await CreateTeamsAsync(msel, ct);
            // create the Cite Roles
            await CreateRolesAsync(msel, ct);
            // create the Cite Actions
            await CreateActionsAsync(msel, ct);
            // commit the transaction
            await _context.Database.CommitTransactionAsync(ct);

            return _mapper.Map<ViewModels.Msel>(msel); 
        }

        public async Task<ViewModels.Msel> PullFromCiteAsync(Guid mselId, CancellationToken ct)
        {
            // user must be a Content Developer or a MSEL owner
            if (!(await _authorizationService.AuthorizeAsync(_user, null, new ContentDeveloperRequirement())).Succeeded &&
                !(await MselOwnerRequirement.IsMet(_user.GetId(), mselId, _context)))
                throw new ForbiddenException();
            // get the MSEL and verify data state
            var msel = await _context.Msels.FindAsync(mselId);
            if (msel == null)
                throw new EntityNotFoundException<MselEntity>($"MSEL {mselId} was not found when attempting to create a collection.");
            if (msel.CiteEvaluationId == null)
                throw new InvalidOperationException($"MSEL {mselId} is not associated to a Cite Evaluation.");
            // delete
            await _citeApiClient.DeleteEvaluationAsync((Guid)msel.CiteEvaluationId, ct);
            // update the MSEL
            msel.CiteEvaluationId = null;
            // save the changes
            await _context.SaveChangesAsync(ct);

            return _mapper.Map<ViewModels.Msel>(msel); 
        }

        //
        // Helper methods
        //

        // Create a Cite Evaluation for this MSEL
        private async Task CreateEvaluationAsync(MselEntity msel, CancellationToken ct)
        {
            Evaluation newEvaluation = new Evaluation() {
                Description = msel.Description,
                Status = ItemStatus.Pending,
                CurrentMoveNumber = 0,
                ScoringModelId = (Guid)msel.CiteScoringModelId
            };
            newEvaluation = await _citeApiClient.CreateEvaluationAsync(newEvaluation, ct);
            // update the MSEL
            msel.CiteEvaluationId = newEvaluation.Id;
            await _context.SaveChangesAsync(ct);
        }

        // Create Cite Teams for this MSEL
        private async Task CreateTeamsAsync(MselEntity msel, CancellationToken ct)
        {
            // get the Cite teams, Cite Users, and the Cite TeamUsers
            var citeTeams = await _citeApiClient.GetTeamsAsync(ct);
            var citeUserIds = (await _citeApiClient.GetUsersAsync(ct)).Select(u => u.Id);
            var citeTeamUsers = await _citeApiClient.GetTeamUsersAsync(ct);
            // get the teams for this MSEL and loop through them
            var teams = await _context.MselTeams
                .Where(mt => mt.MselId == msel.Id)
                .Select(mt => mt.Team)
                .ToListAsync();
            foreach (var team in teams)
            {
                // if this team doesn't exist in Cite, create it
                var citeTeam = citeTeams.FirstOrDefault(t => t.Id == team.Id);
                if (citeTeam == null)
                {
                    citeTeam = new Team() {
                        Id = team.Id,
                        Name = team.Name,
                        ShortName = team.ShortName,
                        TeamTypeId = 
                    };
                    citeTeam = await _citeApiClient.CreateTeamAsync(citeTeam, ct);
                }
                // add the cite team to the cite evaluation, if necessary
                var citeExhibitTeam = new EvaluationTeam() {
                    TeamId = team.Id,
                    EvaluationId = (Guid)msel.CiteEvaluationId
                };
                await _citeApiClient.CreateEvaluationTeamAsync(citeExhibitTeam, ct);
                // get all of the users for this team and loop through them
                var users = await _context.TeamUsers
                    .Where(tu => tu.TeamId == team.Id)
                    .Select(tu => tu.User)
                    .ToListAsync(ct);
                foreach (var user in users)
                {
                    // if this user is not in Cite, add it
                    if (!citeUserIds.Contains(user.Id))
                    {
                        var newUser = new User() {
                            Id = user.Id,
                            Name = user.Name
                        };
                        await _citeApiClient.CreateUserAsync(newUser, ct);
                    }
                    // if there is no Cite TeamUser, create it
                    if (!citeTeamUsers.Any(tu => tu.TeamId == citeTeam.Id && tu.UserId == user.Id))
                    {
                        var teamUser = new TeamUser() {
                            TeamId = citeTeam.Id,
                            UserId = user.Id
                        };
                        await _citeApiClient.CreateTeamUserAsync(teamUser, ct);
                    }
                }
            }
        }

        // Create Cite Roles for this MSEL
        private async Task CreateRolesAsync(MselEntity msel, CancellationToken ct)
        {
            foreach (var role in msel.CiteRoles)
            {
                Role citeRole = new Role() {
                    EvaluationId = (Guid)msel.CiteEvaluationId,
                    Name = role.Name,
                    TeamId = role.TeamId
                };
                citeRole = await _citeApiClient.CreateRoleAsync(citeRole, ct);
                await _context.SaveChangesAsync(ct);
            }
        }

        // Create Cite Articles for this MSEL
        private async Task CreateActionsAsync(MselEntity msel, CancellationToken ct)
        {
            foreach (var action in msel.CiteActions)
            {
                // create the action
                Cite.Api.Client.Action citeAction = new Cite.Api.Client.Action() {
                    EvaluationId = (Guid)msel.CiteEvaluationId,
                    TeamId = action.TeamId,
                    MoveNumber = action.MoveNumber,
                    InjectNumber = action.InjectNumber,
                    Description = action.Description
                };
                citeAction = await _citeApiClient.CreateActionAsync(citeAction, ct);
            }
        }

    }
}

