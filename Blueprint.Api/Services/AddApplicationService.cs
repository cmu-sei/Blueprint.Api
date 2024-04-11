// Copyright 2024 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Blueprint.Api.Hubs;
using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Blueprint.Api.Infrastructure.Extensions;
using Blueprint.Api.Data;
using Player.Api.Client;

namespace Blueprint.Api.Services
{
    public interface IAddApplicationService : IHostedService
    {
    }

    public class AddApplicationService : IAddApplicationService
    {
        private readonly ILogger<AddApplicationService> _logger;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly IAddApplicationQueue _addApplicationQueue;
        private readonly IHubContext<MainHub> _hubContext;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IOptionsMonitor<Infrastructure.Options.ClientOptions> _clientOptions;

        public AddApplicationService(
            ILogger<AddApplicationService> logger,
            IServiceScopeFactory scopeFactory,
            IAddApplicationQueue addApplicationQueue,
            IHubContext<MainHub> mainHub,
            IHttpClientFactory httpClientFactory,
            IOptionsMonitor<Infrastructure.Options.ClientOptions> clientOptions)
        {
            _logger = logger;
            _scopeFactory = scopeFactory;
            _addApplicationQueue = addApplicationQueue;
            _hubContext = mainHub;
            _httpClientFactory = httpClientFactory;
            _clientOptions = clientOptions;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            _ = Run();

            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        private async Task Run()
        {
            await Task.Run(() =>
            {
                while (true)
                {
                    try
                    {
                        _logger.LogDebug("The AddApplicationService is ready to process tasks.");
                        // _implementatioQueue is a BlockingCollection, so this loop will sleep if nothing is in the queue
                        var addApplicationInformation = _addApplicationQueue.Take(new CancellationToken());
                        // process on a new thread
                        // When adding a Task to the AddApplicationQueue, the UserId MUST be changed to the current UserId, so that all results can be assigned to the correct user
                        var newThread = new Thread(ProcessTheAddApplication);
                        newThread.Start(addApplicationInformation);
                    }
                    catch (System.Exception ex)
                    {
                        _logger.LogError("Exception encountered in AddApplicationService Run loop.", ex);
                    }
                }
            });
        }

        private async void ProcessTheAddApplication(Object addApplicationInformationObject)
        {
            var ct = new CancellationToken();
            var addApplicationInformation = (AddApplicationInformation)addApplicationInformationObject;
            var loggerInformation = $"Adding Application";
            var currentProcessStep = "Begin processing";
            _logger.LogDebug($"{currentProcessStep} {loggerInformation}");
            try
            {
                using (var scope = _scopeFactory.CreateScope())
                using (var blueprintContext = scope.ServiceProvider.GetRequiredService<BlueprintContext>())
                {
                    // get auth token
                    var tokenResponse = await ApiClientsExtensions.GetToken(scope);
                    // Get Player API client
                    currentProcessStep = "Player - get API client";
                    var playerApiClient = IntegrationPlayerExtensions.GetPlayerApiClient(_httpClientFactory, _clientOptions.CurrentValue.PlayerApiUrl, tokenResponse);
                    var playerApplication = await playerApiClient.CreateApplicationAsync(addApplicationInformation.Application.ViewId, addApplicationInformation.Application, ct);
                    // create the Player Team Application
                    var applicationInstanceForm = new ApplicationInstanceForm() {
                        TeamId = addApplicationInformation.PlayerTeamId,
                        ApplicationId = playerApplication.Id,
                        DisplayOrder = addApplicationInformation.DisplayOrder
                    };
                    var applicationInstance = await playerApiClient.CreateApplicationInstanceAsync(applicationInstanceForm.TeamId, applicationInstanceForm, ct);
                }
            }
            catch (System.Exception ex)
            {
                _logger.LogError($"{currentProcessStep} {loggerInformation}", ex);
            }
        }

    }

}
