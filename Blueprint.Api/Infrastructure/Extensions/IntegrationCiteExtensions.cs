// Copyright 2024 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using IdentityModel.Client;
using Cite.Api.Client;
using Blueprint.Api.Data;
using Blueprint.Api.Data.Enumerations;
using Blueprint.Api.Data.Models;

namespace Blueprint.Api.Infrastructure.Extensions
{
    public static class IntegrationCiteExtensions
    {
        public static CiteApiClient GetCiteApiClient(IHttpClientFactory httpClientFactory, string apiUrl, TokenResponse tokenResponse)
        {
            var client = ApiClientsExtensions.GetHttpClient(httpClientFactory, apiUrl, tokenResponse);
            var apiClient = new CiteApiClient(client);
            return apiClient;
        }

        public static async Task PullFromCiteAsync(MselEntity msel, CiteApiClient citeApiClient, BlueprintContext blueprintContext, CancellationToken ct)
        {
            try
            {
                // delete
                await citeApiClient.DeleteEvaluationAsync((Guid)msel.CiteEvaluationId, ct);
            }
            catch (System.Exception)
            {
            }
            // update the MSEL
            msel.CiteEvaluationId = null;
            // save the changes
            await blueprintContext.SaveChangesAsync(ct);
        }

        // Create a Cite Evaluation for this MSEL
        public static async Task CreateEvaluationAsync(MselEntity msel, CiteApiClient citeApiClient, BlueprintContext blueprintContext, CancellationToken ct)
        {
            var move0 = msel.Moves.SingleOrDefault(m => m.MoveNumber == 0);
            Evaluation newEvaluation = new Evaluation() {
                Description = msel.Name,
                Status = Cite.Api.Client.ItemStatus.Pending,
                CurrentMoveNumber = 0,
                ScoringModelId = (Guid)msel.CiteScoringModelId,
                GalleryExhibitId = msel.GalleryExhibitId,
                SituationDescription = "Preparing for the start of the exercise."
            };
            if (move0 != null)
            {
                newEvaluation.SituationDescription = move0.SituationDescription;
                newEvaluation.SituationTime = (DateTimeOffset)move0.SituationTime;
            }
            newEvaluation = await citeApiClient.CreateEvaluationAsync(newEvaluation, ct);
            // update the MSEL
            msel.CiteEvaluationId = newEvaluation.Id;
            await blueprintContext.SaveChangesAsync(ct);
            // delete the default move 0 that was created when the evaluation was created
            var defaultMoveId = newEvaluation.Moves.Single().Id;
            await citeApiClient.DeleteMoveAsync(defaultMoveId);
        }

        // Create Cite Moves for this MSEL
        public static async Task CreateMovesAsync(MselEntity msel, CiteApiClient citeApiClient, BlueprintContext blueprintContext, CancellationToken ct)
        {
            foreach (var move in msel.Moves)
            {
                Move citeMove = new Move() {
                    EvaluationId = (Guid)msel.CiteEvaluationId,
                    Description = move.Description,
                    MoveNumber = move.MoveNumber,
                    SituationTime = (DateTimeOffset)move.SituationTime,
                    SituationDescription = move.SituationDescription
                };
                await citeApiClient.CreateMoveAsync(citeMove, ct);
                await blueprintContext.SaveChangesAsync(ct);
            }
        }

        // Create Cite Teams for this MSEL
        public static async Task<Dictionary<Guid, Guid>> CreateTeamsAsync(MselEntity msel, CiteApiClient citeApiClient, BlueprintContext blueprintContext, CancellationToken ct)
        {
            var citeTeamDictionary = new Dictionary<Guid, Guid>();
            // get the Cite teams, Cite Users, and the Cite TeamUsers
            var citeUserIds = (await citeApiClient.GetUsersAsync(ct)).Select(u => u.Id);
            // get the teams for this MSEL and loop through them
            var mselTeams = await blueprintContext.MselTeams
                .Where(mt => mt.MselId == msel.Id)
                .Include(mt => mt.Team)
                .ToListAsync();
            foreach (var mselTeam in mselTeams)
            {
                if (mselTeam.CiteTeamTypeId != null)
                {
                    var citeTeamId = Guid.NewGuid();
                    // create team in Cite
                    var citeTeam = new Team() {
                        Id = citeTeamId,
                        Name = mselTeam.Team.Name,
                        ShortName = mselTeam.Team.ShortName,
                        EvaluationId = (Guid)msel.CiteEvaluationId,
                        TeamTypeId = (Guid)mselTeam.CiteTeamTypeId
                    };
                    citeTeam = await citeApiClient.CreateTeamAsync(citeTeam, ct);
                    citeTeamDictionary.Add(mselTeam.TeamId, citeTeam.Id);
                    // get all of the users for this team and loop through them
                    var users = await blueprintContext.TeamUsers
                        .Where(tu => tu.TeamId == mselTeam.Team.Id)
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
                            await citeApiClient.CreateUserAsync(newUser, ct);
                        }
                        // create Cite TeamUsers
                        var isObserver = await blueprintContext.UserMselRoles
                            .AnyAsync(umr => umr.UserId == user.Id && umr.MselId == msel.Id && umr.Role == MselRole.CiteObserver);
                        var teamUser = new TeamUser() {
                            TeamId = citeTeam.Id,
                            UserId = user.Id,
                            IsObserver = isObserver
                        };
                        await citeApiClient.CreateTeamUserAsync(teamUser, ct);
                    }
                }
                else
                {
                    citeTeamDictionary.Add(mselTeam.TeamId, Guid.Empty);
                }
            }

            return citeTeamDictionary;
        }

        // Create Cite Roles for this MSEL
        public static async Task CreateRolesAsync(MselEntity msel, Dictionary<Guid, Guid> citeTeamDictionary, CiteApiClient citeApiClient, BlueprintContext blueprintContext, CancellationToken ct)
        {
            foreach (var role in msel.CiteRoles)
            {
                var citeTeamId = citeTeamDictionary[(Guid)role.TeamId];
                if (citeTeamId != Guid.Empty)
                {
                    Role citeRole = new Role() {
                        EvaluationId = (Guid)msel.CiteEvaluationId,
                        Name = role.Name,
                        TeamId = citeTeamId
                    };
                    await citeApiClient.CreateRoleAsync(citeRole, ct);
                    await blueprintContext.SaveChangesAsync(ct);
                }
            }
        }

        // Create Cite Articles for this MSEL
        public static async Task CreateActionsAsync(MselEntity msel, Dictionary<Guid, Guid> citeTeamDictionary, CiteApiClient citeApiClient, BlueprintContext blueprintContext, CancellationToken ct)
        {
            foreach (var action in msel.CiteActions)
            {
                var citeTeamId = citeTeamDictionary[(Guid)action.TeamId];
                if (citeTeamId != Guid.Empty)
                {
                    // create the action
                    Cite.Api.Client.Action citeAction = new Cite.Api.Client.Action() {
                        EvaluationId = (Guid)msel.CiteEvaluationId,
                        TeamId = citeTeamId,
                        MoveNumber = action.MoveNumber,
                        InjectNumber = action.InjectNumber,
                        Description = action.Description
                    };
                    await citeApiClient.CreateActionAsync(citeAction, ct);
                }
            }
        }

    }
}
