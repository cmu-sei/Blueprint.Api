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
using System.Net.Http;
using System.Threading;
using STT = System.Threading.Tasks;
using Blueprint.Api.Infrastructure.Extensions;
using Blueprint.Api.Data;
using Cite.Api.Client;
using Gallery.Api.Client;
using Player.Api.Client;
using Steamfitter.Api.Client;


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

        public STT.Task StartAsync(CancellationToken cancellationToken)
        {
            _ = Run();

            return STT.Task.CompletedTask;
        }

        public STT.Task StopAsync(CancellationToken cancellationToken)
        {
            return STT.Task.CompletedTask;
        }

        private async STT.Task Run()
        {
            await STT.Task.Run(() =>
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
                        _logger.LogError(ex, "Exception encountered in IntegrationService Run loop.");
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
                    var hubGroup = _hubContext.Clients.Group(msel.Id.ToString());
                    currentProcessStep = "Try processing the MSEL";
                    try
                    {
                        PlayerApiClient playerApiClient = null;
                        currentProcessStep = "Getting Auth Token with scope " + scope;
                        var tokenResponse = await ApiClientsExtensions.GetToken(scope);
                        if (isAPush)
                        {
                            // Player processing part 1
                            if (msel.UsePlayer)
                            {
                                // Get Player API client
                                currentProcessStep = "Player - get API client with token: " + tokenResponse.AccessToken;
                                playerApiClient = IntegrationPlayerExtensions.GetPlayerApiClient(_httpClientFactory, _clientOptions.CurrentValue.PlayerApiUrl, tokenResponse);
                                await hubGroup.SendAsync(MainHubMethods.MselPushStatusChange, msel.Id + ",Pushing Integrations", null, ct);
                                // Player processing part 1
                                currentProcessStep = "Player - begin processing part 1";
                                await PlayerProcessPart1(msel, integrationInformation.PlayerViewId, playerApiClient, blueprintContext, ct);
                            }

                            // Gallery processing
                            if (msel.UseGallery)
                            {
                                // Get Gallery API client
                                currentProcessStep = "Gallery - get API client";
                                var galleryApiClient = IntegrationGalleryExtensions.GetGalleryApiClient(_httpClientFactory, _clientOptions.CurrentValue.GalleryApiUrl, tokenResponse);

                                currentProcessStep = "Gallery - get MSEL " + integrationInformation.MselId;
                                msel = await blueprintContext.Msels
                                    .Include(m => m.Cards)
                                    .Include(m => m.DataFields)
                                    .Include(m => m.ScenarioEvents)
                                    .ThenInclude(se => se.DataValues)
                                    .AsSplitQuery()
                                    .SingleOrDefaultAsync(m => m.Id == integrationInformation.MselId);
                                currentProcessStep = "Gallery - get scenario event service";
                                var scenarioEventService = scope.ServiceProvider.GetRequiredService<IScenarioEventService>();
                                currentProcessStep = "Gallery - start GalleryProcess";
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

                            // Steamfitter processing
                            if (msel.UseSteamfitter)
                            {
                                // Get Steamfitter API client
                                currentProcessStep = "Steamfitter - get API client";
                                var steamfitterApiClient = IntegrationSteamfitterExtensions.GetSteamfitterApiClient(_httpClientFactory, _clientOptions.CurrentValue.SteamfitterApiUrl, tokenResponse);

                                currentProcessStep = "Steamfitter - begin processing";
                                msel = await blueprintContext.Msels
                                    .Include(m => m.Moves)
                                    .Include(m => m.ScenarioEvents)
                                    .ThenInclude(m => m.SteamfitterTask)
                                    .AsSplitQuery()
                                    .SingleOrDefaultAsync(m => m.Id == integrationInformation.MselId);
                                await SteamfitterProcess(msel, steamfitterApiClient, blueprintContext, ct);
                            }

                            // Player processing part 2
                            if (msel.UsePlayer)
                            {
                                await hubGroup.SendAsync(MainHubMethods.MselPushStatusChange, msel.Id + ",Push Player Applications", null, ct);
                                currentProcessStep = "Player - push applications";
                                await IntegrationPlayerExtensions.CreateApplicationsAsync(msel, playerApiClient, blueprintContext, ct);
                            }
                            // set the MSEL status
                            msel.Status = Data.Enumerations.MselItemStatus.Deployed;
                            await blueprintContext.SaveChangesAsync(ct);
                            // tell UI we are done
                            await hubGroup.SendAsync(MainHubMethods.MselPushStatusChange, msel.Id + "", null, ct);
                        }
                        else
                        {
                            await hubGroup.SendAsync(MainHubMethods.MselPushStatusChange, msel.Id + ",Pulling Integrations", null, ct);
                            // Pull from Steamfitter
                            if (msel.SteamfitterScenarioId != null)
                            {
                                try
                                {
                                    currentProcessStep = "Steamfitter - pull scenario";
                                    await hubGroup.SendAsync(MainHubMethods.MselPushStatusChange, msel.Id + ",Pulling Steamfitter Scenario", null, ct);
                                    var steamfitterApiClient = IntegrationSteamfitterExtensions.GetSteamfitterApiClient(_httpClientFactory, _clientOptions.CurrentValue.SteamfitterApiUrl, tokenResponse);
                                    await IntegrationSteamfitterExtensions.PullFromSteamfitterAsync((Guid)msel.SteamfitterScenarioId, steamfitterApiClient, ct);
                                }
                                catch (System.Exception)
                                {
                                    _logger.LogError($"{currentProcessStep} {msel.Name} ({msel.Id})");
                                }
                            }
                            // Pull from CITE
                            if (msel.CiteEvaluationId != null)
                            {
                                try
                                {
                                    // Get CITE API client
                                    currentProcessStep = "CITE - get API client";
                                    var citeApiClient = IntegrationCiteExtensions.GetCiteApiClient(_httpClientFactory, _clientOptions.CurrentValue.CiteApiUrl, tokenResponse);

                                    currentProcessStep = "CITE - pull evaluation";
                                    await hubGroup.SendAsync(MainHubMethods.MselPushStatusChange, msel.Id + ",Pulling CITE Evaluation", null, ct);
                                    await IntegrationCiteExtensions.PullFromCiteAsync((Guid)msel.CiteEvaluationId, citeApiClient, ct);
                                }
                                catch (System.Exception ex)
                                {
                                    _logger.LogError($"{currentProcessStep} {msel.Name} ({msel.Id})", ex);
                                }
                            }
                            // Pull from Gallery
                            if (msel.GalleryCollectionId != null)
                            {
                                try
                                {
                                    // Get Gallery API client
                                    currentProcessStep = "Gallery - get API client";
                                    var galleryApiClient = IntegrationGalleryExtensions.GetGalleryApiClient(_httpClientFactory, _clientOptions.CurrentValue.GalleryApiUrl, tokenResponse);
                                    currentProcessStep = "Gallery - pull collection";
                                    await hubGroup.SendAsync(MainHubMethods.MselPushStatusChange, msel.Id + ",Pulling Gallery Collection", null, ct);
                                    await IntegrationGalleryExtensions.PullFromGalleryAsync((Guid)msel.GalleryCollectionId, galleryApiClient, ct);
                                }
                                catch (System.Exception ex)
                                {
                                    _logger.LogError($"{currentProcessStep} {msel.Name} ({msel.Id})", ex);
                                }
                            }
                            // Pull from Player
                            if (msel.PlayerViewId != null)
                            {
                                try
                                {
                                    currentProcessStep = "Player - get API client with token: " + tokenResponse.AccessToken;
                                    playerApiClient = IntegrationPlayerExtensions.GetPlayerApiClient(_httpClientFactory, _clientOptions.CurrentValue.PlayerApiUrl, tokenResponse);
                                    currentProcessStep = "Player - pull view";
                                    await hubGroup.SendAsync(MainHubMethods.MselPushStatusChange, msel.Id + ",Pulling Player View", null, ct);
                                    // TODO:  Player requires two deletes?
                                    await IntegrationPlayerExtensions.PullFromPlayerAsync((Guid)msel.PlayerViewId, playerApiClient, ct);
                                    await IntegrationPlayerExtensions.PullFromPlayerAsync((Guid)msel.PlayerViewId, playerApiClient, ct);
                                }
                                catch (System.Exception)
                                {
                                    _logger.LogError($"{currentProcessStep} {msel.Name} ({msel.Id})");
                                }
                            }
                            // update the MSEL
                            currentProcessStep = "MSEL update ";
                            var mselId = msel.Id;
                            msel = await blueprintContext.Msels.FirstOrDefaultAsync(m => m.Id == mselId);
                            if (msel != null)
                            {
                                msel.CiteEvaluationId = null;
                                msel.GalleryExhibitId = null;
                                msel.GalleryCollectionId = null;
                                msel.PlayerViewId = null;
                                msel.SteamfitterScenarioId = null;
                                msel.Status = integrationInformation.FinalStatus;
                                await blueprintContext.SaveChangesAsync(ct);
                            }
                            // send completion status
                            await hubGroup.SendAsync(MainHubMethods.MselPushStatusChange, mselId + "", null, ct);
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

        private async STT.Task PlayerProcessPart1(MselEntity msel, Guid? playerViewId, PlayerApiClient playerApiClient, BlueprintContext blueprintContext, CancellationToken ct)
        {
            var currentProcessStep = "Player create view";
            var hubGroup = _hubContext.Clients.Group(msel.Id.ToString());
            try
            {
                // create the Player View
                await hubGroup.SendAsync(MainHubMethods.MselPushStatusChange, msel.Id + ",Pushing View to Player", null, ct);
                await IntegrationPlayerExtensions.CreateViewAsync(msel, playerViewId, playerApiClient, blueprintContext, ct);
                // create the Player Teams
                currentProcessStep = "Player create teams";
                await hubGroup.SendAsync(MainHubMethods.MselPushStatusChange, msel.Id + ",Pushing Teams to Player", null, ct);
                await IntegrationPlayerExtensions.CreateTeamsAsync(msel, playerApiClient, blueprintContext, ct);
            }
            catch (System.Exception ex)
            {
                _logger.LogError($"{currentProcessStep} {msel.Name} ({msel.Id})", ex);
                throw ex;
            }
        }

        private async STT.Task CiteProcess(MselEntity msel, CiteApiClient citeApiClient, BlueprintContext blueprintContext, CancellationToken ct)
        {
            var currentProcessStep = "CITE - get hubGroup";
            try
            {
                var hubGroup = _hubContext.Clients.Group(msel.Id.ToString());
                // create the Cite Evaluation
                currentProcessStep = "CITE - create evaluation";
                await hubGroup.SendAsync(MainHubMethods.MselPushStatusChange, msel.Id + ",Pushing Evaluation to CITE", null, ct);
                var evaluation = await IntegrationCiteExtensions.CreateEvaluationAsync(msel, citeApiClient, blueprintContext, ct);
                // create the Cite Moves
                currentProcessStep = "CITE - create moves";
                await hubGroup.SendAsync(MainHubMethods.MselPushStatusChange, msel.Id + ",Pushing Moves to CITE", null, ct);
                await IntegrationCiteExtensions.CreateMovesAsync(msel, citeApiClient, blueprintContext, ct);
                // create the Cite Teams
                currentProcessStep = "CITE - create teams";
                await hubGroup.SendAsync(MainHubMethods.MselPushStatusChange, msel.Id + ",Pushing Teams to CITE", null, ct);
                await IntegrationCiteExtensions.CreateTeamsAsync(msel, citeApiClient, blueprintContext, ct);
                // create the Cite Roles
                currentProcessStep = "CITE - create roles";
                await hubGroup.SendAsync(MainHubMethods.MselPushStatusChange, msel.Id + ",Pushing Roles to CITE", null, ct);
                await IntegrationCiteExtensions.CreateRolesAsync(msel, citeApiClient, blueprintContext, ct);
                // create the Cite Actions
                currentProcessStep = "CITE - create actions";
                await hubGroup.SendAsync(MainHubMethods.MselPushStatusChange, msel.Id + ",Pushing Actions to CITE", null, ct);
                await IntegrationCiteExtensions.CreateActionsAsync(msel, citeApiClient, blueprintContext, ct);
                // update the evaluation, so that submissions get created
                currentProcessStep = "CITE - advance";
                await hubGroup.SendAsync(MainHubMethods.MselPushStatusChange, msel.Id + ",Finishing Evaluation to CITE", null, ct);
                await IntegrationCiteExtensions.CycleMoveAsync(evaluation.Id, citeApiClient, blueprintContext, ct);
            }
            catch (System.Exception ex)
            {
                _logger.LogError($"{currentProcessStep} {msel.Name} ({msel.Id})", ex);
                throw ex;
            }
        }

        private async STT.Task GalleryProcess(MselEntity msel, IScenarioEventService scenarioEventService, GalleryApiClient galleryApiClient, BlueprintContext blueprintContext, CancellationToken ct)
        {
            var currentProcessStep = "Gallery - get hubGroup";
            try
            {
                var hubGroup = _hubContext.Clients.Group(msel.Id.ToString());
                // start a transaction, because we will modify many database items
                currentProcessStep = "Gallery - begin transaction";
                await blueprintContext.Database.BeginTransactionAsync();
                // create the Gallery Collection
                currentProcessStep = "Gallery - Pushing Collection";
                await hubGroup.SendAsync(MainHubMethods.MselPushStatusChange, msel.Id + ",Pushing Collection to Gallery", null, ct);
                await IntegrationGalleryExtensions.CreateCollectionAsync(msel, galleryApiClient, blueprintContext, ct);
                // create the Gallery Exhibit
                currentProcessStep = "Gallery - Pushing Exhibit";
                await hubGroup.SendAsync(MainHubMethods.MselPushStatusChange, msel.Id + ",Pushing Exhibit to Gallery", null, ct);
                await IntegrationGalleryExtensions.CreateExhibitAsync(msel, galleryApiClient, blueprintContext, ct);
                // create the Gallery Teams
                currentProcessStep = "Gallery - Pushing Teams";
                await hubGroup.SendAsync(MainHubMethods.MselPushStatusChange, msel.Id + ",Pushing Teams to Gallery", null, ct);
                await IntegrationGalleryExtensions.CreateTeamsAsync(msel, galleryApiClient, blueprintContext, ct);
                // create the Gallery Cards
                currentProcessStep = "Gallery - Pushing Cards";
                await hubGroup.SendAsync(MainHubMethods.MselPushStatusChange, msel.Id + ",Pushing Cards to Gallery", null, ct);
                await IntegrationGalleryExtensions.CreateCardsAsync(msel, galleryApiClient, blueprintContext, ct);
                // create the Gallery Articles
                currentProcessStep = "Gallery - Pushing Articles";
                await hubGroup.SendAsync(MainHubMethods.MselPushStatusChange, msel.Id + ",Pushing Articles to Gallery", null, ct);
                await IntegrationGalleryExtensions.CreateArticlesAsync(msel, galleryApiClient, blueprintContext, scenarioEventService, ct);
                // commit the transaction
                currentProcessStep = "Gallery - commit transaction";
                await hubGroup.SendAsync(MainHubMethods.MselPushStatusChange, msel.Id + ",Commit to Gallery", null, ct);
                await blueprintContext.Database.CommitTransactionAsync(ct);
            }
            catch (System.Exception ex)
            {
                _logger.LogError($"{currentProcessStep} {msel.Name} ({msel.Id})", ex);
                throw ex;
            }
        }

        private async STT.Task SteamfitterProcess(MselEntity msel, SteamfitterApiClient steamfitterApiClient, BlueprintContext blueprintContext, CancellationToken ct)
        {
            var currentProcessStep = "Steamfitter - get hubGroup";
            try
            {
                var hubGroup = _hubContext.Clients.Group(msel.Id.ToString());
                // create the Steamfitter Scenario
                currentProcessStep = "Steamfitter - create scenario";
                await hubGroup.SendAsync(MainHubMethods.MselPushStatusChange, msel.Id + ",Pushing Scenario to Steamfitter", null, ct);
                var scenario = await IntegrationSteamfitterExtensions.CreateScenarioAsync(msel, steamfitterApiClient, blueprintContext, ct);
                // create the scenario tasks
                foreach (var scenarioEvent in msel.ScenarioEvents)
                {
                    if (scenarioEvent.IntegrationTarget.Contains("Steamfitter") && scenarioEvent.SteamfitterTask != null)
                    {
                        currentProcessStep = "Steamfitter - pushing steamfitter task " + scenarioEvent.SteamfitterTaskId.ToString();
                        var steamfitterTask = await IntegrationSteamfitterExtensions.CreateScenarioTasksAsync(
                            msel,
                            scenarioEvent.SteamfitterTask,
                            steamfitterApiClient,
                            _clientOptions.CurrentValue,
                            ct);
                    }
                }
            }
            catch (System.Exception ex)
            {
                _logger.LogError($"{currentProcessStep} {msel.Name} ({msel.Id})", ex);
                throw ex;
            }
        }

    }

    public class IntegrationInformation
    {
        public Guid MselId { get; set; }
        public Guid? PlayerViewId { get; set; }
        public Data.Enumerations.MselItemStatus FinalStatus { get; set; }
    }

}
