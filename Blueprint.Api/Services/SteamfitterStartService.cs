// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Blueprint.Api.Data;
using Blueprint.Api.Data.Enumerations;
using Blueprint.Api.Infrastructure.Extensions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Blueprint.Api.Services
{
    /// <summary>
    /// Starts the Steamfitter Scenario of a pushed MSEL whose Scenario is to start at the MSEL start time,
    /// once that time has come. Steamfitter has no way to start a Scenario at its start date by itself.
    /// </summary>
    /// <remarks>
    /// A push that finishes after the start time starts the Scenario itself; this covers the push that
    /// finishes before it. The Scenarios it has dealt with are remembered only in memory, so after a restart
    /// it reads each one again, once, and leaves alone any that is no longer ready.
    /// </remarks>
    public class SteamfitterStartService : BackgroundService
    {
        private const int DefaultCheckSeconds = 30;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IOptionsMonitor<Infrastructure.Options.ClientOptions> _clientOptions;
        private readonly ILogger<SteamfitterStartService> _logger;
        // the Scenarios started, or found already started or ended, since this service started
        private readonly HashSet<Guid> _handledScenarioIds = new();

        public SteamfitterStartService(
            IServiceScopeFactory scopeFactory,
            IHttpClientFactory httpClientFactory,
            IOptionsMonitor<Infrastructure.Options.ClientOptions> clientOptions,
            ILogger<SteamfitterStartService> logger)
        {
            _scopeFactory = scopeFactory;
            _httpClientFactory = httpClientFactory;
            _clientOptions = clientOptions;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await StartDueScenariosAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "Error starting Steamfitter scenarios at their MSEL start time");
                }

                var checkSeconds = _clientOptions.CurrentValue.SteamfitterStartCheckSeconds;
                await Task.Delay(TimeSpan.FromSeconds(checkSeconds > 0 ? checkSeconds : DefaultCheckSeconds), stoppingToken);
            }
        }

        /// <summary>
        /// Starts every due Scenario that is still ready, and returns how many it started. A Scenario that
        /// cannot be reached or started is tried again on the next call.
        /// </summary>
        public async Task<int> StartDueScenariosAsync(CancellationToken ct)
        {
            using var scope = _scopeFactory.CreateScope();
            var blueprintContext = scope.ServiceProvider.GetRequiredService<BlueprintContext>();
            var now = DateTime.UtcNow;
            var candidates = await blueprintContext.Msels
                .AsNoTracking()
                .Where(m =>
                    m.Status == MselItemStatus.Deployed &&
                    m.UseSteamfitter &&
                    m.SteamfitterScenarioId != null &&
                    m.SteamfitterScenarioStartMode == SteamfitterScenarioStartMode.AtStartTime &&
                    m.StartTime <= now)
                .Select(m => new { m.Id, ScenarioId = (Guid)m.SteamfitterScenarioId, m.StartTime, m.DurationSeconds })
                .ToListAsync(ct);
            // forget a Scenario whose MSEL was pulled or changed, so the set does not grow without end
            _handledScenarioIds.IntersectWith(candidates.Select(c => c.ScenarioId));
            // an exercise that is over is not started; a MSEL with no duration has no end to be over
            var due = candidates
                .Where(c => !_handledScenarioIds.Contains(c.ScenarioId))
                .Where(c => c.DurationSeconds <= 0 || c.StartTime.AddSeconds(c.DurationSeconds) > now)
                .ToList();
            if (due.Count == 0)
                return 0;

            var tokenResponse = await ApiClientsExtensions.GetToken(scope);
            var steamfitterApiClient = IntegrationSteamfitterExtensions.GetSteamfitterApiClient(_httpClientFactory, _clientOptions.CurrentValue.SteamfitterApiUrl, tokenResponse);
            var started = 0;
            foreach (var msel in due)
            {
                try
                {
                    if (await IntegrationSteamfitterExtensions.StartScenarioAsync(msel.ScenarioId, steamfitterApiClient, ct))
                    {
                        started++;
                        _logger.LogInformation($"Started Steamfitter scenario {msel.ScenarioId} at the start time of MSEL {msel.Id}");
                    }
                    _handledScenarioIds.Add(msel.ScenarioId);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, $"Could not start Steamfitter scenario {msel.ScenarioId} of MSEL {msel.Id}; trying again on the next check");
                }
            }

            return started;
        }
    }
}
