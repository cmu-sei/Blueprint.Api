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
using Blueprint.Api.Data.Enumerations;
using Blueprint.Api.Data.Models;
using Gallery.Api.Client;

namespace Blueprint.Api.Services
{
    public interface IGalleryService
    {
        Task<ViewModels.Msel> PushToGalleryAsync(Guid mselId, CancellationToken ct);
        Task<ViewModels.Msel> PullFromGalleryAsync(Guid mselId, CancellationToken ct);
    }

    public class GalleryService : IGalleryService
    {
        private readonly IGalleryApiClient _galleryApiClient;
        private readonly ResourceOwnerAuthorizationOptions _resourceOwnerAuthorizationOptions;
        private readonly ClaimsPrincipal _user;
        private readonly IAuthorizationService _authorizationService;
        private readonly BlueprintContext _context;
        protected readonly IMapper _mapper;
        private readonly ILogger<GalleryService> _logger;
        private readonly string _galleryDelivery = "Gallery";

        public GalleryService(
            IGalleryApiClient galleryApiClient,
            IPrincipal user,
            BlueprintContext mselContext,
            IMapper mapper,
            IAuthorizationService authorizationService,
            ILogger<GalleryService> logger,
            ResourceOwnerAuthorizationOptions resourceOwnerAuthorizationOptions)
        {
            _galleryApiClient = galleryApiClient;
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
                .AsSplitQuery()
                .SingleOrDefaultAsync(m => m.Id == mselId);
            if (msel == null)
                throw new EntityNotFoundException<MselEntity>($"MSEL {mselId} was not found when attempting to create a collection.");
            if (msel.GalleryCollectionId != null)
                throw new InvalidOperationException($"MSEL {mselId} is already associated to a Gallery Collection.");
            // verify that no users are on more than one team
            var userVerificationErrorMessage = await FindDuplicateMselUsersAsync(mselId, ct);
            if (!String.IsNullOrWhiteSpace(userVerificationErrorMessage))
                throw new InvalidOperationException(userVerificationErrorMessage);
            // start a transaction, because we will modify many database items
            await _context.Database.BeginTransactionAsync();
            // create the Gallery Collection
            await CreateCollectionAsync(msel, ct);
            // create the Gallery Exhibit
            await CreateExhibitAsync(msel, ct);
            // create the Gallery Teams
            var galleryTeamDictionary = await CreateTeamsAsync(msel, ct);
            // create the Gallery Cards
            await CreateCardsAsync(msel, galleryTeamDictionary, ct);
            // create the Gallery Articles
            await CreateArticlesAsync(msel, galleryTeamDictionary, ct);
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
                throw new EntityNotFoundException<MselEntity>($"MSEL {mselId} was not found when attempting to remove from Gallery.");
            if (msel.GalleryCollectionId == null)
                throw new InvalidOperationException($"MSEL {mselId} is not associated to a Gallery Collection.");
            // delete
            try
            {
                await _galleryApiClient.DeleteCollectionAsync((Guid)msel.GalleryCollectionId, ct);
            }
            catch (System.Exception)
            {
            }
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
        private async Task CreateCollectionAsync(MselEntity msel, CancellationToken ct)
        {
            Collection newCollection = new Collection() {
                Name = msel.Name,
                Description = msel.Description
            };
            newCollection = await _galleryApiClient.CreateCollectionAsync(newCollection, ct);
            // update the MSEL
            msel.GalleryCollectionId = newCollection.Id;
            await _context.SaveChangesAsync(ct);
        }

        // Create a Gallery Exhibit for this MSEL
        private async Task CreateExhibitAsync(MselEntity msel, CancellationToken ct)
        {
            Exhibit newExhibit = new Exhibit() {
                CollectionId = (Guid)msel.GalleryCollectionId,
                ScenarioId = null,
                CurrentMove = 0,
                CurrentInject = 0
            };
            newExhibit = await _galleryApiClient.CreateExhibitAsync(newExhibit, ct);
            // update the MSEL
            msel.GalleryExhibitId = newExhibit.Id;
            await _context.SaveChangesAsync(ct);
        }

        // Create Gallery Teams for this MSEL
        private async Task<Dictionary<Guid, Guid>> CreateTeamsAsync(MselEntity msel, CancellationToken ct)
        {
            var galleryTeamDictionary = new Dictionary<Guid, Guid>();
            // get the Gallery teams, Gallery Users, and the Gallery TeamUsers
            var galleryUserIds = (await _galleryApiClient.GetUsersAsync(ct)).Select(u => u.Id);
            // get the teams for this MSEL and loop through them
            var teams = await _context.MselTeams
                .Where(mt => mt.MselId == msel.Id)
                .Select(mt => mt.Team)
                .ToListAsync();
            foreach (var team in teams)
            {
                var galleryTeamId = Guid.NewGuid();
                // create team in Gallery
                var galleryTeam = new Team() {
                    Id = galleryTeamId,
                    Name = team.Name,
                    ShortName = team.ShortName,
                    ExhibitId = (Guid)msel.GalleryExhibitId
                };
                galleryTeam = await _galleryApiClient.CreateTeamAsync(galleryTeam, ct);
                galleryTeamDictionary.Add(team.Id, galleryTeam.Id);
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
                        var newUser = new User() {
                            Id = user.Id,
                            Name = user.Name
                        };
                        await _galleryApiClient.CreateUserAsync(newUser, ct);
                    }
                    // create Gallery TeamUsers
                    var teamUser = new TeamUser() {
                        TeamId = galleryTeam.Id,
                        UserId = user.Id
                    };
                    await _galleryApiClient.CreateTeamUserAsync(teamUser, ct);
                }
            }

            return galleryTeamDictionary;
        }

        // Create Gallery Cards for this MSEL
        private async Task CreateCardsAsync(MselEntity msel, Dictionary<Guid, Guid> galleryTeamDictionary, CancellationToken ct)
        {
            foreach (var card in msel.Cards)
            {
                Card galleryCard = new Card() {
                    CollectionId = (Guid)msel.GalleryCollectionId,
                    Name = card.Name,
                    Description = card.Description,
                    Move = card.Move,
                    Inject = card.Inject
                };
                galleryCard = await _galleryApiClient.CreateCardAsync(galleryCard, ct);
                card.GalleryId = galleryCard.Id;
                await _context.SaveChangesAsync(ct);
                // create the Gallery Team Cards
                var cardTeams = await _context.CardTeams
                    .Where(ct => ct.CardId == card.Id)
                    .ToListAsync(ct);
                foreach (var cardTeam in cardTeams)
                {
                    var newTeamCard = new TeamCard() {
                        TeamId = galleryTeamDictionary[cardTeam.TeamId],
                        CardId = (Guid)card.GalleryId,
                        IsShownOnWall = cardTeam.IsShownOnWall,
                        CanPostArticles = cardTeam.CanPostArticles
                    };
                    await _galleryApiClient.CreateTeamCardAsync(newTeamCard, ct);
                }
            }
        }

        // Create Gallery Articles for this MSEL
        private async Task CreateArticlesAsync(MselEntity msel, Dictionary<Guid, Guid> galleryTeamDictionary, CancellationToken ct)
        {
            var mselTeams = await _context.MselTeams
                .Where(mt => mt.MselId == msel.Id)
                .Select(mt => mt.Team)
                .ToListAsync(ct);
            foreach (var scenarioEvent in msel.ScenarioEvents)
            {
                var deliveryMethod = GetArticleValue(GalleryArticleParameter.DeliveryMethod.ToString(), scenarioEvent.DataValues, msel.DataFields);
                if (deliveryMethod.Contains(_galleryDelivery))
                {
                    Int32 move = 0;
                    Int32 inject = 0;
                    object status = Gallery.Api.Client.ItemStatus.Unused;
                    object sourceType = SourceType.News;
                    DateTime datePosted;
                    bool openInNewTab = false;
                    // get the Gallery Article values from the scenario event data values
                    var cardIdString = GetArticleValue(GalleryArticleParameter.CardId.ToString(), scenarioEvent.DataValues, msel.DataFields);
                    Guid cardId;
                    var hasACard = Guid.TryParse(cardIdString, out cardId);
                    Guid? galleryCardId = null;
                    if (hasACard)
                    {
                        var card = msel.Cards.FirstOrDefault(c => c.Id == cardId);
                        galleryCardId = card != null ? card.GalleryId : null;
                    }
                    var name = GetArticleValue(GalleryArticleParameter.Name.ToString(), scenarioEvent.DataValues, msel.DataFields);
                    var summary = GetArticleValue(GalleryArticleParameter.Summary.ToString(), scenarioEvent.DataValues, msel.DataFields);
                    var description = GetArticleValue(GalleryArticleParameter.Description.ToString(), scenarioEvent.DataValues, msel.DataFields);
                    Int32.TryParse(GetArticleValue(GalleryArticleParameter.Move.ToString(), scenarioEvent.DataValues, msel.DataFields), out move);
                    Int32.TryParse(GetArticleValue(GalleryArticleParameter.Inject.ToString(), scenarioEvent.DataValues, msel.DataFields), out inject);
                    Enum.TryParse(typeof(Gallery.Api.Client.ItemStatus), GetArticleValue(GalleryArticleParameter.Status.ToString(), scenarioEvent.DataValues, msel.DataFields), true, out status);
                    Enum.TryParse(typeof(SourceType), GetArticleValue(GalleryArticleParameter.SourceType.ToString(), scenarioEvent.DataValues, msel.DataFields), true, out sourceType);
                    var sourceName = GetArticleValue(GalleryArticleParameter.SourceName.ToString(), scenarioEvent.DataValues, msel.DataFields);
                    var url = GetArticleValue(GalleryArticleParameter.Url.ToString(), scenarioEvent.DataValues, msel.DataFields);
                    DateTime.TryParse(GetArticleValue(GalleryArticleParameter.DatePosted.ToString(), scenarioEvent.DataValues, msel.DataFields), out datePosted);
                    bool.TryParse(GetArticleValue(GalleryArticleParameter.OpenInNewTab.ToString(), scenarioEvent.DataValues, msel.DataFields), out openInNewTab);
                    // create the article
                    Article galleryArticle = new Article() {
                        CollectionId = (Guid)msel.GalleryCollectionId,
                        CardId = galleryCardId,
                        Name = name,
                        Summary = summary,
                        Description = description,
                        Move = move,
                        Inject = inject,
                        Status = status == null ? Gallery.Api.Client.ItemStatus.Unused : (Gallery.Api.Client.ItemStatus)status,
                        SourceType = sourceType == null ? SourceType.News : (SourceType)sourceType,
                        SourceName = sourceName,
                        Url = url,
                        DatePosted = datePosted,
                        OpenInNewTab = openInNewTab
                    };
                    galleryArticle = await _galleryApiClient.CreateArticleAsync(galleryArticle, ct);
                    // create the Gallery Team Articles
                    var toOrgs = GetArticleValue(GalleryArticleParameter.ToOrg.ToString(), scenarioEvent.DataValues, msel.DataFields).Split(",", StringSplitOptions.TrimEntries);
                    var teamIds = mselTeams
                        .Where(t => toOrgs.Contains("ALL") || toOrgs.Contains(t.ShortName))
                        .Select(t => t.Id);
                    foreach (var teamId in teamIds)
                    {
                        var newArticleTeam = new TeamArticle() {
                            ExhibitId = (Guid)msel.GalleryExhibitId,
                            TeamId = galleryTeamDictionary[teamId],
                            ArticleId = galleryArticle.Id
                        };
                        await _galleryApiClient.CreateTeamArticleAsync(newArticleTeam, ct);
                    }
                }
            }
        }

        private string GetArticleValue(string key, ICollection<DataValueEntity> dataValues, ICollection<DataFieldEntity> dataFields)
        {
            var dataField = dataFields.SingleOrDefault(df => df.GalleryArticleParameter == key);
            var dataValue = dataField == null ? null : dataValues.SingleOrDefault(dv => dv.DataFieldId == dataField.Id);
            return dataValue == null ? "" : dataValue.Value;
        }

        private async Task<string> FindDuplicateMselUsersAsync(Guid mselId, CancellationToken ct)
        {
            var duplicateResultList = await _context.MselTeams
                .AsNoTracking()
                .Where(mt => mt.MselId == mselId)
                .SelectMany(mt => mt.Team.TeamUsers)
                .Select(tu => new DuplicateResult {
                    TeamId = tu.TeamId,
                    UserId = tu.UserId,
                    TeamName = tu.Team.ShortName,
                    UserName = tu.User.Name
                })
                .ToListAsync(ct);
            var duplicates = duplicateResultList
                .GroupBy(tu => tu.UserId)
                .Where(x => x.Count() > 1)
                .ToList();
            var explanation = "";
            if (duplicates.Any())
            {
                explanation = "Users can only be on one team.  The following users are on more than one team.  ";
                foreach (var dup in duplicates)
                {
                    var dupTeamUsers = dup.ToList();
                    explanation = explanation + "[" + dupTeamUsers[0].UserName + " is on teams ";
                    foreach (var teamUser in dupTeamUsers)
                    {
                        explanation = explanation + teamUser.TeamName + ", ";
                    }
                    explanation = explanation + "],   ";
                }
            }

            return explanation;
        }

    }

    public class DuplicateResult
    {
        public Guid TeamId { get; set; }
        public Guid UserId { get; set; }
        public string TeamName { get; set; }
        public string UserName { get; set; }
    }
}

