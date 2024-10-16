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
using Blueprint.Api.ViewModels;

namespace Blueprint.Api.Services
{
    public interface IJoinService : IHostedService
    {
    }

    public class JoinService : IJoinService
    {
        private readonly ILogger<JoinService> _logger;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly IJoinQueue _joinQueue;
        private readonly IHubContext<MainHub> _hubContext;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IOptionsMonitor<Infrastructure.Options.ClientOptions> _clientOptions;

        public JoinService(
            ILogger<JoinService> logger,
            IServiceScopeFactory scopeFactory,
            IJoinQueue joinQueue,
            IHubContext<MainHub> mainHub,
            IHttpClientFactory httpClientFactory,
            IOptionsMonitor<Infrastructure.Options.ClientOptions> clientOptions)
        {
            _logger = logger;
            _scopeFactory = scopeFactory;
            _joinQueue = joinQueue;
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
                        _logger.LogDebug("The JoinService is ready to process tasks.");
                        // _implementatioQueue is a BlockingCollection, so this loop will sleep if nothing is in the queue
                        var joinInformation = _joinQueue.Take(new CancellationToken());
                        // process on a new thread
                        // When adding a Task to the JoinQueue, the UserId MUST be changed to the current UserId, so that all results can be assigned to the correct user
                        var newThread = new Thread(ProcessTheJoin);
                        newThread.Start(joinInformation);
                    }
                    catch (System.Exception ex)
                    {
                        _logger.LogError(ex, "Exception encountered in JoinService Run loop.");
                    }
                }
            });
        }

        private async void ProcessTheJoin(Object joinInformationObject)
        {
            var ct = new CancellationToken();
            var joinInformation = (JoinInformation)joinInformationObject;
            var loggerInformation = $"Join for User: {joinInformation.UserId}, PlayerTeam: {joinInformation.PlayerTeamId}";
            var currentProcessStep = "Begin processing";
            _logger.LogDebug($"{currentProcessStep} {loggerInformation}");
            try
            {
                using (var scope = _scopeFactory.CreateScope())
                using (var blueprintContext = scope.ServiceProvider.GetRequiredService<BlueprintContext>())
                {
                    // get auth token
                    var tokenResponse = await ApiClientsExtensions.GetToken(scope);
                    // Join Player
                    if (joinInformation.PlayerTeamId != null)
                    {
                        // Get Player API client
                        currentProcessStep = "Player - get API client";
                        var playerApiClient = IntegrationPlayerExtensions.GetPlayerApiClient(_httpClientFactory, _clientOptions.CurrentValue.PlayerApiUrl, tokenResponse);

                        // add user to team
                        currentProcessStep = "Player - add user to team";
                        await IntegrationPlayerExtensions.AddUserToTeamAsync(joinInformation.UserId, (Guid)joinInformation.PlayerTeamId, playerApiClient, blueprintContext, ct);
                    }
                    // Join Gallery
                    if (joinInformation.GalleryTeamId != null)
                    {
                        // Get Gallery API client
                        currentProcessStep = "Gallery - get API client";
                        var galleryApiClient = IntegrationGalleryExtensions.GetGalleryApiClient(_httpClientFactory, _clientOptions.CurrentValue.GalleryApiUrl, tokenResponse);

                        // add user to team
                        currentProcessStep = "Gallery - add user to team";
                        await IntegrationGalleryExtensions.AddUserToTeamAsync(joinInformation.UserId, (Guid)joinInformation.GalleryTeamId, galleryApiClient, blueprintContext, ct);
                    }
                    // Join Cite
                    if (joinInformation.CiteTeamId != null)
                    {
                        // Get Cite API client
                        currentProcessStep = "Cite - get API client";
                        var citeApiClient = IntegrationCiteExtensions.GetCiteApiClient(_httpClientFactory, _clientOptions.CurrentValue.CiteApiUrl, tokenResponse);

                        // add user to team
                        currentProcessStep = "Cite - add user to team";
                        await IntegrationCiteExtensions.AddUserToTeamAsync(joinInformation.UserId, (Guid)joinInformation.CiteTeamId, citeApiClient, blueprintContext, ct);
                    }
                }
            }
            catch (System.Exception ex)
            {
                _logger.LogError($"{currentProcessStep} {loggerInformation}", ex);
            }
        }

    }

}
