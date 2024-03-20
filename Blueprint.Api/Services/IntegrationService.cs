// Copyright 2024 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using AutoMapper;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Blueprint.Api.Data.Models;
using Blueprint.Api.Hubs;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Blueprint.Api.Infrastructure.Extensions;
using Blueprint.Api.Data;
using Cite.Api.Client;
using Gallery.Api.Client;
using Player.Api.Client;


namespace Blueprint.Api.Services
{
    public interface IIntegrationService : IHostedService
    {
    }

    public class IntegrationService : IIntegrationService
    {
        private readonly ILogger<IntegrationService> _logger;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly IIntegrationQueue _integrationQueue;
        private readonly IHubContext<MainHub> _hubContext;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IOptionsMonitor<Infrastructure.Options.ClientOptions> _clientOptions;

        public IntegrationService(
            ILogger<IntegrationService> logger,
            IServiceScopeFactory scopeFactory,
            IIntegrationQueue integrationQueue,
            IHubContext<MainHub> mainHub,
            IHttpClientFactory httpClientFactory,
            IOptionsMonitor<Infrastructure.Options.ClientOptions> clientOptions)
        {
            _logger = logger;
            _scopeFactory = scopeFactory;
            _integrationQueue = integrationQueue;
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
                        _logger.LogDebug("The IntegrationService is ready to process tasks.");
                        // _implementatioQueue is a BlockingCollection, so this loop will sleep if nothing is in the queue
                        var integrationInformation = _integrationQueue.Take(new CancellationToken());
                        // process on a new thread
                        // When adding a Task to the IntegrationQueue, the UserId MUST be changed to the current UserId, so that all results can be assigned to the correct user
                        var newThread = new Thread(ProcessTheMsel);
                        newThread.Start(integrationInformation);
                    }
                    catch (System.Exception ex)
                    {
                        _logger.LogError("Exception encountered in IntegrationService Run loop.", ex);
                    }
                }
            });
        }

        private async void ProcessTheMsel(Object integrationInformationObject)
        {
            var ct = new CancellationToken();
            var integrationInformation = (IntegrationInformation)integrationInformationObject;
            var currentProcessStep = "Begin processing";
            _logger.LogDebug($"{currentProcessStep} {integrationInformation.MselId}");
            try
            {
                using (var scope = _scopeFactory.CreateScope())
                using (var blueprintContext = scope.ServiceProvider.GetRequiredService<BlueprintContext>())
                {
                    currentProcessStep = "Getting the MSEL entity";
                    // get the MSEL and verify data state
                    var msel = await blueprintContext.Msels
                        .Include(m => m.PlayerApplications)
                        .ThenInclude(pa => pa.PlayerApplicationTeams)
                        .AsSplitQuery()
                        .SingleOrDefaultAsync(m => m.Id == integrationInformation.MselId);
                    var isAPush = !(
                        msel.PlayerViewId != null ||
                        msel.GalleryExhibitId != null ||
                        msel.CiteEvaluationId != null ||
                        msel.SteamfitterScenarioId != null);
                    try
                    {
                        var tokenResponse = await ApiClientsExtensions.GetToken(scope);
                        // Get Player API client
                        currentProcessStep = "Player - get API client";
                        var playerApiClient = IntegrationPlayerExtensions.GetPlayerApiClient(_httpClientFactory, _clientOptions.CurrentValue.PlayerApiUrl, tokenResponse);
                        if (isAPush)
                        {
                            await _hubContext.Clients.Group(msel.Id.ToString()).SendAsync(MainHubMethods.MselPushStatusChange, msel.Id + "Pushing Integrations", null, ct);
                            // Player processing part 1
                            currentProcessStep = "Player - begin processing part 1";
                            await PlayerProcessPart1(msel, integrationInformation.PlayerViewId, playerApiClient, blueprintContext, ct);

                            // Gallery processing
                            if (msel.UseGallery)
                            {
                                // Get Gallery API client
                                currentProcessStep = "Gallery - get API client";
                                var galleryApiClient = IntegrationGalleryExtensions.GetGalleryApiClient(_httpClientFactory, _clientOptions.CurrentValue.GalleryApiUrl, tokenResponse);

                                currentProcessStep = "Gallery - begin processing";
                                msel = await blueprintContext.Msels
                                    .Include(m => m.Cards)
                                    .Include(m => m.DataFields)
                                    .Include(m => m.ScenarioEvents)
                                    .ThenInclude(se => se.DataValues)
                                    .AsSplitQuery()
                                    .SingleOrDefaultAsync(m => m.Id == integrationInformation.MselId);
                                var scenarioEventService = scope.ServiceProvider.GetRequiredService<IScenarioEventService>();
                                await GalleryProcess(msel, scenarioEventService, galleryApiClient, blueprintContext, ct);

                            }
                            // CITE processing
                            if (msel.UseCite)
                            {
                                // Get CITE API client
                                currentProcessStep = "CITE - get API client";
                                var citeApiClient = IntegrationCiteExtensions.GetCiteApiClient(_httpClientFactory, _clientOptions.CurrentValue.CiteApiUrl, tokenResponse);

                                currentProcessStep = "CITE - begin processing";
                                msel = await blueprintContext.Msels
                                    .Include(m => m.CiteActions)
                                    .Include(m => m.CiteRoles)
                                    .Include(m => m.Moves)
                                    .AsSplitQuery()
                                    .SingleOrDefaultAsync(m => m.Id == integrationInformation.MselId);
                                await CiteProcess(msel, citeApiClient, blueprintContext, ct);
                            }

                            // Player processing part 2
                            currentProcessStep = "Player - push applications";
                            await IntegrationPlayerExtensions.CreateApplicationsAsync(msel, playerApiClient, blueprintContext, ct);
                            await _hubContext.Clients.Group(msel.Id.ToString()).SendAsync(MainHubMethods.MselPushStatusChange, msel.Id + "", null, ct);
                        }
                        else
                        {
                            await _hubContext.Clients.Group(msel.Id.ToString()).SendAsync(MainHubMethods.MselPushStatusChange, msel.Id + "Pulling Integrations", null, ct);
                            // Pull from CITE
                            if (msel.UseCite)
                            {
                                // Get CITE API client
                                currentProcessStep = "CITE - get API client";
                                var citeApiClient = IntegrationCiteExtensions.GetCiteApiClient(_httpClientFactory, _clientOptions.CurrentValue.CiteApiUrl, tokenResponse);

                                currentProcessStep = "CITE - pull evaluation";
                                try
                                {
                                    await IntegrationCiteExtensions.PullFromCiteAsync(msel, citeApiClient, blueprintContext, ct);
                                }
                                catch (System.Exception ex)
                                {
                                _logger.LogError($"{currentProcessStep} {msel.Name} ({msel.Id})", ex);
                                }
                            }
                            // Pull from Gallery
                            if (msel.UseGallery)
                            {
                                // Get Gallery API client
                                currentProcessStep = "Gallery - get API client";
                                var galleryApiClient = IntegrationGalleryExtensions.GetGalleryApiClient(_httpClientFactory, _clientOptions.CurrentValue.GalleryApiUrl, tokenResponse);

                                currentProcessStep = "Gallery - pull collection";
                                try
                                {
                                    await IntegrationGalleryExtensions.PullFromGalleryAsync(msel, galleryApiClient, blueprintContext, ct);
                                }
                                catch (System.Exception ex)
                                {
                                _logger.LogError($"{currentProcessStep} {msel.Name} ({msel.Id})", ex);
                                }
                            }
                            // Pull from Player
                            currentProcessStep = "Player - pull view";
                            try
                            {
                                await IntegrationPlayerExtensions.PullFromPlayerAsync(msel, playerApiClient, blueprintContext, ct);
                            }
                            catch (System.Exception ex)
                            {
                               _logger.LogError($"{currentProcessStep} {msel.Name} ({msel.Id})", ex);
                            }
                            await _hubContext.Clients.Group(msel.Id.ToString()).SendAsync(MainHubMethods.MselPushStatusChange, msel.Id + "", null, ct);
                        }

                    }
                    catch (System.Exception ex)
                    {
                        _logger.LogError($"{currentProcessStep} {msel.Name} ({msel.Id})", ex);
                    }

                }
            }
            catch (System.Exception ex)
            {
                _logger.LogError($"{currentProcessStep} {integrationInformation}", ex);
            }
        }

        private bool CanMselBePushed(MselEntity mselToIntegrate)
        {
            // TODO: build this out!!!
            return true;
        }

        private async Task PlayerProcessPart1(MselEntity msel, Guid? playerViewId, PlayerApiClient playerApiClient, BlueprintContext blueprintContext, CancellationToken ct)
        {
            var currentProcessStep = "Player create view";
            try
            {
                // create the Player View
                await _hubContext.Clients.Group(msel.Id.ToString()).SendAsync(MainHubMethods.MselPushStatusChange, msel.Id + ",Pushing View to Player", null, ct);
                await IntegrationPlayerExtensions.CreateViewAsync(msel, playerViewId, playerApiClient, blueprintContext, ct);
                // create the Player Teams
                currentProcessStep = "Player create teams";
                await _hubContext.Clients.Group(msel.Id.ToString()).SendAsync(MainHubMethods.MselPushStatusChange, msel.Id + ",Pushing Teams to Player", null, ct);
                await IntegrationPlayerExtensions.CreateTeamsAsync(msel, playerApiClient, blueprintContext, ct);
            }
            catch (System.Exception ex)
            {
                _logger.LogError($"{currentProcessStep} {msel.Name} ({msel.Id})", ex);
                throw ex;
            }
        }

        private async Task CiteProcess(MselEntity msel, CiteApiClient citeApiClient, BlueprintContext blueprintContext, CancellationToken ct)
        {
            // start a transaction, because we will modify many database items
            await blueprintContext.Database.BeginTransactionAsync();
            // create the Cite Evaluation
            await _hubContext.Clients.Group(msel.Id.ToString()).SendAsync(MainHubMethods.MselPushStatusChange, msel.Id + ",Pushing Evaluation to CITE", null, ct);
            await IntegrationCiteExtensions.CreateEvaluationAsync(msel, citeApiClient, blueprintContext, ct);
            // create the Cite Moves
            await _hubContext.Clients.Group(msel.Id.ToString()).SendAsync(MainHubMethods.MselPushStatusChange, msel.Id + ",Pushing Moves to CITE", null, ct);
            await IntegrationCiteExtensions.CreateMovesAsync(msel, citeApiClient, blueprintContext, ct);
            // create the Cite Teams
            await _hubContext.Clients.Group(msel.Id.ToString()).SendAsync(MainHubMethods.MselPushStatusChange, msel.Id + ",Pushing Teams to CITE", null, ct);
            await IntegrationCiteExtensions.CreateTeamsAsync(msel, citeApiClient, blueprintContext, ct);
            // create the Cite Roles
            await _hubContext.Clients.Group(msel.Id.ToString()).SendAsync(MainHubMethods.MselPushStatusChange, msel.Id + ",Pushing Roles to CITE", null, ct);
            await IntegrationCiteExtensions.CreateRolesAsync(msel, citeApiClient, blueprintContext, ct);
            // create the Cite Actions
            await _hubContext.Clients.Group(msel.Id.ToString()).SendAsync(MainHubMethods.MselPushStatusChange, msel.Id + ",Pushing Actions to CITE", null, ct);
            await IntegrationCiteExtensions.CreateActionsAsync(msel, citeApiClient, blueprintContext, ct);
            // commit the transaction
            await _hubContext.Clients.Group(msel.Id.ToString()).SendAsync(MainHubMethods.MselPushStatusChange, msel.Id + ",Commit to CITE", null, ct);
            await blueprintContext.Database.CommitTransactionAsync(ct);
        }

        private async Task GalleryProcess(MselEntity msel, IScenarioEventService scenarioEventService, GalleryApiClient galleryApiClient, BlueprintContext blueprintContext, CancellationToken ct)
        {
            // start a transaction, because we will modify many database items
            await blueprintContext.Database.BeginTransactionAsync();
            // create the Gallery Collection
            await _hubContext.Clients.Group(msel.Id.ToString()).SendAsync(MainHubMethods.MselPushStatusChange, msel.Id + ",Pushing Collection to Gallery", null, ct);
            await IntegrationGalleryExtensions.CreateCollectionAsync(msel, galleryApiClient, blueprintContext, ct);
            // create the Gallery Exhibit
            await _hubContext.Clients.Group(msel.Id.ToString()).SendAsync(MainHubMethods.MselPushStatusChange, msel.Id + ",Pushing Exhibit to Gallery", null, ct);
            await IntegrationGalleryExtensions.CreateExhibitAsync(msel, galleryApiClient, blueprintContext, ct);
            // create the Gallery Teams
            await _hubContext.Clients.Group(msel.Id.ToString()).SendAsync(MainHubMethods.MselPushStatusChange, msel.Id + ",Pushing Teams to Gallery", null, ct);
            await IntegrationGalleryExtensions.CreateTeamsAsync(msel, galleryApiClient, blueprintContext, ct);
            // create the Gallery Cards
            await _hubContext.Clients.Group(msel.Id.ToString()).SendAsync(MainHubMethods.MselPushStatusChange, msel.Id + ",Pushing Cards to Gallery", null, ct);
            await IntegrationGalleryExtensions.CreateCardsAsync(msel, galleryApiClient, blueprintContext, ct);
            // create the Gallery Articles
            await _hubContext.Clients.Group(msel.Id.ToString()).SendAsync(MainHubMethods.MselPushStatusChange, msel.Id + ",Pushing Articles to Gallery", null, ct);
            await IntegrationGalleryExtensions.CreateArticlesAsync(msel, galleryApiClient, blueprintContext, scenarioEventService, ct);
            // commit the transaction
            await _hubContext.Clients.Group(msel.Id.ToString()).SendAsync(MainHubMethods.MselPushStatusChange, msel.Id + ",Commit to Gallery", null, ct);
            await blueprintContext.Database.CommitTransactionAsync(ct);


        }

    }

    public class IntegrationInformation
    {
        public Guid MselId { get; set; }
        public Guid? PlayerViewId { get; set; }
    }

}
