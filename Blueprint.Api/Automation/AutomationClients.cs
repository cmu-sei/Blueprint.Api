// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Blueprint.Api.Infrastructure.Extensions;
using Blueprint.Api.Infrastructure.Options;
using Cite.Api.Client;
using Gallery.Api.Client;
using IdentityModel.Client;
using Microsoft.Extensions.Options;
using Player.Api.Client;
using Player.Vm.Api;
using ClientOptions = Blueprint.Api.Infrastructure.Options.ClientOptions;

namespace Blueprint.Api.Automation
{
    /// <summary>
    /// Blueprint's service account token, kept until shortly before it expires. Automation runs off any request, so
    /// they act as the service account; the token is shared by every run rather than fetched per action.
    /// </summary>
    public interface IServiceAccountTokenProvider
    {
        Task<TokenResponse> GetTokenAsync(CancellationToken ct);
    }

    public class ServiceAccountTokenProvider : IServiceAccountTokenProvider
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IOptionsMonitor<ResourceOwnerAuthorizationOptions> _options;
        private readonly TimeProvider _time;
        private readonly SemaphoreSlim _lock = new(1, 1);
        private TokenResponse _token;
        private DateTimeOffset _refreshAt;

        public ServiceAccountTokenProvider(IHttpClientFactory httpClientFactory, IOptionsMonitor<ResourceOwnerAuthorizationOptions> options, TimeProvider time)
        {
            _httpClientFactory = httpClientFactory;
            _options = options;
            _time = time;
        }

        public async Task<TokenResponse> GetTokenAsync(CancellationToken ct)
        {
            if (_token != null && _time.GetUtcNow() < _refreshAt)
                return _token;
            await _lock.WaitAsync(ct);
            try
            {
                if (_token != null && _time.GetUtcNow() < _refreshAt)
                    return _token;
                using var httpClient = _httpClientFactory.CreateClient();
                var options = _options.CurrentValue;
                var token = await ApiClientsExtensions.RequestTokenAsync(options, httpClient, ct);
                if (token.IsError)
                    throw new AutomationConfigurationException("Blueprint could not get its service account token: " + token.Error);
                // refresh the buffer's length before it expires, or halfway through a token shorter than the buffer
                var lifetime = Math.Max(token.ExpiresIn, 0);
                var buffer = options.TokenExpirationBufferSeconds;
                var keep = buffer > 0 && buffer < lifetime ? lifetime - buffer : lifetime / 2;
                _token = token;
                _refreshAt = _time.GetUtcNow().AddSeconds(keep);
                return token;
            }
            finally
            {
                _lock.Release();
            }
        }
    }

    /// <summary>The clients of the APIs automation actions call, as Blueprint's service account.</summary>
    public interface IAutomationClientFactory
    {
        Task<IPlayerVmApiClient> PlayerVmAsync(CancellationToken ct);
        Task<IPlayerApiClient> PlayerAsync(CancellationToken ct);
        Task<ICiteApiClient> CiteAsync(CancellationToken ct);
        Task<IGalleryApiClient> GalleryAsync(CancellationToken ct);
    }

    public class AutomationClientFactory : IAutomationClientFactory
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IServiceAccountTokenProvider _tokens;
        private readonly IOptionsMonitor<ClientOptions> _clientOptions;

        public AutomationClientFactory(IHttpClientFactory httpClientFactory, IServiceAccountTokenProvider tokens, IOptionsMonitor<ClientOptions> clientOptions)
        {
            _httpClientFactory = httpClientFactory;
            _tokens = tokens;
            _clientOptions = clientOptions;
        }

        public async Task<IPlayerVmApiClient> PlayerVmAsync(CancellationToken ct) =>
            new PlayerVmApiClient(await HttpClientAsync(Required(_clientOptions.CurrentValue.PlayerVmApiUrl, "ClientSettings:PlayerVmApiUrl"), ct));

        public async Task<IPlayerApiClient> PlayerAsync(CancellationToken ct) =>
            new PlayerApiClient(await HttpClientAsync(Required(_clientOptions.CurrentValue.PlayerApiUrl, "ClientSettings:PlayerApiUrl"), ct));

        public async Task<ICiteApiClient> CiteAsync(CancellationToken ct) =>
            new CiteApiClient(await HttpClientAsync(Required(_clientOptions.CurrentValue.CiteApiUrl, "ClientSettings:CiteApiUrl"), ct));

        public async Task<IGalleryApiClient> GalleryAsync(CancellationToken ct) =>
            new GalleryApiClient(await HttpClientAsync(Required(_clientOptions.CurrentValue.GalleryApiUrl, "ClientSettings:GalleryApiUrl"), ct));

        private async Task<HttpClient> HttpClientAsync(string url, CancellationToken ct) =>
            ApiClientsExtensions.GetHttpClient(_httpClientFactory, url, await _tokens.GetTokenAsync(ct));

        private static string Required(string url, string setting) =>
            string.IsNullOrWhiteSpace(url) ? throw new AutomationConfigurationException(setting + " is not set.") : url;
    }
}
