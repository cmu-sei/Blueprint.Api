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
using Microsoft.EntityFrameworkCore.Migrations.Operations;

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

        public static async Task PullFromCiteAsync(Guid citeEvaluationId, CiteApiClient citeApiClient, CancellationToken ct)
        {
            try
            {
                // delete
                await citeApiClient.DeleteEvaluationAsync(citeEvaluationId, ct);
            }
            catch (System.Exception)
            {
            }
        }

        // Create a Cite Evaluation for this MSEL
        public static async Task<Evaluation> CreateEvaluationAsync(MselEntity msel, CiteApiClient citeApiClient, BlueprintContext blueprintContext, CancellationToken ct)
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
            await citeApiClient.DeleteMoveAsync(defaultMoveId, ct);

            return newEvaluation;
        }

        // Update a Cite Evaluation for this MSEL
        public static async Task ActivateAsync(Evaluation evaluation, CiteApiClient citeApiClient, BlueprintContext blueprintContext, CancellationToken ct)
        {
            await citeApiClient.UpdateEvaluationAsync(evaluation.Id, evaluation, ct);
        }

        // Create Cite Moves for this MSEL
        public static async Task CreateMovesAsync(MselEntity msel, CiteApiClient citeApiClient, BlueprintContext blueprintContext, int batchSize, CancellationToken ct)
        {
            // Build work items without starting execution
            var moves = msel.Moves.ToList();

            // Process in parallel batches
            for (int i = 0; i < moves.Count; i += batchSize)
            {
                var batch = moves.Skip(i).Take(batchSize);
                await Task.WhenAll(batch.Select(move => {
                    var citeMove = new Move() {
                        EvaluationId = (Guid)msel.CiteEvaluationId,
                        Description = move.Description,
                        MoveNumber = move.MoveNumber,
                        SituationTime = (DateTimeOffset)move.SituationTime,
                        SituationDescription = move.SituationDescription
                    };
                    return citeApiClient.CreateMoveAsync(citeMove, ct);
                }));
            }
        }

        // Create Cite Teams for this MSEL
        public static async Task CreateTeamsAsync(MselEntity msel, CiteApiClient citeApiClient, BlueprintContext blueprintContext, HashSet<Guid> citeUserIds, CancellationToken ct)
        {
            // use eager-loaded teams from the MSEL
            var teams = msel.Teams.ToList();
            foreach (var team in teams)
            {
                if (team.CiteTeamTypeId != null)
                {
                    var citeTeamId = Guid.NewGuid();
                    // create team in Cite
                    var citeTeam = new Team() {
                        Id = citeTeamId,
                        Name = team.Name,
                        ShortName = team.ShortName,
                        EvaluationId = (Guid)msel.CiteEvaluationId,
                        TeamTypeId = (Guid)team.CiteTeamTypeId
                    };
                    citeTeam = await citeApiClient.CreateTeamAsync(citeTeam, ct);
                    team.CiteTeamId = citeTeam.Id;
                    // use eager-loaded users from the team
                    var users = team.TeamUsers.Select(tu => tu.User).ToList();
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
                            citeUserIds.Add(user.Id);
                        }
                        // create Cite TeamMemberships
                        var teamMembership = new TeamMembership() {
                            TeamId = citeTeam.Id,
                            UserId = user.Id
                        };
                        try
                        {
                            await citeApiClient.CreateTeamMembershipAsync(citeTeam.Id, teamMembership, ct);
                        }
                        catch (System.Exception)
                        {}
                    }
                }
                else
                {
                    team.CiteTeamId = null;
                }
            }
            // save team CiteTeamIds
            await blueprintContext.SaveChangesAsync(ct);
        }

        // Create Cite Duties for this MSEL
        public static async Task CreateDutiesAsync(MselEntity msel, CiteApiClient citeApiClient, BlueprintContext blueprintContext, int batchSize, CancellationToken ct)
        {
            // Build work items without starting execution
            var duties = msel.CiteDuties
                .Where(duty => msel.Teams.Any(t => t.Id == duty.TeamId && t.CiteTeamId != null))
                .ToList();

            // Process in parallel batches to avoid overwhelming CITE API
            for (int i = 0; i < duties.Count; i += batchSize)
            {
                var batch = duties.Skip(i).Take(batchSize);
                await Task.WhenAll(batch.Select(duty => {
                    var citeTeamId = msel.Teams.SingleOrDefault(t => t.Id == duty.TeamId)?.CiteTeamId;
                    var citeDuty = new Duty() {
                        EvaluationId = (Guid)msel.CiteEvaluationId,
                        Name = duty.Name,
                        TeamId = (Guid)citeTeamId
                    };
                    return citeApiClient.CreateDutyAsync(citeDuty, ct);
                }));
            }
        }

        // Create Cite Articles for this MSEL
        public static async Task CreateActionsAsync(MselEntity msel, CiteApiClient citeApiClient, BlueprintContext blueprintContext, int batchSize, CancellationToken ct)
        {
            // Build work items without starting execution
            var actions = msel.CiteActions
                .Where(action => action.TeamId != null && msel.Teams.Any(t => t.Id == action.TeamId && t.CiteTeamId != null))
                .ToList();

            // Process in parallel batches to avoid overwhelming CITE API
            for (int i = 0; i < actions.Count; i += batchSize)
            {
                var batch = actions.Skip(i).Take(batchSize);
                await Task.WhenAll(batch.Select(action => {
                    var citeTeamId = msel.Teams.SingleOrDefault(t => t.Id == action.TeamId)?.CiteTeamId;
                    var citeAction = new Cite.Api.Client.Action() {
                        EvaluationId = (Guid)msel.CiteEvaluationId,
                        TeamId = (Guid)citeTeamId,
                        MoveNumber = action.MoveNumber,
                        InjectNumber = action.InjectNumber,
                        Description = action.Description
                    };
                    return citeApiClient.CreateActionAsync(citeAction, ct);
                }));
            }
        }

        // Add User to Cite Team
        public static async Task AddUserToTeamAsync(Guid userId, Guid teamId, CiteApiClient citeApiClient, BlueprintContext blueprintContext, CancellationToken ct)
        {
            // create Cite TeamMembership
            var citeTeamMembership = new TeamMembership() {
                TeamId = teamId,
                UserId = userId
            };
            await citeApiClient.CreateTeamMembershipAsync(teamId, citeTeamMembership, ct);
        }

    }
}
