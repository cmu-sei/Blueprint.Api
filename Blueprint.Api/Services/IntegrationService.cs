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
                        var mselId = _integrationQueue.Take(new CancellationToken());
                        // process on a new thread
                        // When adding a Task to the IntegrationQueue, the UserId MUST be changed to the current UserId, so that all results can be assigned to the correct user
                        var newThread = new Thread(ProcessTheMsel);
                        newThread.Start(mselId);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError("Exception encountered in IntegrationService Run loop.", ex);
                    }
                }
            });
        }

        private async void ProcessTheMsel(Object mselIdObject)
        {
            var ct = new CancellationToken();
            var mselId = (Guid)mselIdObject;
            var currentProcessStep = "Begin processing";
            _logger.LogDebug($"{currentProcessStep} {mselId}");
            try
            {
                using (var scope = _scopeFactory.CreateScope())
                using (var blueprintContext = scope.ServiceProvider.GetRequiredService<BlueprintContext>())
                {
                    var playerTeamDictionary = new Dictionary<Guid, Guid>();
                    currentProcessStep = "Getting the MSEL entity";
                    // get the MSEL and verify data state
                    var msel = await blueprintContext.Msels
                        .Include(m => m.PlayerApplications)
                        .ThenInclude(pa => pa.PlayerApplicationTeams)
                        .AsSplitQuery()
                        .SingleOrDefaultAsync(m => m.Id == mselId);
                    var isAPush = !(
                        msel.PlayerViewId != null ||
                        msel.GalleryExhibitId != null ||
                        msel.CiteEvaluationId != null ||
                        msel.SteamfitterScenarioId != null);
                    try
                    {
                        var tokenResponse = await ApiClientsExtensions.GetToken(scope);
                        currentProcessStep = "Player - get API client";
                        var playerApiClient = IntegrationPlayerExtensions.GetPlayerApiClient(_httpClientFactory, _clientOptions.CurrentValue.PlayerApiUrl, tokenResponse);
                        if (isAPush)
                        {
                            // Player processing part 1
                            currentProcessStep = "Player - create view";
                            playerTeamDictionary = await PlayerProcessPart1(msel, playerApiClient, blueprintContext, ct);



                            // Player processing part 2
                            currentProcessStep = "Player - push applications";
                            await IntegrationPlayerExtensions.CreateApplicationsAsync(msel, playerTeamDictionary, playerApiClient, blueprintContext, ct);
                        }
                        else
                        {
                            currentProcessStep = "Player - pull view";
                            await IntegrationPlayerExtensions.PullFromPlayerAsync(msel, playerApiClient, blueprintContext, ct);
                        }

                    }
                    catch (Exception ex)
                    {
                        _logger.LogError($"{currentProcessStep} {msel.Name} ({msel.Id})", ex);
                    }

                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"{currentProcessStep} {mselId}", ex);
            }
        }

        private bool CanMselBePushed(MselEntity mselToIntegrate)
        {
            // TODO: build this out!!!
            return true;
        }

        private async Task<Dictionary<Guid, Guid>> PlayerProcessPart1(MselEntity msel, PlayerApiClient playerApiClient, BlueprintContext blueprintContext, CancellationToken ct)
        {
            var playerTeamDictionary = new Dictionary<Guid, Guid>();
            var currentProcessStep = "Player create view";
            try
            {
                // create the Player View
                await _hubContext.Clients.Group(msel.Id.ToString()).SendAsync(MainHubMethods.MselPushStatusChange, msel.Id + ",Pushing View to Player", null, ct);
                await IntegrationPlayerExtensions.CreateViewAsync(msel, playerApiClient, blueprintContext, ct);
                // create the Player Teams
                currentProcessStep = "Player create teams";
                await _hubContext.Clients.Group(msel.Id.ToString()).SendAsync(MainHubMethods.MselPushStatusChange, msel.Id + ",Pushing Teams to Player", null, ct);
                playerTeamDictionary = await IntegrationPlayerExtensions.CreateTeamsAsync(msel, playerApiClient, blueprintContext, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError($"{currentProcessStep} {msel.Name} ({msel.Id})", ex);
                throw ex;
            }
            return playerTeamDictionary;
        }

        private async Task CiteProcess()
        {


            // // start a transaction, because we will modify many database items
            // await _context.Database.BeginTransactionAsync();
            // // create the Cite Evaluation
            // await _hubContext.Clients.Group(mselId.ToString()).SendAsync(MainHubMethods.MselPushStatusChange, msel.Id + ",Pushing Evaluation to CITE", null, ct);
            // await IntegrationCiteExtensions.CreateEvaluationAsync(msel, ct);
            // // create the Cite Moves
            // await _hubContext.Clients.Group(mselId.ToString()).SendAsync(MainHubMethods.MselPushStatusChange, msel.Id + ",Pushing Moves to CITE", null, ct);
            // await IntegrationCiteExtensions.CreateMovesAsync(msel, ct);
            // // create the Cite Teams
            // await _hubContext.Clients.Group(mselId.ToString()).SendAsync(MainHubMethods.MselPushStatusChange, msel.Id + ",Pushing Teams to CITE", null, ct);
            // var citeTeamDictionary = await IntegrationCiteExtensions.CreateTeamsAsync(msel, ct);
            // // create the Cite Roles
            // await _hubContext.Clients.Group(mselId.ToString()).SendAsync(MainHubMethods.MselPushStatusChange, msel.Id + ",Pushing Roles to CITE", null, ct);
            // await IntegrationCiteExtensions.CreateRolesAsync(msel, citeTeamDictionary, ct);
            // // create the Cite Actions
            // await _hubContext.Clients.Group(mselId.ToString()).SendAsync(MainHubMethods.MselPushStatusChange, msel.Id + ",Pushing Actions to CITE", null, ct);
            // await IntegrationCiteExtensions.CreateActionsAsync(msel, citeTeamDictionary, ct);
            // // commit the transaction
            // await _hubContext.Clients.Group(mselId.ToString()).SendAsync(MainHubMethods.MselPushStatusChange, msel.Id + ",Commit to CITE", null, ct);
            // await _context.Database.CommitTransactionAsync(ct);









        }

    }

}
