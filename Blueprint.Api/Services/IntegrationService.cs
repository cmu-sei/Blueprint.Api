// Copyright 2024 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

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
using System.Collections.Generic;
using System.Linq;


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
        private readonly IOptionsMonitor<Infrastructure.Options.EmailOptions> _emailOptions;

        public IntegrationService(
            ILogger<IntegrationService> logger,
            IServiceScopeFactory scopeFactory,
            IIntegrationQueue integrationQueue,
            IHubContext<MainHub> mainHub,
            IHttpClientFactory httpClientFactory,
            IOptionsMonitor<Infrastructure.Options.ClientOptions> clientOptions,
            IOptionsMonitor<Infrastructure.Options.EmailOptions> emailOptions)
        {
            _logger = logger;
            _scopeFactory = scopeFactory;
            _integrationQueue = integrationQueue;
            _hubContext = mainHub;
            _httpClientFactory = httpClientFactory;
            _clientOptions = clientOptions;
            _emailOptions = emailOptions;
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
                    // Clear any tracked entities from previous operations
                    blueprintContext.ChangeTracker.Clear();

                    currentProcessStep = "Getting the MSEL entity";
                    // get the MSEL and verify data state
                    var msel = await blueprintContext.Msels
                        .Include(m => m.PlayerApplications).ThenInclude(pa => pa.PlayerApplicationTeams)
                        .Include(m => m.Cards)
                        .Include(m => m.DataFields)
                        .Include(m => m.ScenarioEvents).ThenInclude(se => se.DataValues)
                        .Include(m => m.ScenarioEvents).ThenInclude(se => se.SteamfitterTask)
                        .Include(m => m.Moves)
                        .Include(m => m.CiteActions)
                        .Include(m => m.CiteDuties)
                        .Include(m => m.Teams).ThenInclude(t => t.TeamUsers).ThenInclude(tu => tu.User)
                        .Include(m => m.Teams).ThenInclude(t => t.UserTeamRoles)
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
                            // Create all API clients upfront
                            GalleryApiClient galleryApiClient = null;
                            CiteApiClient citeApiClient = null;
                            SteamfitterApiClient steamfitterApiClient = null;
                            if (msel.UsePlayer)
                                playerApiClient = IntegrationPlayerExtensions.GetPlayerApiClient(_httpClientFactory, _clientOptions.CurrentValue.PlayerApiUrl, tokenResponse);
                            if (msel.UseGallery)
                                galleryApiClient = IntegrationGalleryExtensions.GetGalleryApiClient(_httpClientFactory, _clientOptions.CurrentValue.GalleryApiUrl, tokenResponse);
                            if (msel.UseCite)
                                citeApiClient = IntegrationCiteExtensions.GetCiteApiClient(_httpClientFactory, _clientOptions.CurrentValue.CiteApiUrl, tokenResponse);
                            if (msel.UseSteamfitter)
                                steamfitterApiClient = IntegrationSteamfitterExtensions.GetSteamfitterApiClient(_httpClientFactory, _clientOptions.CurrentValue.SteamfitterApiUrl, tokenResponse);

                            // Pre-fetch user lists from external services in parallel
                            HashSet<Guid> playerUserIds = null, galleryUserIds = null, citeUserIds = null;
                            var userFetchTasks = new List<STT.Task>();
                            if (msel.UsePlayer)
                                userFetchTasks.Add(STT.Task.Run(async () =>
                                {
                                    playerUserIds = (await playerApiClient.GetUsersAsync(ct)).Select(u => u.Id).ToHashSet();
                                }));
                            if (msel.UseGallery)
                                userFetchTasks.Add(STT.Task.Run(async () =>
                                {
                                    galleryUserIds = (await galleryApiClient.GetUsersAsync(ct)).Select(u => u.Id).ToHashSet();
                                }));
                            if (msel.UseCite)
                                userFetchTasks.Add(STT.Task.Run(async () =>
                                {
                                    citeUserIds = (await citeApiClient.GetUsersAsync(ct)).Select(u => u.Id).ToHashSet();
                                }));
                            await STT.Task.WhenAll(userFetchTasks);

                            // Player processing part 1
                            if (msel.UsePlayer)
                            {
                                currentProcessStep = "Player - get API client with token: " + tokenResponse.AccessToken;
                                await hubGroup.SendAsync(MainHubMethods.MselPushStatusChange, msel.Id + ",Pushing Integrations", null, ct);
                                currentProcessStep = "Player - begin processing part 1";
                                await PlayerProcessPart1(msel, integrationInformation.PlayerViewId, playerApiClient, blueprintContext, playerUserIds, ct);
                            }

                            // Gallery processing
                            if (msel.UseGallery)
                            {
                                currentProcessStep = "Gallery - get scenario event service";
                                var scenarioEventService = scope.ServiceProvider.GetRequiredService<IScenarioEventService>();
                                currentProcessStep = "Gallery - start GalleryProcess";
                                await GalleryProcess(msel, scenarioEventService, galleryApiClient, blueprintContext, galleryUserIds, ct);
                            }

                            // CITE processing
                            if (msel.UseCite)
                            {
                                currentProcessStep = "CITE - begin processing";
                                await CiteProcess(msel, citeApiClient, blueprintContext, citeUserIds, ct);
                            }

                            // Steamfitter processing
                            if (msel.UseSteamfitter)
                            {
                                currentProcessStep = "Steamfitter - begin processing";
                                await SteamfitterProcess(msel, steamfitterApiClient, blueprintContext, ct);
                            }

                            // Player processing part 2
                            if (msel.UsePlayer)
                            {
                                await hubGroup.SendAsync(MainHubMethods.MselPushStatusChange, msel.Id + ",Push Player Applications", null, ct);
                                currentProcessStep = "Player - push applications";
                                await IntegrationPlayerExtensions.CreateApplicationsAsync(msel, playerApiClient, blueprintContext, _clientOptions.CurrentValue.PlayerMaxConcurrentRequests, ct);
                            }
                            // set the MSEL status
                            msel.Status = Data.Enumerations.MselItemStatus.Deployed;
                            await blueprintContext.SaveChangesAsync(ct);

                            // Clear tracked entities to prevent accumulation across operations
                            blueprintContext.ChangeTracker.Clear();

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
                                    _logger.LogError($"{currentProcessStep} ({msel.Id})");
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
                                    _logger.LogError(ex, $"{currentProcessStep} ({msel.Id})");
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
                                    _logger.LogError(ex, $"{currentProcessStep} ({msel.Id})");
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
                                    _logger.LogError($"{currentProcessStep} ({msel.Id})");
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

                            // Clear tracked entities to prevent accumulation across operations
                            blueprintContext.ChangeTracker.Clear();

                            // send completion status
                            await hubGroup.SendAsync(MainHubMethods.MselPushStatusChange, mselId + "", null, ct);
                        }

                    }
                    catch (System.Exception ex)
                    {
                        _logger.LogError(ex, $"{currentProcessStep} ({msel.Id})");
                    }

                }
            }
            catch (System.Exception ex)
            {
                _logger.LogError(ex, $"{currentProcessStep} {integrationInformation}");
            }
        }

        private bool CanMselBePushed(MselEntity mselToIntegrate)
        {
            // TODO: build this out!!!
            return true;
        }

        private async STT.Task PlayerProcessPart1(MselEntity msel, Guid? playerViewId, PlayerApiClient playerApiClient, BlueprintContext blueprintContext, HashSet<Guid> playerUserIds, CancellationToken ct)
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
                await IntegrationPlayerExtensions.CreateTeamsAsync(msel, playerApiClient, blueprintContext, playerUserIds, ct);
            }
            catch (System.Exception ex)
            {
                _logger.LogError(ex, $"{currentProcessStep} ({msel.Id})");
                throw;
            }
        }

        private async STT.Task CiteProcess(MselEntity msel, CiteApiClient citeApiClient, BlueprintContext blueprintContext, HashSet<Guid> citeUserIds, CancellationToken ct)
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
                await IntegrationCiteExtensions.CreateMovesAsync(msel, citeApiClient, blueprintContext, _clientOptions.CurrentValue.CiteMaxConcurrentRequests, ct);
                // create the Cite Teams
                currentProcessStep = "CITE - create teams";
                await hubGroup.SendAsync(MainHubMethods.MselPushStatusChange, msel.Id + ",Pushing Teams to CITE", null, ct);
                await IntegrationCiteExtensions.CreateTeamsAsync(msel, citeApiClient, blueprintContext, citeUserIds, ct);
                // create the Cite Duties
                currentProcessStep = "CITE - create duties";
                await hubGroup.SendAsync(MainHubMethods.MselPushStatusChange, msel.Id + ",Pushing Duties to CITE", null, ct);
                await IntegrationCiteExtensions.CreateDutiesAsync(msel, citeApiClient, blueprintContext, _clientOptions.CurrentValue.CiteMaxConcurrentRequests, ct);
                // create the Cite Actions
                currentProcessStep = "CITE - create actions";
                await hubGroup.SendAsync(MainHubMethods.MselPushStatusChange, msel.Id + ",Pushing Actions to CITE", null, ct);
                await IntegrationCiteExtensions.CreateActionsAsync(msel, citeApiClient, blueprintContext, _clientOptions.CurrentValue.CiteMaxConcurrentRequests, ct);
                // update the evaluation, so that submissions get created
                currentProcessStep = "CITE - advance";
                await hubGroup.SendAsync(MainHubMethods.MselPushStatusChange, msel.Id + ",Finishing Evaluation to CITE", null, ct);
                evaluation.Status = Cite.Api.Client.ItemStatus.Active;
                await IntegrationCiteExtensions.ActivateAsync(evaluation, citeApiClient, blueprintContext, ct);
            }
            catch (System.Exception ex)
            {
                _logger.LogError(ex, $"{currentProcessStep} ({msel.Id})");
                throw;
            }
        }

        private async STT.Task GalleryProcess(MselEntity msel, IScenarioEventService scenarioEventService, GalleryApiClient galleryApiClient, BlueprintContext blueprintContext, HashSet<Guid> galleryUserIds, CancellationToken ct)
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
                var emailEnabled = _emailOptions.CurrentValue.Enabled && msel.EmailEnabled;
                await IntegrationGalleryExtensions.CreateTeamsAsync(msel, galleryApiClient, blueprintContext, galleryUserIds, emailEnabled, ct);
                // create the Gallery Cards
                currentProcessStep = "Gallery - Pushing Cards";
                await hubGroup.SendAsync(MainHubMethods.MselPushStatusChange, msel.Id + ",Pushing Cards to Gallery", null, ct);
                await IntegrationGalleryExtensions.CreateCardsAsync(msel, galleryApiClient, blueprintContext, _clientOptions.CurrentValue.GalleryMaxConcurrentRequests, ct);
                // create the Gallery Articles
                currentProcessStep = "Gallery - Pushing Articles";
                await hubGroup.SendAsync(MainHubMethods.MselPushStatusChange, msel.Id + ",Pushing Articles to Gallery", null, ct);
                await IntegrationGalleryExtensions.CreateArticlesAsync(msel, galleryApiClient, blueprintContext, scenarioEventService, _clientOptions.CurrentValue.GalleryMaxConcurrentRequests, ct);
                // commit the transaction
                currentProcessStep = "Gallery - commit transaction";
                await hubGroup.SendAsync(MainHubMethods.MselPushStatusChange, msel.Id + ",Commit to Gallery", null, ct);
                await blueprintContext.Database.CommitTransactionAsync(ct);
            }
            catch (System.Exception ex)
            {
                _logger.LogError(ex, $"{currentProcessStep} ({msel.Id})");
                throw;
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
                var sortedScenarioEvents = msel.ScenarioEvents.OrderBy(m => m.DeltaSeconds).ToList();
                var sortedMoves = msel.Moves.OrderBy(m => m.MoveNumber).ToList();
                var movesAndGroups = GetMovesAndGroups(sortedScenarioEvents, sortedMoves);
                var clientOptions = _clientOptions.CurrentValue;
                var playerApiUrl = clientOptions.PlayerApiUrl.EndsWith("/") ? clientOptions.PlayerApiUrl + "api/" : clientOptions.PlayerApiUrl + "/api/";
                var citeApiUrl = clientOptions.CiteApiUrl.EndsWith("/") ? clientOptions.CiteApiUrl + "api/" : clientOptions.CiteApiUrl + "/api/";
                var galleryApiUrl = clientOptions.GalleryApiUrl.EndsWith("/") ? clientOptions.GalleryApiUrl + "api/" : clientOptions.GalleryApiUrl + "/api/";
                var moveNumber = -1;
                var groupNumber = 0;
                Task triggerTask = null;
                foreach (var scenarioEvent in sortedScenarioEvents)
                {
                    if (movesAndGroups[scenarioEvent.Id][0] > moveNumber)
                    {
                        moveNumber = movesAndGroups[scenarioEvent.Id][0];
                        groupNumber = 0;
                        triggerTask = await IntegrationSteamfitterExtensions.CreateNextMoveTasksAsync(
                            msel,
                            steamfitterApiClient,
                            moveNumber,
                            citeApiUrl,
                            galleryApiUrl,
                            null,
                            ct);
                    }
                    else if (movesAndGroups[scenarioEvent.Id][1] > groupNumber)
                    {
                        groupNumber = movesAndGroups[scenarioEvent.Id][1];
                        triggerTask = await IntegrationSteamfitterExtensions.CreateNextGroupTasksAsync(
                            msel,
                            steamfitterApiClient,
                            moveNumber,
                            groupNumber,
                            citeApiUrl,
                            galleryApiUrl,
                            null,
                            ct);
                    }
                    if (scenarioEvent.IntegrationTarget.Contains("Steamfitter") && scenarioEvent.SteamfitterTask != null)
                    {
                        currentProcessStep = "Steamfitter - pushing steamfitter task " + scenarioEvent.SteamfitterTaskId.ToString();
                        var emailEnabled = _emailOptions.CurrentValue.Enabled && msel.EmailEnabled;
                        triggerTask = await IntegrationSteamfitterExtensions.CreateScenarioTasksAsync(
                            msel,
                            scenarioEvent.SteamfitterTask,
                            steamfitterApiClient,
                            moveNumber,
                            groupNumber,
                            playerApiUrl,
                            citeApiUrl,
                            galleryApiUrl,
                            triggerTask,
                            emailEnabled,
                            ct);
                    }
                }
            }
            catch (System.Exception ex)
            {
                _logger.LogError(ex, $"{currentProcessStep} ({msel.Id})");
                throw;
            }
        }

        private static Dictionary<Guid, int[]> GetMovesAndGroups(
            List<ScenarioEventEntity> sortedScenarioEvents,
            List<MoveEntity> sortedMoves)
        {
            var movesAndGroups = new Dictionary<Guid, int[]>();
            if (sortedScenarioEvents.Count > 0)
            {
                var moveIndex = sortedMoves.Count > 0 ? -1 : 0;
                var groupIndex = 0;
                var groupSeconds = sortedScenarioEvents[0].DeltaSeconds;
                //loop through ordered scenario events
                for (int index = 0; index < sortedScenarioEvents.Count; index++)
                {
                    var scenarioEvent = sortedScenarioEvents[index];
                    if (moveIndex < sortedMoves.Count - 1 && scenarioEvent.DeltaSeconds >= sortedMoves[moveIndex + 1].DeltaSeconds)
                    {
                        moveIndex++;
                        groupIndex = 0;
                        groupSeconds = scenarioEvent.DeltaSeconds;
                    }
                    else if (scenarioEvent.DeltaSeconds > groupSeconds)
                    {
                        groupIndex++;
                        groupSeconds = scenarioEvent.DeltaSeconds;
                    }
                    var moveNumber = moveIndex < 0 || sortedMoves.Count == 0 ? -1 : sortedMoves[moveIndex].MoveNumber;
                    movesAndGroups[scenarioEvent.Id] = [moveIndex, groupIndex];
                }
            }
            return movesAndGroups;
        }

    }

    public class IntegrationInformation
    {
        public Guid MselId { get; set; }
        public Guid? PlayerViewId { get; set; }
        public Data.Enumerations.MselItemStatus FinalStatus { get; set; }
    }

}
