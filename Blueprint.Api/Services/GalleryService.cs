// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using Blueprint.Api.Infrastructure.Authorization;
using Blueprint.Api.Infrastructure.Exceptions;
using Blueprint.Api.Infrastructure.Extensions;
using Blueprint.Api.Infrastructure.Options;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Logging;
using System;
using System.Net.Http;
using System.Security.Claims;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using Blueprint.Api.Data;
using Blueprint.Api.Data.Models;
using GAC = Gallery.Api.Client;

namespace Blueprint.Api.Services
{
    public interface IGalleryService
    {
        Task<GAC.UnreadArticles> GetMyUnreadArticleCountAsync(Guid exhibitId, CancellationToken ct);
        Task<GAC.UnreadArticles> GetUnreadArticleCountAsync(Guid exhibitId, Guid userId, CancellationToken ct);
    }

    public class GalleryService : IGalleryService
    {
        private readonly ResourceOwnerAuthorizationOptions _resourceOwnerAuthorizationOptions;
        private readonly ClientOptions _clientOptions;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ClaimsPrincipal _user;
        private readonly IAuthorizationService _authorizationService;
        private readonly BlueprintContext _context;
        private readonly ILogger<GalleryService> _logger;

        public GalleryService(
            IHttpClientFactory httpClientFactory,
            ClientOptions clientOptions,
            IPrincipal user,
            BlueprintContext mselContext,
            IAuthorizationService authorizationService,
            ILogger<GalleryService> logger,
            ResourceOwnerAuthorizationOptions resourceOwnerAuthorizationOptions)
        {
            _httpClientFactory = httpClientFactory;
            _clientOptions = clientOptions;
            _resourceOwnerAuthorizationOptions = resourceOwnerAuthorizationOptions;
            _user = user as ClaimsPrincipal;
            _authorizationService = authorizationService;
            _context = mselContext;
            _logger = logger;
        }

        public async Task<GAC.UnreadArticles> GetMyUnreadArticleCountAsync(Guid mselId, CancellationToken ct)
        {
            if (!(await _authorizationService.AuthorizeAsync(_user, null, new BaseUserRequirement())).Succeeded)
                throw new ForbiddenException();

            return await GetUnreadArticleCountAsync(mselId, _user.GetId(), ct);
        }

        public async Task<GAC.UnreadArticles> GetUnreadArticleCountAsync(Guid mselId, Guid userId, CancellationToken ct)
        {
            // get the msel
            var msel = await _context.Msels.FindAsync(mselId);
            if (msel == null)
                throw new EntityNotFoundException<MselEntity>("Msel not found " + mselId.ToString());

            // get the exhibit ID from the msel
            var unreadArticles = new GAC.UnreadArticles();
            var galleryExhibitId = msel.GalleryExhibitId;
            if (galleryExhibitId != null && _clientOptions.GalleryApiUrl != null && _clientOptions.GalleryApiUrl.Length > 0)
            {
                // send request for the unread article count for the exhibit/user
                var client = ApiClientsExtensions.GetHttpClient(_httpClientFactory, _clientOptions.GalleryApiUrl);
                var tokenResponse = await ApiClientsExtensions.RequestTokenAsync(_resourceOwnerAuthorizationOptions, client);
                client.DefaultRequestHeaders.Add("authorization", $"{tokenResponse.TokenType} {tokenResponse.AccessToken}");
                var galleryApiClient = new GAC.GalleryApiClient(client);
                try
                {
                    unreadArticles = await galleryApiClient.GetUnreadCountAsync((Guid)galleryExhibitId, userId);
                }
                catch (System.Exception ex)
                {
                    _logger.LogError(ex, "The Msel (" + msel.Id.ToString() + ") has a Gallery Exhibit ID (" + msel.GalleryExhibitId.ToString() + "), but there was an error with the Gallery API (" + _clientOptions.GalleryApiUrl + ").");
                }
            }

            return unreadArticles;
        }

    }
}

