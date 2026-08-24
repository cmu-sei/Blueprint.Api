// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Blueprint.Api.ViewModels;
using Cite.Api.Client;
using Gallery.Api.Client;
using Player.Api.Client;
using Steamfitter.Api.Client;

namespace Blueprint.Api.Services
{
    public interface IIntegrationNameService
    {
        Task<MselIntegrationNames> GetAsync(Guid mselId, bool hasSystemPermission, CancellationToken ct);
    }

    /// <summary>
    /// Resolves the display names of a MSEL's integrations by asking each application for the thing
    /// the MSEL is associated with.
    /// </summary>
    /// <remarks>
    /// This is why the lookup belongs here and not in the browser: the sibling APIs only allow their
    /// own UI's origin, so a fetch from the Blueprint UI is blocked by CORS before it is even sent.
    /// Blueprint's API has no such restriction, and it already forwards the caller's bearer token to
    /// each client (see ServiceCollectionExtensions.Add*ApiClient), so a name is only ever returned
    /// to a user who could have read it directly.
    /// </remarks>
    public class IntegrationNameService : IIntegrationNameService
    {
        private readonly IMselService _mselService;
        private readonly IPlayerApiClient _playerApiClient;
        private readonly IGalleryApiClient _galleryApiClient;
        private readonly ICiteApiClient _citeApiClient;
        private readonly ISteamfitterApiClient _steamfitterApiClient;
        private readonly ILogger<IntegrationNameService> _logger;

        public IntegrationNameService(
            IMselService mselService,
            IPlayerApiClient playerApiClient,
            IGalleryApiClient galleryApiClient,
            ICiteApiClient citeApiClient,
            ISteamfitterApiClient steamfitterApiClient,
            ILogger<IntegrationNameService> logger)
        {
            _mselService = mselService;
            _playerApiClient = playerApiClient;
            _galleryApiClient = galleryApiClient;
            _citeApiClient = citeApiClient;
            _steamfitterApiClient = steamfitterApiClient;
            _logger = logger;
        }

        public async Task<MselIntegrationNames> GetAsync(Guid mselId, bool hasSystemPermission, CancellationToken ct)
        {
            // Getting the MSEL through MselService is what authorizes the caller: it throws
            // ForbiddenException for a user who cannot view this MSEL, so the names below cannot be
            // used to read the name of something the caller has no business seeing.
            var msel = await _mselService.GetAsync(mselId, hasSystemPermission, ct);
            var names = new MselIntegrationNames();
            if (msel == null)
                return names;

            if (msel.PlayerViewId.HasValue)
            {
                var id = msel.PlayerViewId.Value;
                names.PlayerViewName = await ResolveAsync("Player view", id,
                    async () => (await _playerApiClient.GetViewAsync(id, ct))?.Name, ct);
            }

            if (msel.GalleryCollectionId.HasValue)
            {
                var id = msel.GalleryCollectionId.Value;
                names.GalleryCollectionName = await ResolveAsync("Gallery collection", id,
                    async () => (await _galleryApiClient.GetCollectionAsync(id, ct))?.Name, ct);
            }

            if (msel.GalleryExhibitId.HasValue)
            {
                var id = msel.GalleryExhibitId.Value;
                names.GalleryExhibitName = await ResolveAsync("Gallery exhibit", id,
                    async () => (await _galleryApiClient.GetExhibitAsync(id, ct))?.Name, ct);
            }

            if (msel.CiteEvaluationId.HasValue)
            {
                // CITE evaluations and scoring models are named by their Description.
                var id = msel.CiteEvaluationId.Value;
                names.CiteEvaluationName = await ResolveAsync("CITE evaluation", id,
                    async () => (await _citeApiClient.GetEvaluationAsync(id, ct))?.Description, ct);
            }

            if (msel.CiteScoringModelId.HasValue)
            {
                var id = msel.CiteScoringModelId.Value;
                names.CiteScoringModelName = await ResolveAsync("CITE scoring model", id,
                    async () => (await _citeApiClient.GetScoringModelAsync(id, ct))?.Description, ct);
            }

            if (msel.SteamfitterScenarioId.HasValue)
            {
                var id = msel.SteamfitterScenarioId.Value;
                names.SteamfitterScenarioName = await ResolveAsync("Steamfitter scenario", id,
                    async () => (await _steamfitterApiClient.GetScenarioAsync(id, ct))?.Name, ct);
            }

            return names;
        }

        /// <summary>
        /// Runs one name lookup, turning any failure into an empty name.
        /// </summary>
        /// <remarks>
        /// One unreachable application must not cost the caller the other five names, and an
        /// association whose target has been deleted out from under the MSEL is a display problem
        /// rather than an error, so a failed lookup is logged and reported as no name at all.
        /// Cancellation is not a failure and is left to propagate.
        /// </remarks>
        private async Task<string> ResolveAsync(string what, Guid id, Func<Task<string>> lookup, CancellationToken ct)
        {
            try
            {
                return await lookup() ?? "";
            }
            // System.Exception is spelled out because both generated clients declare their own.
            catch (System.Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, $"Could not resolve the {what} name for {id}.");
                return "";
            }
        }
    }
}
