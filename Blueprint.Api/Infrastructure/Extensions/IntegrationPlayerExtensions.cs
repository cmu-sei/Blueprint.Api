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

        public static async Task PullFromPlayerAsync(Guid playerViewId, PlayerApiClient playerApiClient, CancellationToken ct)
        {
            try
            {
                // delete
                await playerApiClient.DeleteViewAsync(playerViewId, ct);
            }
            catch (System.Exception)
            {
            }
        }

        // Create a Player View for this MSEL
        public static async Task CreateViewAsync(MselEntity msel, Guid? playerViewId, PlayerApiClient playerApiClient, BlueprintContext blueprintContext, CancellationToken ct)
        {
            ViewForm viewForm = new ViewForm() {
                Name = msel.Name,
                Description = msel.Description,
                Status = ViewStatus.Active,
                CreateAdminTeam = true
            };
            if (playerViewId != null) viewForm.Id = (Guid)playerViewId;
            var newView = await playerApiClient.CreateViewAsync(viewForm, ct);
            // update the MSEL
            msel.PlayerViewId = newView.Id;
            await blueprintContext.SaveChangesAsync(ct);
        }

        // Create Player Teams for this MSEL
        public static async Task CreateTeamsAsync(MselEntity msel, PlayerApiClient playerApiClient, BlueprintContext blueprintContext, CancellationToken ct)
        {
            var playerTeamDictionary = new Dictionary<Guid, Guid>();
            // get the Player teams, Player Users, and the Player TeamUsers
            var playerUserIds = (await playerApiClient.GetUsersAsync(ct)).Select(u => u.Id);
            // get the teams for this MSEL and loop through them
            var teams = await blueprintContext.Teams
                .Where(t => t.MselId == msel.Id)
                .ToListAsync();
            foreach (var team in teams)
            {
                // create team in Player
                var playerTeamForm = new TeamForm() {
                    Name = team.Name
                };
                var playerTeam = await playerApiClient.CreateTeamAsync((Guid)msel.PlayerViewId, playerTeamForm, ct);
                team.PlayerTeamId = playerTeam.Id;
                // get all of the users for this team and loop through them
                var users = await blueprintContext.TeamUsers
                    .Where(tu => tu.TeamId == team.Id)
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
            // save the teams PlayerTeamId values
            await blueprintContext.SaveChangesAsync(ct);
        }

        // Create Player Applications for this MSEL
        public static async Task CreateApplicationsAsync(MselEntity msel, PlayerApiClient playerApiClient, BlueprintContext blueprintContext, CancellationToken ct)
        {
            foreach (var application in msel.PlayerApplications)
            {
                var urlString = application.Url
                    .Replace("{citeEvaluationId}", msel.CiteEvaluationId.ToString())
                    .Replace("{galleryExhibitId}", msel.GalleryExhibitId.ToString())
                    .Replace("{steamFitterScenarioId}", msel.SteamfitterScenarioId.ToString())
                    .Replace("{playerViewId}", msel.PlayerViewId.ToString());
                Uri applicationUrl;
                if (!Uri.TryCreate(urlString, UriKind.Absolute, out applicationUrl) || !(applicationUrl.Scheme == Uri.UriSchemeHttp || applicationUrl.Scheme == Uri.UriSchemeHttps))
                {
                    applicationUrl = null;
                }
                var playerApplication = new Application() {
                    Name = application.Name,
                    Embeddable = application.Embeddable,
                    ViewId = (Guid)msel.PlayerViewId,
                    Url = applicationUrl,
                    Icon = application.Icon,
                    LoadInBackground = application.LoadInBackground
                };
                playerApplication = await playerApiClient.CreateApplicationAsync((Guid)msel.PlayerViewId, playerApplication, ct);
                // create the Player Team Applications
                var applicationTeams = await blueprintContext.PlayerApplicationTeams
                    .Where(ct => ct.PlayerApplicationId == application.Id)
                    .Include(apt => apt.Team)
                    .ToListAsync(ct);
                foreach (var applicationTeam in applicationTeams)
                {
                    var applicationInstanceForm = new ApplicationInstanceForm() {
                        TeamId = (Guid)applicationTeam.Team.PlayerTeamId,
                        ApplicationId = (Guid)playerApplication.Id,
                        DisplayOrder = applicationTeam.DisplayOrder
                    };
                    await playerApiClient.CreateApplicationInstanceAsync(applicationInstanceForm.TeamId, applicationInstanceForm, ct);
                }
            }
        }

        // Add User to Player Team
        public static async Task AddUserToTeamAsync(Guid userId, Guid teamId, PlayerApiClient playerApiClient, BlueprintContext blueprintContext, CancellationToken ct)
        {
            // create Player TeamUsers
            await playerApiClient.AddUserToTeamAsync(teamId, userId, ct);
        }

    }
}
