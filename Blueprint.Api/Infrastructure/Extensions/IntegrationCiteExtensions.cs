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
                Status = Cite.Api.Client.ItemStatus.Active,
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
        public static async Task CreateTeamsAsync(MselEntity msel, CiteApiClient citeApiClient, BlueprintContext blueprintContext, CancellationToken ct)
        {
            // get the Cite teams, Cite Users, and the Cite TeamUsers
            var citeUserIds = (await citeApiClient.GetUsersAsync(ct)).Select(u => u.Id);
            // get the teams for this MSEL and loop through them
            var teams = await blueprintContext.Teams
                .Where(t => t.MselId == msel.Id)
                .Include(t => t.UserTeamRoles)
                .ToListAsync();
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
                    // get all of the users for this team and loop through them
                    var users = await blueprintContext.TeamUsers
                        .Where(tu => tu.TeamId == team.Id)
                        .Select(tu => tu.User)
                        .ToListAsync(ct);
                    var citePermissions = await citeApiClient.GetPermissionsAsync(ct);
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
                        // create UserPermissions
                        try
                        {
                            var userPermission = new UserPermission{UserId = user.Id, PermissionId = citePermissions.SingleOrDefault(p => p.Key == "CanModify").Id};
                            await citeApiClient.CreateUserPermissionAsync(userPermission, ct);
                            userPermission = new UserPermission{UserId = user.Id, PermissionId = citePermissions.SingleOrDefault(p => p.Key == "CanSubmit").Id};
                            await citeApiClient.CreateUserPermissionAsync(userPermission, ct);
                            if (team.UserTeamRoles.Any(x => x.UserId == user.Id && x.Role == TeamRole.CiteIncrementer))
                            {
                                userPermission = new UserPermission{UserId = user.Id, PermissionId = citePermissions.SingleOrDefault(p => p.Key == "CanIncrementMove").Id};
                                await citeApiClient.CreateUserPermissionAsync(userPermission, ct);
                            }
                        }
                        catch (Exception ex)
                        {}
                        // create Cite TeamUsers
                        var isObserver = await blueprintContext.UserTeamRoles
                            .AnyAsync(umr => umr.UserId == user.Id && umr.TeamId == team.Id && umr.Role == TeamRole.CiteObserver);
                        var teamUser = new TeamUser() {
                            TeamId = citeTeam.Id,
                            UserId = user.Id,
                            IsObserver = isObserver
                        };
                        try
                        {
                            await citeApiClient.CreateTeamUserAsync(teamUser, ct);
                        }
                        catch (Exception ex)
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

        // Create Cite Roles for this MSEL
        public static async Task CreateRolesAsync(MselEntity msel, CiteApiClient citeApiClient, BlueprintContext blueprintContext, CancellationToken ct)
        {
            foreach (var role in msel.CiteRoles)
            {
                var citeTeamId = msel.Teams.SingleOrDefault(t => t.Id == role.TeamId)?.CiteTeamId;
                if (citeTeamId != null)
                {
                    Role citeRole = new Role() {
                        EvaluationId = (Guid)msel.CiteEvaluationId,
                        Name = role.Name,
                        TeamId = (Guid)citeTeamId
                    };
                    await citeApiClient.CreateRoleAsync(citeRole, ct);
                    await blueprintContext.SaveChangesAsync(ct);
                }
            }
        }

        // Create Cite Articles for this MSEL
        public static async Task CreateActionsAsync(MselEntity msel, CiteApiClient citeApiClient, BlueprintContext blueprintContext, CancellationToken ct)
        {
            foreach (var action in msel.CiteActions)
            {
                var citeTeamId = msel.Teams.SingleOrDefault(t => t.Id == (Guid)action.TeamId)?.CiteTeamId;
                if (citeTeamId != null)
                {
                    // create the action
                    Cite.Api.Client.Action citeAction = new Cite.Api.Client.Action() {
                        EvaluationId = (Guid)msel.CiteEvaluationId,
                        TeamId = (Guid)citeTeamId,
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
