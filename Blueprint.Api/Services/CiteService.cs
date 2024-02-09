// Copyright 2023 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using AutoMapper;
using Blueprint.Api.Infrastructure.Options;
using Blueprint.Api.Hubs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using System.Security.Claims;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using Blueprint.Api.Data;
using Cite.Api.Client;
using Microsoft.AspNetCore.SignalR;

namespace Blueprint.Api.Services
{
    public interface ICiteService
    {
        Task<IEnumerable<ScoringModel>> GetScoringModelsAsync(CancellationToken ct);
        Task<IEnumerable<TeamType>> GetTeamTypesAsync(CancellationToken ct);
    }

    public class CiteService : ICiteService
    {
        private readonly ICiteApiClient _citeApiClient;
        private readonly ResourceOwnerAuthorizationOptions _resourceOwnerAuthorizationOptions;
        private readonly ClaimsPrincipal _user;
        private readonly IAuthorizationService _authorizationService;
        private readonly BlueprintContext _context;
        protected readonly IMapper _mapper;
        private readonly ILogger<CiteService> _logger;
        private readonly IMselService _mselService;
        private readonly IHubContext<MainHub> _hubContext;

        public CiteService(
            ICiteApiClient citeApiClient,
            IPrincipal user,
            BlueprintContext mselContext,
            IMapper mapper,
            IAuthorizationService authorizationService,
            ILogger<CiteService> logger,
            ResourceOwnerAuthorizationOptions resourceOwnerAuthorizationOptions,
            IMselService mselService,
            IHubContext<MainHub> hubContext)
        {
            _citeApiClient = citeApiClient;
            _resourceOwnerAuthorizationOptions = resourceOwnerAuthorizationOptions;
            _user = user as ClaimsPrincipal;
            _authorizationService = authorizationService;
            _context = mselContext;
            _mapper = mapper;
            _logger = logger;
            _mselService = mselService;
            _hubContext = hubContext;
        }

        public async Task<IEnumerable<ScoringModel>> GetScoringModelsAsync(CancellationToken ct)
        {
            var scoringModels = new List<ScoringModel>();
            try
            {
                scoringModels = (List<ScoringModel>)await _citeApiClient.GetScoringModelsAsync("", "", false);
            }
            catch (System.Exception)
            {
            }
            return (IEnumerable<ScoringModel>)scoringModels;

        }

        public async Task<IEnumerable<TeamType>> GetTeamTypesAsync(CancellationToken ct)
        {
            var teamTypes = new List<TeamType>();
            try
            {
                teamTypes = (List<TeamType>)await _citeApiClient.GetTeamTypesAsync(ct);
            }
            catch (System.Exception)
            {
            }
            return (IEnumerable<TeamType>)teamTypes;

        }

    }
}

