// Copyright 2024 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Blueprint.Api.Data.Models;
using System;
using System.Collections.Concurrent;
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
using Microsoft.AspNetCore.SignalR;
using Blueprint.Api.Hubs;


namespace Blueprint.Api.Services
{
    public interface IIntegrationService : IHostedService
    {
        void CancelPush(Guid mselId);
    }

    public class IntegrationService : IIntegrationService
    {
        private readonly ILogger<IntegrationService> _logger;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly IIntegrationQueue _integrationQueue;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IOptionsMonitor<Infrastructure.Options.ClientOptions> _clientOptions;
        private readonly IHubContext<MainHub> _mainHub;
        private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _cancellationTokenSources = new();

        public IntegrationService(
            ILogger<IntegrationService> logger,
            IServiceScopeFactory scopeFactory,
            IIntegrationQueue integrationQueue,
            IHttpClientFactory httpClientFactory,
            IOptionsMonitor<Infrastructure.Options.ClientOptions> clientOptions,
            IHubContext<MainHub> mainHub)
        {
            _logger = logger;
            _scopeFactory = scopeFactory;
            _integrationQueue = integrationQueue;
            _httpClientFactory = httpClientFactory;
            _clientOptions = clientOptions;
            _mainHub = mainHub;
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

        public void CancelPush(Guid mselId)
        {
            if (_cancellationTokenSources.TryGetValue(mselId, out var cts))
                cts.Cancel();
            else
                _ = PerformCancelCleanupAsync(mselId);
        }

        /// <summary>
        /// Performs cancel cleanup using only ExecuteUpdateAsync and external API calls.
        /// Never calls SaveChangesAsync, which avoids the EntityEventInterceptor → MediatR →
        /// SignalR notification chain that can hang on slow/saturated browser connections.
        /// </summary>
        private async STT.Task PerformCancelCleanupAsync(Guid mselId)
        {
            _logger.LogInformation($"Integration push cancelled for MSEL {mselId}. Starting cleanup.");
            var freshCt = new CancellationToken();
            try
            {
                using var scope = _scopeFactory.CreateScope();
                using var dbContext = scope.ServiceProvider.GetRequiredService<BlueprintContext>();

                // Step 1: Update status directly via ExecuteUpdateAsync (bypasses EF tracking/interceptor).
                var cancelStatus = "Cancelling - removing partial integrations";
                var updated = await dbContext.Msels
                    .Where(m => m.Id == mselId)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(m => m.Status, Data.Enumerations.MselItemStatus.Pulling)
                        .SetProperty(m => m.IntegrationStatus, cancelStatus),
                        freshCt);
                _logger.LogInformation($"Cancel cleanup: status update affected {updated} row(s) for MSEL {mselId}.");
                // Send lightweight SignalR notification for cancel progress
                await _mainHub.Clients.Group(mselId.ToString())
                    .SendAsync(MainHubMethods.IntegrationStatusUpdated, mselId, cancelStatus, freshCt);
                await _mainHub.Clients.Group(MainHub.ADMIN_DATA_GROUP)
                    .SendAsync(MainHubMethods.IntegrationStatusUpdated, mselId, cancelStatus, freshCt);

                // Step 2: Load MSEL AsNoTracking — we only need the integration IDs for API delete calls.
                var msel = await dbContext.Msels
                    .AsNoTracking()
                    .FirstOrDefaultAsync(m => m.Id == mselId, freshCt);

                if (msel == null)
                {
                    _logger.LogWarning($"Cancel cleanup: MSEL {mselId} not found in database.");
                    return;
                }

                _logger.LogInformation($"Cancel cleanup: MSEL {mselId} loaded. PlayerViewId={msel.PlayerViewId}, GalleryCollectionId={msel.GalleryCollectionId}, CiteEvaluationId={msel.CiteEvaluationId}, SteamfitterScenarioId={msel.SteamfitterScenarioId}");

                // Step 3: Get auth token for external API calls.
                var tokenResponse = await ApiClientsExtensions.GetToken(scope);
                _logger.LogInformation($"Cancel cleanup: token acquired for MSEL {mselId}. Starting external pulls.");

                // Step 4: Call external API deletes directly (no BlueprintContext/SaveChanges needed).
                if (msel.SteamfitterScenarioId != null)
                {
                    try
                    {
                        _logger.LogInformation($"Cancel cleanup: pulling Steamfitter scenario for MSEL {mselId}.");
                        var steamfitterApiClient = IntegrationSteamfitterExtensions.GetSteamfitterApiClient(_httpClientFactory, _clientOptions.CurrentValue.SteamfitterApiUrl, tokenResponse);
                        await IntegrationSteamfitterExtensions.PullFromSteamfitterAsync((Guid)msel.SteamfitterScenarioId, steamfitterApiClient, freshCt);
                    }
                    catch (System.Exception ex)
                    {
                        _logger.LogError(ex, $"Cancel cleanup: Steamfitter pull failed ({mselId})");
                    }
                }

                if (msel.CiteEvaluationId != null)
                {
                    try
                    {
                        _logger.LogInformation($"Cancel cleanup: pulling CITE evaluation for MSEL {mselId}.");
                        var citeApiClient = IntegrationCiteExtensions.GetCiteApiClient(_httpClientFactory, _clientOptions.CurrentValue.CiteApiUrl, tokenResponse);
                        await IntegrationCiteExtensions.PullFromCiteAsync((Guid)msel.CiteEvaluationId, citeApiClient, freshCt);
                    }
                    catch (System.Exception ex)
                    {
                        _logger.LogError(ex, $"Cancel cleanup: CITE pull failed ({mselId})");
                    }
                }

                if (msel.GalleryCollectionId != null)
                {
                    try
                    {
                        _logger.LogInformation($"Cancel cleanup: pulling Gallery collection for MSEL {mselId}.");
                        var galleryApiClient = IntegrationGalleryExtensions.GetGalleryApiClient(_httpClientFactory, _clientOptions.CurrentValue.GalleryApiUrl, tokenResponse);
                        await IntegrationGalleryExtensions.PullFromGalleryAsync((Guid)msel.GalleryCollectionId, galleryApiClient, freshCt);
                    }
                    catch (System.Exception ex)
                    {
                        _logger.LogError(ex, $"Cancel cleanup: Gallery pull failed ({mselId})");
                    }
                }

                if (msel.PlayerViewId != null)
                {
                    try
                    {
                        _logger.LogInformation($"Cancel cleanup: pulling Player view for MSEL {mselId}.");
                        var playerApiClient = IntegrationPlayerExtensions.GetPlayerApiClient(_httpClientFactory, _clientOptions.CurrentValue.PlayerApiUrl, tokenResponse);
                        await IntegrationPlayerExtensions.PullFromPlayerAsync((Guid)msel.PlayerViewId, playerApiClient, freshCt);
                        await IntegrationPlayerExtensions.PullFromPlayerAsync((Guid)msel.PlayerViewId, playerApiClient, freshCt);
                    }
                    catch (System.Exception ex)
                    {
                        _logger.LogError(ex, $"Cancel cleanup: Player pull failed ({mselId})");
                    }
                }

                // Step 5: Use a fresh scope/context for completion so SaveChangesAsync triggers
                // the full MselUpdated SignalR notification with minimal tracked entities.
                _logger.LogInformation($"Cancel cleanup: clearing integration IDs for MSEL {mselId}.");
                using (var finalScope = _scopeFactory.CreateScope())
                using (var finalContext = finalScope.ServiceProvider.GetRequiredService<BlueprintContext>())
                {
                    var finalMsel = await finalContext.Msels.FindAsync(new object[] { mselId }, freshCt);
                    finalMsel.PlayerViewId = null;
                    finalMsel.GalleryExhibitId = null;
                    finalMsel.GalleryCollectionId = null;
                    finalMsel.CiteEvaluationId = null;
                    finalMsel.SteamfitterScenarioId = null;
                    finalMsel.IntegrationStatus = null;
                    finalMsel.Status = Data.Enumerations.MselItemStatus.Approved;
                    await finalContext.SaveChangesAsync(freshCt);
                }

                _logger.LogInformation($"Cancel cleanup: complete for MSEL {mselId}.");
            }
            catch (System.Exception cleanupEx)
            {
                _logger.LogError(cleanupEx, $"Error during cancellation cleanup for MSEL {mselId}");
                try
                {
                    using var errorScope = _scopeFactory.CreateScope();
                    using var errorContext = errorScope.ServiceProvider.GetRequiredService<BlueprintContext>();
                    var errorStatus = $"ERROR: Cancellation cleanup failed - {cleanupEx.Message}";
                    await errorContext.Msels
                        .Where(m => m.Id == mselId)
                        .ExecuteUpdateAsync(s => s
                            .SetProperty(m => m.IntegrationStatus, errorStatus),
                            freshCt);
                    await _mainHub.Clients.Group(mselId.ToString())
                        .SendAsync(MainHubMethods.IntegrationStatusUpdated, mselId, errorStatus, freshCt);
                    await _mainHub.Clients.Group(MainHub.ADMIN_DATA_GROUP)
                        .SendAsync(MainHubMethods.IntegrationStatusUpdated, mselId, errorStatus, freshCt);
                }
                catch { /* best effort */ }
            }
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

        private async STT.Task UpdateIntegrationStatusAsync(MselEntity msel, BlueprintContext blueprintContext, string status, CancellationToken ct)
        {
            msel.IntegrationStatus = status;
            await blueprintContext.SaveChangesAsync(ct);
        }

        private async void ProcessTheMsel(Object integrationInformationObject)
        {
            var integrationInformation = (IntegrationInformation)integrationInformationObject;
            var cts = new CancellationTokenSource();
            _cancellationTokenSources[integrationInformation.MselId] = cts;
            var ct = cts.Token;
            var currentProcessStep = "Begin processing";
            _logger.LogDebug($"{currentProcessStep} {integrationInformation.MselId}");
            Guid? cancelledMselId = null;
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
                    var isAPush = integrationInformation.IsPush;
                    currentProcessStep = "Try processing the MSEL";
                    try
                    {
                        PlayerApiClient playerApiClient = null;
                        currentProcessStep = "Getting Auth Token with scope " + scope;
                        var tokenResponse = await ApiClientsExtensions.GetToken(scope);
                        if (isAPush)
                        {
                            // Pre-generate integration object IDs and save before calling remote APIs
                            if (msel.UsePlayer && msel.PlayerViewId == null)
                                msel.PlayerViewId = Guid.NewGuid();
                            if (msel.UseGallery)
                            {
                                if (msel.GalleryCollectionId == null)
                                    msel.GalleryCollectionId = Guid.NewGuid();
                                if (msel.GalleryExhibitId == null)
                                    msel.GalleryExhibitId = Guid.NewGuid();
                            }
                            if (msel.UseCite && msel.CiteEvaluationId == null)
                                msel.CiteEvaluationId = Guid.NewGuid();
                            if (msel.UseSteamfitter && msel.SteamfitterScenarioId == null)
                                msel.SteamfitterScenarioId = Guid.NewGuid();

                            await UpdateIntegrationStatusAsync(msel, blueprintContext, "Pushing Integrations", ct);
                        }
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
                                ct.ThrowIfCancellationRequested();
                                currentProcessStep = "Player - begin processing part 1";
                                await PlayerProcessPart1(msel, integrationInformation.PlayerViewId, playerApiClient, blueprintContext, playerUserIds, ct);
                            }

                            // Gallery processing
                            if (msel.UseGallery)
                            {
                                ct.ThrowIfCancellationRequested();
                                currentProcessStep = "Gallery - get scenario event service";
                                var scenarioEventService = scope.ServiceProvider.GetRequiredService<IScenarioEventService>();
                                currentProcessStep = "Gallery - start GalleryProcess";
                                await GalleryProcess(msel, scenarioEventService, galleryApiClient, blueprintContext, galleryUserIds, ct);
                            }

                            // CITE processing
                            if (msel.UseCite)
                            {
                                ct.ThrowIfCancellationRequested();
                                currentProcessStep = "CITE - begin processing";
                                await CiteProcess(msel, citeApiClient, blueprintContext, citeUserIds, ct);
                            }

                            // Steamfitter processing
                            if (msel.UseSteamfitter)
                            {
                                ct.ThrowIfCancellationRequested();
                                currentProcessStep = "Steamfitter - begin processing";
                                await SteamfitterProcess(msel, steamfitterApiClient, blueprintContext, ct);
                            }

                            // Player processing part 2
                            if (msel.UsePlayer)
                            {
                                ct.ThrowIfCancellationRequested();
                                await SendIntegrationStatusAsync(blueprintContext, msel.Id, "Pushing Player Applications", ct);
                                currentProcessStep = "Player - push applications";
                                await IntegrationPlayerExtensions.CreateApplicationsAsync(msel, playerApiClient, blueprintContext, _clientOptions.CurrentValue.PlayerMaxConcurrentRequests, _clientOptions.CurrentValue, ct);
                            }
                            // Use a fresh scope/context for completion so SaveChangesAsync triggers
                            // the full MselUpdated SignalR notification with minimal tracked entities.
                            using (var finalScope = _scopeFactory.CreateScope())
                            using (var finalContext = finalScope.ServiceProvider.GetRequiredService<BlueprintContext>())
                            {
                                var finalMsel = await finalContext.Msels.FindAsync(new object[] { msel.Id }, ct);
                                finalMsel.Status = Data.Enumerations.MselItemStatus.Deployed;
                                finalMsel.IntegrationStatus = null;
                                await finalContext.SaveChangesAsync(ct);
                            }
                        }
                        else
                        {
                            await PullIntegrations(msel, integrationInformation, blueprintContext, tokenResponse, ct);
                        }

                    }
                    catch (OperationCanceledException)
                    {
                        // Capture the ID; cleanup runs after the using block disposes
                        // blueprintContext (releasing any open transaction/row locks).
                        cancelledMselId = msel.Id;
                    }
                    catch (System.Exception ex)
                    {
                        _logger.LogError(ex, $"{currentProcessStep} ({msel.Id})");
                        var freshCt = new CancellationToken();
                        var errorStatus = $"ERROR: {currentProcessStep} failed - {ex.Message}";
                        // Use ExecuteUpdateAsync + direct SignalR for error (don't risk SaveChangesAsync in error recovery)
                        await blueprintContext.Msels
                            .Where(m => m.Id == msel.Id)
                            .ExecuteUpdateAsync(s => s.SetProperty(m => m.IntegrationStatus, errorStatus), freshCt);
                        await _mainHub.Clients.Group(msel.Id.ToString())
                            .SendAsync(MainHubMethods.IntegrationStatusUpdated, msel.Id, errorStatus, freshCt);
                        await _mainHub.Clients.Group(MainHub.ADMIN_DATA_GROUP)
                            .SendAsync(MainHubMethods.IntegrationStatusUpdated, msel.Id, errorStatus, freshCt);
                    }

                }
                // blueprintContext is fully disposed here — all DB locks released.
                // Safe to run cleanup now without hitting a lock wait on msels.
                if (cancelledMselId.HasValue)
                    await PerformCancelCleanupAsync(cancelledMselId.Value);
            }
            catch (System.Exception ex)
            {
                _logger.LogError(ex, $"{currentProcessStep} {integrationInformation}");
                // Try to update integration status with error via ExecuteUpdateAsync + direct SignalR
                try
                {
                    using var scope = _scopeFactory.CreateScope();
                    using var blueprintContext = scope.ServiceProvider.GetRequiredService<BlueprintContext>();
                    var mselId = integrationInformation?.MselId ?? Guid.Empty;
                    if (mselId != Guid.Empty)
                    {
                        var errorStatus = $"ERROR: {currentProcessStep} failed - {ex.Message}";
                        await blueprintContext.Msels
                            .Where(m => m.Id == mselId)
                            .ExecuteUpdateAsync(s => s
                                .SetProperty(m => m.IntegrationStatus, errorStatus));
                        await _mainHub.Clients.Group(mselId.ToString())
                            .SendAsync(MainHubMethods.IntegrationStatusUpdated, mselId, errorStatus);
                        await _mainHub.Clients.Group(MainHub.ADMIN_DATA_GROUP)
                            .SendAsync(MainHubMethods.IntegrationStatusUpdated, mselId, errorStatus);
                    }
                }
                catch (System.Exception innerEx)
                {
                    _logger.LogError(innerEx, "Failed to update integration status after outer error.");
                }
            }
            finally
            {
                _cancellationTokenSources.TryRemove(integrationInformation.MselId, out _);
                cts.Dispose();
            }
        }

        /// <summary>
        /// Pulls (deletes) integrations from external services and clears integration IDs.
        /// Uses ExecuteUpdateAsync for all DB writes to avoid the EntityEventInterceptor →
        /// MediatR → SignalR chain that can hang on slow/saturated browser connections.
        /// </summary>
        private async STT.Task PullIntegrations(MselEntity msel, IntegrationInformation integrationInformation, BlueprintContext blueprintContext, IdentityModel.Client.TokenResponse tokenResponse, CancellationToken ct)
        {
            var mselId = msel.Id;
            var currentProcessStep = "Pulling Integrations";
            await SendIntegrationStatusAsync(blueprintContext, mselId, "Pulling Integrations", ct);

            // Pull from Steamfitter
            if (msel.SteamfitterScenarioId != null)
            {
                try
                {
                    currentProcessStep = "Steamfitter - pull scenario";
                    await SendIntegrationStatusAsync(blueprintContext, mselId, "Pulling Steamfitter Scenario", ct);
                    var steamfitterApiClient = IntegrationSteamfitterExtensions.GetSteamfitterApiClient(_httpClientFactory, _clientOptions.CurrentValue.SteamfitterApiUrl, tokenResponse);
                    await IntegrationSteamfitterExtensions.PullFromSteamfitterAsync((Guid)msel.SteamfitterScenarioId, steamfitterApiClient, ct);
                }
                catch (System.Exception ex)
                {
                    _logger.LogError(ex, $"{currentProcessStep} ({msel.Id})");
                }
            }
            // Pull from CITE
            if (msel.CiteEvaluationId != null)
            {
                try
                {
                    currentProcessStep = "CITE - pull evaluation";
                    await SendIntegrationStatusAsync(blueprintContext, mselId, "Pulling CITE Evaluation", ct);
                    var citeApiClient = IntegrationCiteExtensions.GetCiteApiClient(_httpClientFactory, _clientOptions.CurrentValue.CiteApiUrl, tokenResponse);
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
                    currentProcessStep = "Gallery - pull collection";
                    await SendIntegrationStatusAsync(blueprintContext, mselId, "Pulling Gallery Collection", ct);
                    var galleryApiClient = IntegrationGalleryExtensions.GetGalleryApiClient(_httpClientFactory, _clientOptions.CurrentValue.GalleryApiUrl, tokenResponse);
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
                    currentProcessStep = "Player - pull view";
                    await SendIntegrationStatusAsync(blueprintContext, mselId, "Pulling Player View", ct);
                    var playerApiClient = IntegrationPlayerExtensions.GetPlayerApiClient(_httpClientFactory, _clientOptions.CurrentValue.PlayerApiUrl, tokenResponse);
                    // TODO:  Player requires two deletes?
                    await IntegrationPlayerExtensions.PullFromPlayerAsync((Guid)msel.PlayerViewId, playerApiClient, ct);
                    await IntegrationPlayerExtensions.PullFromPlayerAsync((Guid)msel.PlayerViewId, playerApiClient, ct);
                }
                catch (System.Exception ex)
                {
                    _logger.LogError(ex, $"{currentProcessStep} ({msel.Id})");
                }
            }
            // Use a fresh scope/context for completion so SaveChangesAsync triggers
            // the full MselUpdated SignalR notification with minimal tracked entities.
            currentProcessStep = "MSEL update";
            using (var finalScope = _scopeFactory.CreateScope())
            using (var finalContext = finalScope.ServiceProvider.GetRequiredService<BlueprintContext>())
            {
                var finalMsel = await finalContext.Msels.FindAsync(new object[] { mselId }, ct);
                finalMsel.PlayerViewId = null;
                finalMsel.GalleryExhibitId = null;
                finalMsel.GalleryCollectionId = null;
                finalMsel.CiteEvaluationId = null;
                finalMsel.SteamfitterScenarioId = null;
                finalMsel.IntegrationStatus = null;
                finalMsel.Status = integrationInformation.FinalStatus;
                await finalContext.SaveChangesAsync(ct);
            }
        }

        /// <summary>
        /// Updates the IntegrationStatus field via ExecuteUpdateAsync (bypasses EF tracking/interceptor)
        /// and sends a lightweight SignalR notification directly to connected clients.
        /// </summary>
        private async STT.Task SendIntegrationStatusAsync(BlueprintContext blueprintContext, Guid mselId, string status, CancellationToken ct)
        {
            // Persist to DB (bypasses interceptor/SignalR)
            await blueprintContext.Msels
                .Where(m => m.Id == mselId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(m => m.IntegrationStatus, status),
                    ct);

            // Send lightweight SignalR notification directly
            await _mainHub.Clients.Group(mselId.ToString())
                .SendAsync(MainHubMethods.IntegrationStatusUpdated, mselId, status, ct);
            await _mainHub.Clients.Group(MainHub.ADMIN_DATA_GROUP)
                .SendAsync(MainHubMethods.IntegrationStatusUpdated, mselId, status, ct);
        }

        private bool CanMselBePushed(MselEntity mselToIntegrate)
        {
            // TODO: build this out!!!
            return true;
        }

        private async STT.Task PlayerProcessPart1(MselEntity msel, Guid? playerViewId, PlayerApiClient playerApiClient, BlueprintContext blueprintContext, HashSet<Guid> playerUserIds, CancellationToken ct)
        {
            var currentProcessStep = "Player create view";
            try
            {
                // create the Player View
                await SendIntegrationStatusAsync(blueprintContext, msel.Id, "Pushing View to Player", ct);
                await IntegrationPlayerExtensions.CreateViewAsync(msel, playerViewId, playerApiClient, blueprintContext, ct);
                // create the Player Teams
                currentProcessStep = "Player create teams";
                await SendIntegrationStatusAsync(blueprintContext, msel.Id, "Pushing Teams to Player", ct);
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
            var currentProcessStep = "CITE - create evaluation";
            try
            {
                // create the Cite Evaluation
                await SendIntegrationStatusAsync(blueprintContext, msel.Id, "Pushing Evaluation to CITE", ct);
                var evaluation = await IntegrationCiteExtensions.CreateEvaluationAsync(msel, citeApiClient, blueprintContext, ct);
                // create the Cite Moves
                currentProcessStep = "CITE - create moves";
                await SendIntegrationStatusAsync(blueprintContext, msel.Id, "Pushing Moves to CITE", ct);
                await IntegrationCiteExtensions.CreateMovesAsync(msel, citeApiClient, blueprintContext, _clientOptions.CurrentValue.CiteMaxConcurrentRequests, ct);
                // create the Cite Teams
                currentProcessStep = "CITE - create teams";
                await SendIntegrationStatusAsync(blueprintContext, msel.Id, "Pushing Teams to CITE", ct);
                await IntegrationCiteExtensions.CreateTeamsAsync(msel, citeApiClient, blueprintContext, citeUserIds, ct);
                // create the Cite Duties
                currentProcessStep = "CITE - create duties";
                await SendIntegrationStatusAsync(blueprintContext, msel.Id, "Pushing Duties to CITE", ct);
                await IntegrationCiteExtensions.CreateDutiesAsync(msel, citeApiClient, blueprintContext, _clientOptions.CurrentValue.CiteMaxConcurrentRequests, ct);
                // create the Cite Actions
                currentProcessStep = "CITE - create actions";
                await SendIntegrationStatusAsync(blueprintContext, msel.Id, "Pushing Actions to CITE", ct);
                await IntegrationCiteExtensions.CreateActionsAsync(msel, citeApiClient, blueprintContext, _clientOptions.CurrentValue.CiteMaxConcurrentRequests, ct);
                // update the evaluation, so that submissions get created
                currentProcessStep = "CITE - advance";
                await SendIntegrationStatusAsync(blueprintContext, msel.Id, "Finishing Evaluation to CITE", ct);
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
            var currentProcessStep = "Gallery - Pushing Collection";
            try
            {
                // create the Gallery Collection
                await SendIntegrationStatusAsync(blueprintContext, msel.Id, "Pushing Collection to Gallery", ct);
                await IntegrationGalleryExtensions.CreateCollectionAsync(msel, galleryApiClient, blueprintContext, ct);
                // create the Gallery Exhibit
                currentProcessStep = "Gallery - Pushing Exhibit";
                await SendIntegrationStatusAsync(blueprintContext, msel.Id, "Pushing Exhibit to Gallery", ct);
                await IntegrationGalleryExtensions.CreateExhibitAsync(msel, galleryApiClient, blueprintContext, ct);
                // create the Gallery Teams
                currentProcessStep = "Gallery - Pushing Teams";
                await SendIntegrationStatusAsync(blueprintContext, msel.Id, "Pushing Teams to Gallery", ct);
                await IntegrationGalleryExtensions.CreateTeamsAsync(msel, galleryApiClient, blueprintContext, galleryUserIds, ct);
                // create the Gallery Cards
                currentProcessStep = "Gallery - Pushing Cards";
                await SendIntegrationStatusAsync(blueprintContext, msel.Id, "Pushing Cards to Gallery", ct);
                await IntegrationGalleryExtensions.CreateCardsAsync(msel, galleryApiClient, blueprintContext, _clientOptions.CurrentValue.GalleryMaxConcurrentRequests, ct);
                // create the Gallery Articles
                currentProcessStep = "Gallery - Pushing Articles";
                await SendIntegrationStatusAsync(blueprintContext, msel.Id, "Pushing Articles to Gallery", ct);
                await IntegrationGalleryExtensions.CreateArticlesAsync(msel, galleryApiClient, blueprintContext, scenarioEventService, _clientOptions.CurrentValue.GalleryMaxConcurrentRequests, ct);
                currentProcessStep = "Gallery - done";
            }
            catch (System.Exception ex)
            {
                _logger.LogError(ex, $"{currentProcessStep} ({msel.Id})");
                throw;
            }
        }

        private async STT.Task SteamfitterProcess(MselEntity msel, SteamfitterApiClient steamfitterApiClient, BlueprintContext blueprintContext, CancellationToken ct)
        {
            var currentProcessStep = "Steamfitter - create scenario";
            try
            {
                // create the Steamfitter Scenario
                await SendIntegrationStatusAsync(blueprintContext, msel.Id, "Pushing Scenario to Steamfitter", ct);
                var scenario = await IntegrationSteamfitterExtensions.CreateScenarioAsync(msel, steamfitterApiClient, blueprintContext, ct);
                // create the scenario tasks
                var sortedScenarioEvents = msel.ScenarioEvents.OrderBy(m => m.DeltaSeconds).ToList();
                var sortedMoves = msel.Moves.OrderBy(m => m.MoveNumber).ToList();
                var movesAndGroups = GetMovesAndGroups(sortedScenarioEvents, sortedMoves);
                var clientOptions = _clientOptions.CurrentValue;
                var playerApiUrlWithApi = clientOptions.PlayerApiUrl.EndsWith("/") ? clientOptions.PlayerApiUrl + "api/" : clientOptions.PlayerApiUrl + "/api/";
                var citeApiUrlWithApi = clientOptions.CiteApiUrl.EndsWith("/") ? clientOptions.CiteApiUrl + "api/" : clientOptions.CiteApiUrl + "/api/";
                var galleryApiUrlWithApi = clientOptions.GalleryApiUrl.EndsWith("/") ? clientOptions.GalleryApiUrl + "api/" : clientOptions.GalleryApiUrl + "/api/";
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
                            citeApiUrlWithApi,
                            galleryApiUrlWithApi,
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
                            citeApiUrlWithApi,
                            galleryApiUrlWithApi,
                            null,
                            ct);
                    }
                    if (scenarioEvent.IntegrationTarget != null && scenarioEvent.IntegrationTarget.Contains("Steamfitter") && scenarioEvent.SteamfitterTask != null)
                    {
                        currentProcessStep = "Steamfitter - pushing steamfitter task " + scenarioEvent.SteamfitterTaskId.ToString();
                        triggerTask = await IntegrationSteamfitterExtensions.CreateScenarioTasksAsync(
                            msel,
                            scenarioEvent.SteamfitterTask,
                            steamfitterApiClient,
                            moveNumber,
                            groupNumber,
                            playerApiUrlWithApi,
                            citeApiUrlWithApi,
                            galleryApiUrlWithApi,
                            triggerTask,
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
        public bool IsPush { get; set; }
        public Data.Enumerations.MselItemStatus FinalStatus { get; set; }
    }

}
