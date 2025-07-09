// Copyright 2025 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using System;
using System.Net.Http;
using System.Threading;
using STT = System.Threading.Tasks;
using IdentityModel.Client;
using Steamfitter.Api.Client;
using Blueprint.Api.Data;
using Blueprint.Api.Data.Models;
using Blueprint.Api.Data.Enumerations;
using Blueprint.Api.Infrastructure.Options;
using Blueprint.Api.ViewModels;

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
        public static async STT.Task<Scenario> CreateScenarioAsync(
            MselEntity msel,
            SteamfitterApiClient steamfitterApiClient,
            BlueprintContext blueprintContext,
            CancellationToken ct)
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
                ViewId = msel.PlayerViewId,
                View = msel.Name
            };
            var newScenario = await steamfitterApiClient.CreateScenarioAsync(scenarioForm, ct);
            // update the MSEL
            msel.SteamfitterScenarioId = newScenario.Id;
            await blueprintContext.SaveChangesAsync(ct);

            return newScenario;
        }

        // Create the Scenario Tasks for this MSEL
        public static async STT.Task<Task> CreateScenarioTasksAsync(
            MselEntity msel,
            SteamfitterTaskEntity steamfitterTaskEntity,
            SteamfitterApiClient steamfitterApiClient,
            Options.ClientOptions clientOptions,
            CancellationToken ct)
        {
            var action = TaskAction.Http_post;
            var apiUrl = "http";
            var playerApiUrl = clientOptions.PlayerApiUrl.EndsWith("/") ? clientOptions.PlayerApiUrl + "api/" : clientOptions.PlayerApiUrl + "/api/";
            var citeApiUrl = clientOptions.CiteApiUrl.EndsWith("/") ? clientOptions.CiteApiUrl + "api/" : clientOptions.CiteApiUrl + "/api/";
            var galleryApiUrl = clientOptions.PlayerApiUrl.EndsWith("/") ? clientOptions.GalleryApiUrl + "api/" : clientOptions.GalleryApiUrl + "/api/";
            switch (steamfitterTaskEntity.TaskType)
            {
                case SteamfitterIntegrationType.Notification:
                    action = TaskAction.Http_post;
                    apiUrl = "http";
                    steamfitterTaskEntity.ActionParameters["Url"] = playerApiUrl + "views/" +  msel.PlayerViewId + "/notifications";
                    steamfitterTaskEntity.ActionParameters["Body"] = "{\"text\": \"" + steamfitterTaskEntity.ActionParameters["notificationText"] + "\"}";
                    steamfitterTaskEntity.ExpectedOutput = "Message was sent";
                    break;
                case SteamfitterIntegrationType.http_delete:
                    action = TaskAction.Http_delete;
                    apiUrl = "http";
                    break;
                case SteamfitterIntegrationType.http_get:
                    action = TaskAction.Http_get;
                    apiUrl = "http";
                    break;
                case SteamfitterIntegrationType.http_put:
                    action = TaskAction.Http_put;
                    apiUrl = "http";
                    break;
                case SteamfitterIntegrationType.Email:
                    action = TaskAction.Send_email;
                    apiUrl = "stackstorm";
                    break;
                case SteamfitterIntegrationType.SituationUpdate:
                    action = TaskAction.Http_put;
                    apiUrl = "http";
                    steamfitterTaskEntity.ActionParameters["Url"] = citeApiUrl + "evaluations/" +  msel.CiteEvaluationId + "/situation";
                    steamfitterTaskEntity.ActionParameters["Body"] =
                        "{\"situationTime\": \"" +
                        steamfitterTaskEntity.ActionParameters["situationTime"] +
                        "\", \"situationDescription\": \"" +
                        steamfitterTaskEntity.ActionParameters["situationDescription"] +
                        "\"}";
                    break;
            }
            var taskForm = new TaskForm()
            {
                ScenarioId = msel.SteamfitterScenarioId,
                Name = steamfitterTaskEntity.Name,
                Description = steamfitterTaskEntity.Description,
                Action = action,
                ApiUrl = apiUrl,
                ActionParameters = steamfitterTaskEntity.ActionParameters,
                ExpectedOutput = steamfitterTaskEntity.ExpectedOutput,
                ExpirationSeconds = steamfitterTaskEntity.ExpirationSeconds,
                DelaySeconds = steamfitterTaskEntity.DelaySeconds,
                IntervalSeconds = steamfitterTaskEntity.IntervalSeconds,
                Iterations = steamfitterTaskEntity.Iterations,
                IterationTermination = TaskIterationTermination.IterationCount,
                TriggerCondition = TaskTrigger.Manual,
                UserExecutable = steamfitterTaskEntity.UserExecutable,
                Repeatable = steamfitterTaskEntity.Repeatable,
                Executable = true,
                VmList = [],
                VmMask = ""
            };
            var steamfitterTask = await steamfitterApiClient.CreateTaskAsync(taskForm, ct);

            return steamfitterTask;
        }

    }
}
