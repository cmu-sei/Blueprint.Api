// Copyright 2025 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using STT = System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using IdentityModel.Client;
using Steamfitter.Api.Client;
using Blueprint.Api.Data;
using Blueprint.Api.Data.Models;

namespace Blueprint.Api.Infrastructure.Extensions
{
    public static class IntegrationSteamfitterExtensions
    {
        public static SteamfitterApiClient GetSteamfitterApiClient(IHttpClientFactory httpClientFactory, string apiUrl, TokenResponse tokenResponse)
        {
            var client = ApiClientsExtensions.GetHttpClient(httpClientFactory, apiUrl, tokenResponse);
            var apiClient = new SteamfitterApiClient(client);
            return apiClient;
        }

        public static async STT.Task PullFromSteamfitterAsync(Guid SteamfitterScenarioId, SteamfitterApiClient SteamfitterApiClient, CancellationToken ct)
        {
            try
            {
                // delete
                await SteamfitterApiClient.DeleteScenarioAsync(SteamfitterScenarioId, ct);
            }
            catch (System.Exception)
            {
            }
        }

        // Create a Steamfitter Scenario for this MSEL
        public static async STT.Task<Scenario> CreateScenarioAsync(MselEntity msel, SteamfitterApiClient steamfitterApiClient, BlueprintContext blueprintContext, CancellationToken ct)
        {
            var startDate = DateTime.UtcNow;
            startDate = msel.StartTime < startDate ? startDate : msel.StartTime;
            ScenarioForm scenarioForm = new ScenarioForm()
            {
                Name = msel.Name,
                Description = msel.Description,
                Status = ScenarioStatus.Active,
                StartDate = startDate,
                EndDate = startDate.AddSeconds(msel.DurationSeconds),
                ViewId = msel.PlayerViewId
            };
            var newScenario = await steamfitterApiClient.CreateScenarioAsync(scenarioForm, ct);
            // update the MSEL
            msel.SteamfitterScenarioId = newScenario.Id;
            await blueprintContext.SaveChangesAsync(ct);

            // add the scenario tasks
            await CreateScenarioTasksAsync(msel, steamfitterApiClient, blueprintContext, ct);

            return newScenario;
        }

        // Create the Scenario Tasks for this MSEL
        private static async STT.Task CreateScenarioTasksAsync(MselEntity msel, SteamfitterApiClient SteamfitterApiClient, BlueprintContext blueprintContext, CancellationToken ct)
        {
            foreach (var scenarioEvent in msel.ScenarioEvents)
            {
                // var IntegrationTarget = GetArticleValue(GalleryArticleParameter.IntegrationTarget.ToString(), scenarioEvent.DataValues, msel.DataFields);
                // if (IntegrationTarget.Contains("Gallery"))
            }
        }

    }
}
