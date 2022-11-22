// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using Blueprint.Api.Infrastructure.Extensions;
using Blueprint.Api.Infrastructure.Options;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using SAC = Steamfitter.Api.Client;

namespace Blueprint.Api.Services
{
    public interface ISteamfitterService
    {
        Task<ICollection<SAC.Result>> CreateAndExecuteTaskAsnyc(SAC.TaskForm taskForm, CancellationToken ct);
    }

    public class SteamfitterService : ISteamfitterService
    {
        private readonly ResourceOwnerAuthorizationOptions _resourceOwnerAuthorizationOptions;
        private readonly ClientOptions _clientOptions;
        private readonly IHttpClientFactory _httpClientFactory;

        public SteamfitterService(
            IHttpClientFactory httpClientFactory,
            ClientOptions clientOptions,
            ResourceOwnerAuthorizationOptions resourceOwnerAuthorizationOptions)
        {
            _httpClientFactory = httpClientFactory;
            _clientOptions = clientOptions;
            _resourceOwnerAuthorizationOptions = resourceOwnerAuthorizationOptions;
        }

        public async Task<ICollection<SAC.Result>> CreateAndExecuteTaskAsnyc(SAC.TaskForm taskForm, CancellationToken ct)
        {
            var client = ApiClientsExtensions.GetHttpClient(_httpClientFactory, _clientOptions.SteamfitterApiUrl);
            var tokenResponse = await ApiClientsExtensions.RequestTokenAsync(_resourceOwnerAuthorizationOptions, client);
            client.DefaultRequestHeaders.Add("authorization", $"{tokenResponse.TokenType} {tokenResponse.AccessToken}");
            var steamfitterApiClient = new SAC.SteamfitterApiClient(client);
            var results = await steamfitterApiClient.CreateAndExecuteTaskAsync(taskForm, ct);

            return results;
        }

    }
}

