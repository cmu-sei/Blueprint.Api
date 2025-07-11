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
using System.Diagnostics;
using System.Collections.Generic;

namespace Blueprint.Api.Infrastructure.Extensions
{
    public static class IntegrationSteamfitterExtensions
    {
        private const int _expirationSeconds = 120;
        private const int _delaySeconds = 0;
        private const int _iterations = 1;
        private const int _intervalSeconds = 0;

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
            int moveNumber,
            int groupNumber,
            string playerApiUrl,
            string citeApiUrl,
            string galleryApiUrl,
            Task triggerTask,
            CancellationToken ct)
        {
            var action = TaskAction.Http_post;
            var apiUrl = "http";
            switch (steamfitterTaskEntity.TaskType)
            {
                case SteamfitterIntegrationType.Notification:
                    action = TaskAction.Http_post;
                    apiUrl = "http";
                    steamfitterTaskEntity.ActionParameters["Url"] = playerApiUrl + "views/" + msel.PlayerViewId + "/notifications";
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
                    steamfitterTaskEntity.ActionParameters["Url"] = citeApiUrl + "evaluations/" + msel.CiteEvaluationId + "/situation";
                    steamfitterTaskEntity.ActionParameters["Body"] =
                        "{\"situationTime\": \"" +
                        steamfitterTaskEntity.ActionParameters["situationTime"] +
                        "\", \"situationDescription\": \"" +
                        steamfitterTaskEntity.ActionParameters["situationDescription"] +
                        "\"}";
                    break;
            }
            var moveString = moveNumber < 0 ? "-1" : "00" + moveNumber.ToString();
            moveString = moveString.Substring(moveString.Length - 2);
            var groupString = groupNumber < 0 ? "-1" : "00" + groupNumber.ToString();
            groupString = groupString.Substring(groupString.Length - 2);
            var taskForm = new TaskForm()
            {
                ScenarioId = msel.SteamfitterScenarioId,
                Name = moveString + "-" + groupString + " " + steamfitterTaskEntity.Name,
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
                TriggerCondition = triggerTask == null ? TaskTrigger.Manual : TaskTrigger.Completion,
                TriggerTaskId = triggerTask == null ? null : triggerTask.Id,
                UserExecutable = steamfitterTaskEntity.UserExecutable,
                Repeatable = steamfitterTaskEntity.Repeatable,
                Executable = true,
                VmList = [],
                VmMask = ""
            };
            triggerTask = await steamfitterApiClient.CreateTaskAsync(taskForm, ct);

            return triggerTask;
        }

        public static async STT.Task<Task> CreateNextMoveTasksAsync(
            MselEntity msel,
            SteamfitterApiClient steamfitterApiClient,
            int moveNumber,
            string citeApiUrl,
            string galleryApiUrl,
            Task triggerTask,
            CancellationToken ct)
        {
            var action = TaskAction.Http_put;
            var apiUrl = "http";
            var actionParameters = new Dictionary<string, string>();
            actionParameters["Body"] = "";
            var moveString = moveNumber < 0 ? "-1" : "00" + moveNumber.ToString();
            moveString = moveString.Substring(moveString.Length - 2);
            if (msel.UseCite)
            {
                actionParameters["Url"] = citeApiUrl + "evaluations/" + msel.CiteEvaluationId + "/move/" + moveNumber;
                var taskForm = new TaskForm()
                {
                    ScenarioId = msel.SteamfitterScenarioId,
                    Name = moveString + "-00 CITE Move Change",
                    Description = "Change the move on the CITE Evaluation to " + moveNumber,
                    Action = action,
                    ApiUrl = apiUrl,
                    ActionParameters = actionParameters,
                    ExpectedOutput = "\"currentMoveNumber\":\"" + moveNumber + "\"",
                    ExpirationSeconds = _expirationSeconds,
                    DelaySeconds = _delaySeconds,
                    IntervalSeconds = _intervalSeconds,
                    Iterations = _iterations,
                    IterationTermination = TaskIterationTermination.IterationCount,
                    TriggerCondition = triggerTask == null ? TaskTrigger.Manual : TaskTrigger.Completion,
                    TriggerTaskId = triggerTask == null ? null : triggerTask.Id,
                    UserExecutable = true,
                    Repeatable = true,
                    Executable = true,
                    VmList = [],
                    VmMask = ""
                };
                triggerTask = await steamfitterApiClient.CreateTaskAsync(taskForm, ct);
            }
            if (msel.UseGallery)
            {
                actionParameters["Url"] = galleryApiUrl + "exhibits/" + msel.GalleryExhibitId + "/move/" + moveNumber + "/inject/0";
                var taskForm = new TaskForm()
                {
                    ScenarioId = msel.SteamfitterScenarioId,
                    Name = moveString + "-00 Gallery Move Change",
                    Description = "Change the move on the Gallery Exhibit to " + moveNumber,
                    Action = action,
                    ApiUrl = apiUrl,
                    ActionParameters = actionParameters,
                    ExpectedOutput = "\"currentMove\":\"" + moveNumber + "\"",
                    ExpirationSeconds = _expirationSeconds,
                    DelaySeconds = _delaySeconds,
                    IntervalSeconds = _intervalSeconds,
                    Iterations = _iterations,
                    IterationTermination = TaskIterationTermination.IterationCount,
                    TriggerCondition = triggerTask == null ? TaskTrigger.Manual : TaskTrigger.Completion,
                    TriggerTaskId = triggerTask == null ? null : triggerTask.Id,
                    UserExecutable = true,
                    Repeatable = true,
                    Executable = true,
                    VmList = [],
                    VmMask = ""
                };
                triggerTask = await steamfitterApiClient.CreateTaskAsync(taskForm, ct);
            }
            return triggerTask;
        }

        public static async STT.Task<Task> CreateNextGroupTasksAsync(
            MselEntity msel,
            SteamfitterApiClient steamfitterApiClient,
            int moveNumber,
            int groupNumber,
            string citeApiUrl,
            string galleryApiUrl,
            Task triggerTask,
            CancellationToken ct)
        {
            var action = TaskAction.Http_put;
            var apiUrl = "http";
            var actionParameters = new Dictionary<string, string>();
            actionParameters["Body"] = "";
            var moveString = moveNumber < 0 ? "-1" : "00" + moveNumber.ToString();
            moveString = moveString.Substring(moveString.Length - 2);
            var groupString = groupNumber < 0 ? "-1" : "00" + groupNumber.ToString();
            groupString = groupString.Substring(groupString.Length - 2);
            if (msel.UseGallery)
            {
                actionParameters["Url"] = galleryApiUrl + "exhibits/" + msel.GalleryExhibitId + "/move/" + moveNumber + "/inject/" + groupNumber;
                var taskForm = new TaskForm()
                {
                    ScenarioId = msel.SteamfitterScenarioId,
                    Name = moveString + "-" + groupString + " Gallery Inject Change",
                    Description = "Change the move-inject on the Gallery Exhibit to " + moveNumber + "-" + groupNumber,
                    Action = action,
                    ApiUrl = apiUrl,
                    ActionParameters = actionParameters,
                    ExpectedOutput = "\"currentInject\":\"" + groupNumber + "\"",
                    ExpirationSeconds = _expirationSeconds,
                    DelaySeconds = _delaySeconds,
                    IntervalSeconds = _intervalSeconds,
                    Iterations = _iterations,
                    IterationTermination = TaskIterationTermination.IterationCount,
                    TriggerCondition = triggerTask == null ? TaskTrigger.Manual : TaskTrigger.Completion,
                    TriggerTaskId = triggerTask == null ? null : triggerTask.Id,
                    UserExecutable = true,
                    Repeatable = true,
                    Executable = true,
                    VmList = [],
                    VmMask = ""
                };
                triggerTask = await steamfitterApiClient.CreateTaskAsync(taskForm, ct);
            }
            return triggerTask;
        }

    }
}
