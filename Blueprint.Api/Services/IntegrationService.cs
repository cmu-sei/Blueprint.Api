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
using Caster.Api.Client;
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

                // Caster first, while the Player view still exists. If the destroy fails, stop here
                // and keep every ID, so Remove Integrations can retry instead of orphaning VMs.
                if (msel.CasterWorkspaceId != null)
                {
                    var casterClientSource = new CasterClientSource(_httpClientFactory, _clientOptions.CurrentValue.CasterApiUrl, () => ApiClientsExtensions.GetToken(scope));
                    if (!await TryPullFromCasterAsync(dbContext, mselId, (Guid)msel.CasterWorkspaceId, casterClientSource, "Cancelling - pulling", freshCt))
                        return;
                }

                // Step 3: Get auth token for external API calls. After the Caster destroy, which can outlast a token.
                var tokenResponse = await ApiClientsExtensions.GetToken(scope);
                _logger.LogInformation($"Cancel cleanup: token acquired for MSEL {mselId}. Starting external pulls.");

                // Step 4: Call external API deletes directly (no BlueprintContext/SaveChanges needed).
                if (msel.SteamfitterScenarioId != null)
                {
                    try
                    {
                        await SendIntegrationStatusAsync(dbContext, mselId, "Cancelling - pulling Steamfitter Scenario", freshCt);
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
                        await SendIntegrationStatusAsync(dbContext, mselId, "Cancelling - pulling CITE Evaluation", freshCt);
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
                        await SendIntegrationStatusAsync(dbContext, mselId, "Cancelling - pulling Gallery Collection", freshCt);
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
                        await SendIntegrationStatusAsync(dbContext, mselId, "Cancelling - pulling Player View", freshCt);
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
                    finalMsel.CasterWorkspaceId = null;
                    finalMsel.CasterRunId = null;
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
                        .Include(m => m.UserMselRoles)
                        .AsSplitQuery()
                        .SingleOrDefaultAsync(m => m.Id == integrationInformation.MselId);
                    var isAPush = integrationInformation.IsPush;
                    var casterOnly = integrationInformation.CasterOnly;
                    currentProcessStep = "Try processing the MSEL";
                    try
                    {
                        currentProcessStep = "Getting Auth Token with scope " + scope;
                        var tokenResponse = await ApiClientsExtensions.GetToken(scope);
                        if (isAPush)
                        {
                            // a Caster retry redoes the Caster step alone: every step before it already succeeded
                            if (!casterOnly)
                            {
                                // Pre-generate integration object IDs and save before calling remote APIs
                                // the view is created under the queue item's ID when it has one, so the MSEL has to use it too
                                if (msel.UsePlayer && integrationInformation.PlayerViewId != null)
                                    msel.PlayerViewId = integrationInformation.PlayerViewId;
                                else if (msel.UsePlayer && msel.PlayerViewId == null)
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

                                // Create all API clients upfront
                                PlayerApiClient playerApiClient = null;
                                GalleryApiClient galleryApiClient = null;
                                CiteApiClient citeApiClient = null;
                                SteamfitterApiClient steamfitterApiClient = null;
                                HttpClient playerHttpClient = null;
                                if (msel.UsePlayer)
                                {
                                    playerHttpClient = ApiClientsExtensions.GetHttpClient(_httpClientFactory, _clientOptions.CurrentValue.PlayerApiUrl, tokenResponse);
                                    playerApiClient = new PlayerApiClient(playerHttpClient);
                                }
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
                                    await PlayerProcessPart1(msel, integrationInformation.PlayerViewId, playerApiClient, playerHttpClient, blueprintContext, playerUserIds, ct);
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
                            }
                            else
                            {
                                // RetryCasterAsync claims the retry with ExecuteUpdateAsync, which tells no one else
                                await SendIntegrationStatusAsync(blueprintContext, msel.Id, "Retrying Caster", ct);
                            }

                            // Caster processing. Runs last because the apply can outlast the token the
                            // other clients share; the Caster step fetches its own tokens as it goes.
                            if (msel.UseCaster)
                            {
                                ct.ThrowIfCancellationRequested();
                                currentProcessStep = "Caster - begin processing";
                                var casterClientSource = new CasterClientSource(_httpClientFactory, _clientOptions.CurrentValue.CasterApiUrl, () => ApiClientsExtensions.GetToken(scope));
                                await CasterProcess(msel, integrationInformation.UserId ?? msel.CreatedBy, scope, casterClientSource, blueprintContext, step => currentProcessStep = step, ct);
                            }
                            // Start the Steamfitter Scenario last, so that a Caster retry that finishes the push starts it too
                            if (msel.UseSteamfitter && msel.SteamfitterScenarioId != null &&
                                IntegrationSteamfitterExtensions.StartsWithThePush(msel, DateTime.UtcNow))
                            {
                                ct.ThrowIfCancellationRequested();
                                currentProcessStep = "Steamfitter - start scenario";
                                await StartSteamfitterScenarioAsync(msel, scope, blueprintContext, ct);
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
                            await PullIntegrations(msel, integrationInformation, scope, blueprintContext, tokenResponse, ct);
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
                // only this thread's own: a Caster retry queued as soon as this push failed may already have registered its source
                _cancellationTokenSources.TryRemove(new KeyValuePair<Guid, CancellationTokenSource>(integrationInformation.MselId, cts));
                cts.Dispose();
            }
        }

        /// <summary>
        /// Pulls (deletes) integrations from external services and clears integration IDs.
        /// Uses ExecuteUpdateAsync for all DB writes to avoid the EntityEventInterceptor →
        /// MediatR → SignalR chain that can hang on slow/saturated browser connections.
        /// </summary>
        private async STT.Task PullIntegrations(MselEntity msel, IntegrationInformation integrationInformation, IServiceScope scope, BlueprintContext blueprintContext, IdentityModel.Client.TokenResponse tokenResponse, CancellationToken ct)
        {
            var mselId = msel.Id;
            var currentProcessStep = "Pulling Integrations";
            await SendIntegrationStatusAsync(blueprintContext, mselId, "Pulling Integrations", ct);

            // Pull from Caster first, while the Player view its VMs belong to still exists
            if (msel.CasterWorkspaceId != null)
            {
                var casterClientSource = new CasterClientSource(_httpClientFactory, _clientOptions.CurrentValue.CasterApiUrl, () => ApiClientsExtensions.GetToken(scope));
                if (!await TryPullFromCasterAsync(blueprintContext, mselId, (Guid)msel.CasterWorkspaceId, casterClientSource, "Pulling", ct))
                    return;
                // the destroy can outlast the token the other pulls were given
                tokenResponse = await ApiClientsExtensions.GetToken(scope);
            }

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
                finalMsel.CasterWorkspaceId = null;
                finalMsel.CasterRunId = null;
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

        // Every client here is fetched when it is used, not once up front: on a retry, stopping the runs can
        // wait out an apply for longer than a token lasts. CasterClientSource only gets a new token when it needs one.
        private async STT.Task CasterProcess(MselEntity msel, Guid userId, IServiceScope scope, CasterClientSource casterClientSource, BlueprintContext blueprintContext, Action<string> setStep, CancellationToken ct)
        {
            // the statuses name the step, so a failure reports the step it happened in
            Func<string, STT.Task> sendStatus = status =>
            {
                setStep(status);
                return SendIntegrationStatusAsync(blueprintContext, msel.Id, status, ct);
            };

            // a retry picks up the workspace the failed push made, and terraform plans against what it built
            var workspace = msel.CasterWorkspaceId == null
                ? null
                : await IntegrationCasterExtensions.FindWorkspaceAsync((Guid)msel.CasterWorkspaceId, await casterClientSource.GetAsync(ct), ct);
            var isRetry = workspace != null;
            if (isRetry)
            {
                // a timed-out run can still be going, and Caster refuses a new run until it stops
                await sendStatus("Caster - stopping active runs");
                await IntegrationCasterExtensions.StopActiveRunsAsync(workspace.Id, casterClientSource, _clientOptions.CurrentValue, ct);
            }
            else
            {
                // create the workspace and record it at once, so a failure from here on can be cleaned up.
                // Neither is cancelled: a workspace Caster created but Blueprint never recorded would be orphaned.
                await sendStatus("Caster - creating workspace");
                workspace = await IntegrationCasterExtensions.CreateWorkspaceAsync(msel, await casterClientSource.GetAsync(ct), CancellationToken.None);
                msel.CasterWorkspaceId = workspace.Id;
                msel.CasterRunId = null;
                await SaveCasterIdsAsync(blueprintContext, msel, ct);
            }

            // the variables terraform needs to attach VMs to this view and its teams
            await sendStatus("Caster - writing variables");
            var user = await blueprintContext.Users.FindAsync(new object[] { userId }, ct);
            var teamVariables = await blueprintContext.Teams
                .Where(t => t.MselId == msel.Id && t.CasterTeamVariable != null)
                .ToDictionaryAsync(t => t.Id, t => t.CasterTeamVariable, ct);
            var playerApiClient = IntegrationPlayerExtensions.GetPlayerApiClient(_httpClientFactory, _clientOptions.CurrentValue.PlayerApiUrl, await ApiClientsExtensions.GetToken(scope));
            var varsFileContent = await IntegrationCasterExtensions.GetVarsFileContentAsync((Guid)msel.PlayerViewId, userId, user?.Name, teamVariables, playerApiClient, ct);
            var casterApiClient = await casterClientSource.GetAsync(ct);
            if (isRetry)
                await IntegrationCasterExtensions.ReplaceVarsFileAsync(workspace, varsFileContent, casterApiClient, ct);
            else
                await IntegrationCasterExtensions.CreateVarsFileAsync(workspace, varsFileContent, casterApiClient, ct);

            await sendStatus("Caster - creating run");
            var run = await IntegrationCasterExtensions.CreateRunAsync(workspace.Id, false, await casterClientSource.GetAsync(ct), ct);
            msel.CasterRunId = run.Id;
            await SaveCasterIdsAsync(blueprintContext, msel, ct);

            await IntegrationCasterExtensions.PlanAndApplyAsync(run.Id, casterClientSource, _clientOptions.CurrentValue, _clientOptions.CurrentValue.CasterApplyMaxWaitMinutes, sendStatus, ct);
        }

        /// <summary>
        /// Records the MSEL's Caster ids via ExecuteUpdateAsync (bypasses EF tracking/interceptor), then sends
        /// a lightweight SignalR notification, so an open MSEL page can link to the workspace while the run is
        /// still going, or after it fails. Like SendIntegrationStatusAsync, so a stalled browser connection
        /// cannot hang the push. The write is not cancelled, for the same reason as the workspace itself;
        /// the notification is, since by then the ids are safe.
        /// </summary>
        private async STT.Task SaveCasterIdsAsync(BlueprintContext blueprintContext, MselEntity msel, CancellationToken ct)
        {
            var mselId = msel.Id;
            var workspaceId = msel.CasterWorkspaceId;
            var runId = msel.CasterRunId;
            await blueprintContext.Msels
                .Where(m => m.Id == mselId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(m => m.CasterWorkspaceId, workspaceId)
                    .SetProperty(m => m.CasterRunId, runId),
                    CancellationToken.None);

            await _mainHub.Clients.Group(mselId.ToString())
                .SendAsync(MainHubMethods.CasterIdsUpdated, mselId, workspaceId, runId, ct);
            await _mainHub.Clients.Group(MainHub.ADMIN_DATA_GROUP)
                .SendAsync(MainHubMethods.CasterIdsUpdated, mselId, workspaceId, runId, ct);
        }

        /// <summary>
        /// Destroys and deletes the MSEL's Caster workspace. Unlike the other pulls, a failure is not
        /// swallowed: it is reported and false is returned, so the caller stops before deleting the
        /// Player view and clearing the IDs. Otherwise the VMs would keep running with nothing
        /// pointing at them. Running Remove Integrations again retries.
        /// Every step's status starts with <paramref name="statusPrefix"/>, which the UI reads to tell
        /// a pull or a cancel from a push (and so not offer Cancel Push).
        /// </summary>
        private async STT.Task<bool> TryPullFromCasterAsync(BlueprintContext blueprintContext, Guid mselId, Guid casterWorkspaceId, CasterClientSource casterClientSource, string statusPrefix, CancellationToken ct)
        {
            try
            {
                var step = $"{statusPrefix} Caster Workspace";
                await SendIntegrationStatusAsync(blueprintContext, mselId, step, ct);
                await IntegrationCasterExtensions.DestroyWorkspaceAsync(
                    casterWorkspaceId,
                    casterClientSource,
                    _clientOptions.CurrentValue,
                    status => SendIntegrationStatusAsync(blueprintContext, mselId, $"{step} - {(status.StartsWith("Caster - ") ? status["Caster - ".Length..] : status)}", ct),
                    ct);
                return true;
            }
            catch (System.Exception ex)
            {
                _logger.LogError(ex, $"Caster - pull workspace {casterWorkspaceId} ({mselId})");
                await SendIntegrationStatusAsync(blueprintContext, mselId,
                    $"ERROR: Caster - pull workspace {casterWorkspaceId} failed - {ex.Message} Nothing else was removed. Remove Integrations again to retry, or clean up the workspace in Caster.",
                    new CancellationToken());
                return false;
            }
        }

        private bool CanMselBePushed(MselEntity mselToIntegrate)
        {
            // TODO: build this out!!!
            return true;
        }

        private async STT.Task PlayerProcessPart1(MselEntity msel, Guid? playerViewId, PlayerApiClient playerApiClient, HttpClient playerHttpClient, BlueprintContext blueprintContext, HashSet<Guid> playerUserIds, CancellationToken ct)
        {
            var currentProcessStep = "Player get view admin role";
            try
            {
                // a MSEL Admin team takes the place of Player's own; find its role before there is a view to clean up
                Guid? viewAdminRoleId = null;
                if (msel.Teams.Any(t => IntegrationPlayerExtensions.IsAdminTeam(t.Name)))
                    viewAdminRoleId = await IntegrationPlayerExtensions.GetViewAdminRoleIdAsync(playerHttpClient, ct);
                // create the Player View
                currentProcessStep = "Player create view";
                await SendIntegrationStatusAsync(blueprintContext, msel.Id, "Pushing View to Player", ct);
                await IntegrationPlayerExtensions.CreateViewAsync(msel, playerViewId, playerApiClient, blueprintContext, ct);
                // create the Player Teams
                currentProcessStep = "Player create teams";
                await SendIntegrationStatusAsync(blueprintContext, msel.Id, "Pushing Teams to Player", ct);
                await IntegrationPlayerExtensions.CreateTeamsAsync(msel, playerApiClient, blueprintContext, playerUserIds, viewAdminRoleId, ct);
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
                // create the Cite EvaluationMemberships
                currentProcessStep = "CITE - create evaluation memberships";
                await SendIntegrationStatusAsync(blueprintContext, msel.Id, "Pushing Evaluation Memberships to CITE", ct);
                await IntegrationCiteExtensions.CreateEvaluationMembershipsAsync(msel, citeApiClient, blueprintContext, ct);
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
                // create the Gallery ExhibitMemberships
                currentProcessStep = "Gallery - Pushing Exhibit Memberships";
                await SendIntegrationStatusAsync(blueprintContext, msel.Id, "Pushing Exhibit Memberships to Gallery", ct);
                await IntegrationGalleryExtensions.CreateExhibitMembershipsAsync(msel, galleryApiClient, blueprintContext, ct);
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
                // create the Steamfitter ScenarioMemberships
                currentProcessStep = "Steamfitter - create scenario memberships";
                await SendIntegrationStatusAsync(blueprintContext, msel.Id, "Pushing Scenario Memberships to Steamfitter", ct);
                await IntegrationSteamfitterExtensions.CreateScenarioMembershipsAsync(msel, steamfitterApiClient, blueprintContext, ct);
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

        // A Scenario that cannot be started does not fail the push: everything was created, and a facilitator
        // can still start it in Steamfitter. The MSEL Info page shows it as not started.
        private async STT.Task StartSteamfitterScenarioAsync(MselEntity msel, IServiceScope scope, BlueprintContext blueprintContext, CancellationToken ct)
        {
            await SendIntegrationStatusAsync(blueprintContext, msel.Id, "Starting Scenario in Steamfitter", ct);
            try
            {
                // a fresh token, because the Caster step can outlast the one the push started with
                var tokenResponse = await ApiClientsExtensions.GetToken(scope);
                var steamfitterApiClient = IntegrationSteamfitterExtensions.GetSteamfitterApiClient(_httpClientFactory, _clientOptions.CurrentValue.SteamfitterApiUrl, tokenResponse);
                await IntegrationSteamfitterExtensions.StartScenarioAsync((Guid)msel.SteamfitterScenarioId, steamfitterApiClient, ct);
            }
            catch (System.Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, $"Steamfitter - start scenario failed ({msel.Id}); the push continues");
                await SendIntegrationStatusAsync(blueprintContext, msel.Id, $"Steamfitter Scenario was not started - {ex.Message}", ct);
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
        // the user who pushed or launched the MSEL; Caster passes it to terraform as user_id
        public Guid? UserId { get; set; }
        public Data.Enumerations.MselItemStatus FinalStatus { get; set; }
        // a push that redoes only the Caster step, after the rest of the push succeeded and Caster failed
        public bool CasterOnly { get; set; }
    }

}
