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
using Player.Api.Client;
using Blueprint.Api.Data;
using Blueprint.Api.Data.Models;

namespace Blueprint.Api.Infrastructure.Extensions
{
    public static class IntegrationPlayerExtensions
    {
        public static PlayerApiClient GetPlayerApiClient(IHttpClientFactory httpClientFactory, string apiUrl, TokenResponse tokenResponse)
        {
            var client = ApiClientsExtensions.GetHttpClient(httpClientFactory, apiUrl, tokenResponse);
            var apiClient = new PlayerApiClient(client);
            return apiClient;
        }

        public static async Task PullFromPlayerAsync(MselEntity msel, PlayerApiClient playerApiClient, BlueprintContext blueprintContext, CancellationToken ct)
        {
            try
            {
                // delete
                await playerApiClient.DeleteViewAsync((Guid)msel.PlayerViewId, ct);
            }
            catch (System.Exception)
            {
            }
            // update the MSEL
            msel.PlayerViewId = null;
            // save the changes
            await blueprintContext.SaveChangesAsync(ct);
        }

        // Create a Player View for this MSEL
        public static async Task CreateViewAsync(MselEntity msel, PlayerApiClient playerApiClient, BlueprintContext blueprintContext, CancellationToken ct)
        {
            ViewForm viewForm = new ViewForm() {
                Name = msel.Name,
                Description = msel.Description,
                Status = ViewStatus.Active,
                CreateAdminTeam = true
            };
            var newView = await playerApiClient.CreateViewAsync(viewForm, ct);
            // update the MSEL
            msel.PlayerViewId = newView.Id;
            await blueprintContext.SaveChangesAsync(ct);
        }

        // Create Player Teams for this MSEL
        public static async Task<Dictionary<Guid, Guid>> CreateTeamsAsync(MselEntity msel, PlayerApiClient playerApiClient, BlueprintContext blueprintContext, CancellationToken ct)
        {
            var playerTeamDictionary = new Dictionary<Guid, Guid>();
            // get the Player teams, Player Users, and the Player TeamUsers
            var playerUserIds = (await playerApiClient.GetUsersAsync(ct)).Select(u => u.Id);
            // get the teams for this MSEL and loop through them
            var mselTeams = await blueprintContext.MselTeams
                .Where(mt => mt.MselId == msel.Id)
                .Include(mt => mt.Team)
                .ToListAsync();
            foreach (var mselTeam in mselTeams)
            {
                // create team in Player
                var playerTeamForm = new TeamForm() {
                    Name = mselTeam.Team.Name
                };
                var playerTeam = await playerApiClient.CreateTeamAsync((Guid)msel.PlayerViewId, playerTeamForm, ct);
                playerTeamDictionary.Add(mselTeam.Team.Id, playerTeam.Id);
                // get all of the users for this team and loop through them
                var users = await blueprintContext.TeamUsers
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
                        await playerApiClient.CreateUserAsync(newUser, ct);
                    }
                    // create Player TeamUsers
                    await playerApiClient.AddUserToTeamAsync(playerTeam.Id, user.Id, ct);
                }
            }

            return playerTeamDictionary;
        }

        // Create Player Applications for this MSEL
        public static async Task CreateApplicationsAsync(MselEntity msel, Dictionary<Guid, Guid> playerTeamDictionary, PlayerApiClient playerApiClient, BlueprintContext blueprintContext, CancellationToken ct)
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
                playerApplication = await playerApiClient.CreateApplicationAsync((Guid)msel.PlayerViewId, playerApplication, ct);
                // create the Player Team Applications
                var applicationTeams = await blueprintContext.PlayerApplicationTeams
                    .Where(ct => ct.PlayerApplicationId == application.Id)
                    .ToListAsync(ct);
                foreach (var applicationTeam in applicationTeams)
                {
                    var applicationInstanceForm = new ApplicationInstanceForm() {
                        TeamId = playerTeamDictionary[applicationTeam.TeamId],
                        ApplicationId = (Guid)playerApplication.Id
                    };
                    await playerApiClient.CreateApplicationInstanceAsync(applicationInstanceForm.TeamId, applicationInstanceForm, ct);
                }
            }
        }

    }
}
