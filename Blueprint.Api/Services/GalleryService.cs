// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using AutoMapper;
using Blueprint.Api.Infrastructure.Authorization;
using Blueprint.Api.Infrastructure.Exceptions;
using Blueprint.Api.Infrastructure.Extensions;
using Blueprint.Api.Infrastructure.Options;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
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
        Task<ViewModels.Msel> PushToGalleryAsync(Guid mselId, CancellationToken ct);
        Task<ViewModels.Msel> PullFromGalleryAsync(Guid mselId, CancellationToken ct);
    }

    public class GalleryService : IGalleryService
    {
        private readonly ResourceOwnerAuthorizationOptions _resourceOwnerAuthorizationOptions;
        private readonly ClientOptions _clientOptions;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ClaimsPrincipal _user;
        private readonly IAuthorizationService _authorizationService;
        private readonly BlueprintContext _context;
        protected readonly IMapper _mapper;
        private readonly ILogger<GalleryService> _logger;

        public GalleryService(
            IHttpClientFactory httpClientFactory,
            ClientOptions clientOptions,
            IPrincipal user,
            BlueprintContext mselContext,
            IMapper mapper,
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
            _mapper = mapper;
            _logger = logger;
        }

        public async Task<ViewModels.Msel> PushToGalleryAsync(Guid mselId, CancellationToken ct)
        {
            // user must be a Content Developer or a MSEL owner
            if (!(await _authorizationService.AuthorizeAsync(_user, null, new ContentDeveloperRequirement())).Succeeded &&
                !(await MselOwnerRequirement.IsMet(_user.GetId(), mselId, _context)))
                throw new ForbiddenException();
            // get the MSEL and verify data state
            var msel = await _context.Msels
                .Include(m => m.Cards)
                .Include(m => m.DataFields)
                .Include(m => m.ScenarioEvents)
                .ThenInclude(se => se.DataValues)
                .SingleOrDefaultAsync(m => m.Id == mselId);
            if (msel == null)
                throw new EntityNotFoundException<MselEntity>($"MSEL {mselId} was not found when attempting to create a collection.");
            if (msel.GalleryCollectionId != null)
                throw new InvalidOperationException($"MSEL {mselId} is already associated to a Gallery Collection.");
            // create the Gallery Api Client
            var client = ApiClientsExtensions.GetHttpClient(_httpClientFactory, _clientOptions.GalleryApiUrl);
            var tokenResponse = await ApiClientsExtensions.RequestTokenAsync(_resourceOwnerAuthorizationOptions, client);
            client.DefaultRequestHeaders.Add("authorization", $"{tokenResponse.TokenType} {tokenResponse.AccessToken}");
            var galleryApiClient = new GAC.GalleryApiClient(client);
            // start a transaction, because we will modify many database items
            await _context.Database.BeginTransactionAsync();
            // create the Gallery Collection
            await CreateCollectionAsync(galleryApiClient, msel, ct);
            // create the Gallery Cards
            await CreateCardsAsync(galleryApiClient, msel, ct);
            // create the Gallery Articles
            await CreateArticlesAsync(galleryApiClient, msel, ct);
            // create the Gallery Exhibit
            await CreateExhibitAsync(galleryApiClient, msel, ct);
            // create the Gallery Teams
            await CreateTeamsAsync(galleryApiClient, msel, ct);
            // commit the transaction
            await _context.Database.CommitTransactionAsync(ct);

            return _mapper.Map<ViewModels.Msel>(msel); 
        }

        public async Task<ViewModels.Msel> PullFromGalleryAsync(Guid mselId, CancellationToken ct)
        {
            // user must be a Content Developer or a MSEL owner
            if (!(await _authorizationService.AuthorizeAsync(_user, null, new ContentDeveloperRequirement())).Succeeded &&
                !(await MselOwnerRequirement.IsMet(_user.GetId(), mselId, _context)))
                throw new ForbiddenException();
            // get the MSEL and verify data state
            var msel = await _context.Msels.FindAsync(mselId);
            if (msel == null)
                throw new EntityNotFoundException<MselEntity>($"MSEL {mselId} was not found when attempting to create a collection.");
            if (msel.GalleryCollectionId == null)
                throw new InvalidOperationException($"MSEL {mselId} is not associated to a Gallery Collection.");
            // create the Gallery Api Client
            var client = ApiClientsExtensions.GetHttpClient(_httpClientFactory, _clientOptions.GalleryApiUrl);
            var tokenResponse = await ApiClientsExtensions.RequestTokenAsync(_resourceOwnerAuthorizationOptions, client);
            client.DefaultRequestHeaders.Add("authorization", $"{tokenResponse.TokenType} {tokenResponse.AccessToken}");
            var galleryApiClient = new GAC.GalleryApiClient(client);
            // delete
            await galleryApiClient.DeleteCollectionAsync((Guid)msel.GalleryCollectionId, ct);
            // update the MSEL
            msel.GalleryCollectionId = null;
            msel.GalleryExhibitId = null;
            // update the MSEL Cards
            var cards = await _context.Cards
                .Where(c => c.MselId == msel.Id)
                .ToListAsync(ct);
            foreach (var card in cards)
            {
                card.GalleryId = null;
            }
            // save the changes
            await _context.SaveChangesAsync(ct);

            return _mapper.Map<ViewModels.Msel>(msel); 
        }

        //
        // Helper methods
        //

        // Create a Gallery Collection for this MSEL
        private async Task CreateCollectionAsync(GAC.GalleryApiClient galleryApiClient, MselEntity msel, CancellationToken ct)
        {
            GAC.Collection newCollection = new GAC.Collection() {
                Name = msel.Name,
                Description = msel.Description
            };
            newCollection = await galleryApiClient.CreateCollectionAsync(newCollection, ct);
            // update the MSEL
            msel.GalleryCollectionId = newCollection.Id;
            await _context.SaveChangesAsync(ct);
        }

        // Create Gallery Cards for this MSEL
        private async Task CreateCardsAsync(GAC.GalleryApiClient galleryApiClient, MselEntity msel, CancellationToken ct)
        {
            foreach (var card in msel.Cards)
            {
                GAC.Card newCard = new GAC.Card() {
                    CollectionId = (Guid)msel.GalleryCollectionId,
                    Name = card.Name,
                    Description = card.Description,
                    Move = card.Move,
                    Inject = card.Inject
                };
                newCard = await galleryApiClient.CreateCardAsync(newCard, ct);
                card.GalleryId = newCard.Id;
                await _context.SaveChangesAsync(ct);
            }
        }

        // Create Gallery Articles for this MSEL
        private async Task CreateArticlesAsync(GAC.GalleryApiClient galleryApiClient, MselEntity msel, CancellationToken ct)
        {
            foreach (var scenarioEvent in msel.ScenarioEvents)
            {
                Int32 move = 0;
                Int32 inject = 0;
                object status = GAC.ItemStatus.Unused;
                object sourceType = GAC.SourceType.News;
                DateTime datePosted;
                bool openInNewTab = false;
                // get the Gallery Article values from the scenario event data values
                var cardIdString = GetArticleValue("CardId", scenarioEvent.DataValues, msel.DataFields);
                Guid? galleryCardId = null;
                if (!String.IsNullOrWhiteSpace(cardIdString))
                {
                    var card = msel.Cards.FirstOrDefault(c => c.Id == Guid.Parse(cardIdString));
                    galleryCardId = card.GalleryId;
                }
                var name = GetArticleValue("Name", scenarioEvent.DataValues, msel.DataFields);
                var description = GetArticleValue("Description", scenarioEvent.DataValues, msel.DataFields);
                Int32.TryParse(GetArticleValue("Move", scenarioEvent.DataValues, msel.DataFields), out move);
                Int32.TryParse(GetArticleValue("Inject", scenarioEvent.DataValues, msel.DataFields), out inject);
                Enum.TryParse(typeof(GAC.ItemStatus), GetArticleValue("Status", scenarioEvent.DataValues, msel.DataFields), true, out status);
                Enum.TryParse(typeof(GAC.SourceType), GetArticleValue("SourceType", scenarioEvent.DataValues, msel.DataFields), true, out sourceType);
                var sourceName = GetArticleValue("SourceName", scenarioEvent.DataValues, msel.DataFields);
                var url = GetArticleValue("Url", scenarioEvent.DataValues, msel.DataFields);
                DateTime.TryParse(GetArticleValue("DatePosted", scenarioEvent.DataValues, msel.DataFields), out datePosted);
                bool.TryParse(GetArticleValue("OpenInNewTab", scenarioEvent.DataValues, msel.DataFields), out openInNewTab);
                // create the article
                GAC.Article newArticle = new GAC.Article() {
                    CollectionId = (Guid)msel.GalleryCollectionId,
                    CardId = galleryCardId,
                    Name = name,
                    Description = description,
                    Move = move,
                    Inject = inject,
                    Status = status == null ? GAC.ItemStatus.Unused : (GAC.ItemStatus)status,
                    SourceType = sourceType == null ? GAC.SourceType.News : (GAC.SourceType)sourceType,
                    SourceName = sourceName,
                    Url = url,
                    DatePosted = datePosted,
                    OpenInNewTab = openInNewTab
                };
                newArticle = await galleryApiClient.CreateArticleAsync(newArticle, ct);
            }
        }

        private string GetArticleValue(string key, ICollection<DataValueEntity> dataValues, ICollection<DataFieldEntity> dataFields)
        {
            var dataField = dataFields.SingleOrDefault(df => df.GalleryArticleParameter == key);
            var dataValue = dataField == null ? null : dataValues.SingleOrDefault(dv => dv.DataFieldId == dataField.Id);
            return dataValue == null ? "" : dataValue.Value;
        }

        // Create a Gallery Exhibit for this MSEL
        private async Task CreateExhibitAsync(GAC.GalleryApiClient galleryApiClient, MselEntity msel, CancellationToken ct)
        {
            GAC.Exhibit newExhibit = new GAC.Exhibit() {
                CollectionId = (Guid)msel.GalleryCollectionId,
                ScenarioId = null,
                CurrentMove = 0,
                CurrentInject = 0,

            };
            newExhibit = await galleryApiClient.CreateExhibitAsync(newExhibit, ct);
            // update the MSEL
            msel.GalleryExhibitId = newExhibit.Id;
            await _context.SaveChangesAsync(ct);
        }

        // Create Gallery Teams for this MSEL
        private async Task CreateTeamsAsync(GAC.GalleryApiClient galleryApiClient, MselEntity msel, CancellationToken ct)
        {
            // get the Gallery teams, Gallery Users, and the Gallery TeamUsers
            var galleryTeams = await galleryApiClient.GetTeamsAsync(ct);
            var exhibitTeams = await galleryApiClient.GetTeamsByExhibitAsync((Guid)msel.GalleryExhibitId, ct);
            var galleryUserIds = (await galleryApiClient.GetUsersAsync(ct)).Select(u => u.Id);
            var galleryTeamUsers = await galleryApiClient.GetTeamUsersAsync(ct);
            // get the teams for this MSEL and loop through them
            var teams = await _context.MselTeams
                .Where(mt => mt.MselId == msel.Id)
                .Select(mt => mt.Team)
                .ToListAsync();
            foreach (var team in teams)
            {
                // if this team doesn't exist in Gallery, create it
                var galleryTeam = galleryTeams.FirstOrDefault(t => t.Id == team.Id);
                if (galleryTeam == null)
                {
                    galleryTeam = new GAC.Team() {
                        Id = team.Id,
                        Name = team.Name,
                        ShortName = team.ShortName
                    };
                    galleryTeam = await galleryApiClient.CreateTeamAsync(galleryTeam, ct);
                }
                // add the gallery team to the gallery exhibit, if necessary
                var exhibitTeam = exhibitTeams.FirstOrDefault(et => et.Id == team.Id);
                if (exhibitTeam == null)
                {
                    var galleryExhibitTeam = new GAC.ExhibitTeam() {
                        TeamId = team.Id,
                        ExhibitId = (Guid)msel.GalleryExhibitId
                    };
                    await galleryApiClient.CreateExhibitTeamAsync(galleryExhibitTeam, ct);
                }
                // get all of the users for this team and loop through them
                var users = await _context.TeamUsers
                    .Where(tu => tu.TeamId == team.Id)
                    .Select(tu => tu.User)
                    .ToListAsync(ct);
                foreach (var user in users)
                {
                    // if this user is not in Gallery, add it
                    if (!galleryUserIds.Contains(user.Id))
                    {
                        var newUser = new GAC.User() {
                            Id = user.Id,
                            Name = user.Name
                        };
                        await galleryApiClient.CreateUserAsync(newUser, ct);
                    }
                    // if there is no Gallery TeamUser, create it
                    if (!galleryTeamUsers.Any(tu => tu.TeamId == galleryTeam.Id && tu.UserId == user.Id))
                    {
                        var teamUser = new GAC.TeamUser() {
                            TeamId = galleryTeam.Id,
                            UserId = user.Id
                        };
                        await galleryApiClient.CreateTeamUserAsync(teamUser, ct);
                    }
                }
            }
        }

    }
}

