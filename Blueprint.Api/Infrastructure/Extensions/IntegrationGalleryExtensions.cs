// Copyright 2024 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using IdentityModel.Client;
using Gallery.Api.Client;
using Blueprint.Api.Data;
using Blueprint.Api.Data.Enumerations;
using Blueprint.Api.Data.Models;
using Blueprint.Api.Services;

namespace Blueprint.Api.Infrastructure.Extensions
{
    public static class IntegrationGalleryExtensions
    {
        public static GalleryApiClient GetGalleryApiClient(IHttpClientFactory httpClientFactory, string apiUrl, TokenResponse tokenResponse)
        {
            var client = ApiClientsExtensions.GetHttpClient(httpClientFactory, apiUrl, tokenResponse);
            var apiClient = new GalleryApiClient(client);
            return apiClient;
        }

        public static async Task PullFromGalleryAsync(Guid galleryCollectionId, GalleryApiClient galleryApiClient, CancellationToken ct)
        {
            try
            {
                // delete
                await galleryApiClient.DeleteCollectionAsync(galleryCollectionId, ct);
            }
            catch (System.Exception)
            {
            }
        }

        // Create a Gallery Collection for this MSEL
        public static async Task CreateCollectionAsync(MselEntity msel, GalleryApiClient galleryApiClient, BlueprintContext blueprintContext, CancellationToken ct)
        {
            Collection newCollection = new Collection() {
                Name = msel.Name,
                Description = msel.Description
            };
            newCollection = await galleryApiClient.CreateCollectionAsync(newCollection, ct);
            // update the MSEL
            msel.GalleryCollectionId = newCollection.Id;
            await blueprintContext.SaveChangesAsync(ct);
        }

        // Create a Gallery Exhibit for this MSEL
        public static async Task CreateExhibitAsync(MselEntity msel, GalleryApiClient galleryApiClient, BlueprintContext blueprintContext, CancellationToken ct)
        {
            Exhibit newExhibit = new Exhibit() {
                CollectionId = (Guid)msel.GalleryCollectionId,
                ScenarioId = msel.SteamfitterScenarioId,
                CurrentMove = 0,
                CurrentInject = 0
            };
            newExhibit = await galleryApiClient.CreateExhibitAsync(newExhibit, ct);
            // update the MSEL
            msel.GalleryExhibitId = newExhibit.Id;
            await blueprintContext.SaveChangesAsync(ct);
        }

        // Create Gallery Teams for this MSEL
        public static async Task CreateTeamsAsync(MselEntity msel, GalleryApiClient galleryApiClient, BlueprintContext blueprintContext, HashSet<Guid> galleryUserIds, bool emailEnabled, CancellationToken ct)
        {
            // use eager-loaded teams from the MSEL
            var teams = msel.Teams.ToList();
            foreach (var team in teams)
            {
                var galleryTeamId = Guid.NewGuid();
                // create team in Gallery
                var galleryTeam = new Team() {
                    Id = galleryTeamId,
                    Name = team.Name,
                    ShortName = team.ShortName,
                    ExhibitId = (Guid)msel.GalleryExhibitId,
                    Email = emailEnabled ? team.Email : null
                };
                galleryTeam = await galleryApiClient.CreateTeamAsync(galleryTeam, ct);
                team.GalleryTeamId = galleryTeam.Id;
                // use eager-loaded users from the team
                var users = team.TeamUsers.Select(tu => tu.User).ToList();
                foreach (var user in users)
                {
                    // if this user is not in Gallery, add it
                    if (!galleryUserIds.Contains(user.Id))
                    {
                        var newUser = new User() {
                            Id = user.Id,
                            Name = user.Name
                        };
                        await galleryApiClient.CreateUserAsync(newUser, ct);
                        galleryUserIds.Add(user.Id);
                    }
                    // create Gallery TeamUsers, using eager-loaded role data
                    var isObserverRole = team.UserTeamRoles
                        .Any(umr => umr.UserId == user.Id && umr.Role == TeamRole.Observer);
                    var teamUser = new TeamUser() {
                        TeamId = galleryTeam.Id,
                        UserId = user.Id,
                        IsObserver = isObserverRole
                    };
                    await galleryApiClient.CreateTeamUserAsync(teamUser, ct);
                }
            }
            await blueprintContext.SaveChangesAsync(ct);
        }

        // Create Gallery Cards for this MSEL
        public static async Task CreateCardsAsync(MselEntity msel, GalleryApiClient galleryApiClient, BlueprintContext blueprintContext, int batchSize, CancellationToken ct)
        {
            // pre-load all CardTeams to avoid per-card DB queries
            var cardIds = msel.Cards.Select(c => c.Id).ToList();
            var allCardTeams = await blueprintContext.CardTeams
                .AsNoTracking()
                .Where(cdt => cardIds.Contains(cdt.CardId))
                .Include(cdt => cdt.Team)
                .ToListAsync(ct);

            // Build work items without starting execution
            var cards = msel.Cards.ToList();

            // Process cards in parallel batches
            for (int i = 0; i < cards.Count; i += batchSize)
            {
                var batch = cards.Skip(i).Take(batchSize);
                await Task.WhenAll(batch.Select(async card => {
                    var galleryCard = new Card() {
                        CollectionId = (Guid)msel.GalleryCollectionId,
                        Name = card.Name,
                        Description = card.Description,
                        Move = card.Move,
                        Inject = card.Inject
                    };
                    galleryCard = await galleryApiClient.CreateCardAsync(galleryCard, ct);
                    card.GalleryId = galleryCard.Id;

                    // create the Gallery Team Cards for this card
                    var cardTeams = allCardTeams.Where(cdt => cdt.CardId == card.Id).ToList();
                    var teamCardTasks = cardTeams.Select(cardTeam => {
                        var newTeamCard = new TeamCard() {
                            TeamId = (Guid)cardTeam.Team.GalleryTeamId,
                            CardId = (Guid)card.GalleryId,
                            IsShownOnWall = cardTeam.IsShownOnWall,
                            CanPostArticles = cardTeam.CanPostArticles
                        };
                        return galleryApiClient.CreateTeamCardAsync(newTeamCard, ct);
                    });
                    await Task.WhenAll(teamCardTasks);
                }));
            }

            // batch save all card GalleryId updates
            await blueprintContext.SaveChangesAsync(ct);
        }

        // Create Gallery Articles for this MSEL
        public static async Task CreateArticlesAsync(
            MselEntity msel,
            GalleryApiClient galleryApiClient,
            BlueprintContext blueprintContext,
            IScenarioEventService scenarioEventService,
            int batchSize,
            CancellationToken ct)
        {
            var teams = msel.Teams.ToList();
            var movesAndInjects = await scenarioEventService.GetMovesAndInjects(msel.Id, ct);

            // Build work items without starting execution
            var scenarioEvents = msel.ScenarioEvents
                .Where(scenarioEvent => scenarioEvent.IntegrationTarget.Contains("Gallery"))
                .ToList();

            // Process articles in parallel batches
            for (int i = 0; i < scenarioEvents.Count; i += batchSize)
            {
                var batch = scenarioEvents.Skip(i).Take(batchSize);
                await Task.WhenAll(batch.Select(async scenarioEvent => {
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
                    var move = movesAndInjects[scenarioEvent.Id][0];
                    var inject = movesAndInjects[scenarioEvent.Id][1];
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
                    galleryArticle = await galleryApiClient.CreateArticleAsync(galleryArticle, ct);
                    // create the Gallery Team Articles sequentially to avoid overwhelming the API
                    var toOrgs = GetArticleValue(GalleryArticleParameter.ToOrg.ToString(), scenarioEvent.DataValues, msel.DataFields).Split(",", StringSplitOptions.TrimEntries);
                    foreach (var team in teams.Where(team => toOrgs.Contains("ALL") || toOrgs.Contains(team.ShortName)))
                    {
                        var newArticleTeam = new TeamArticle() {
                            ExhibitId = (Guid)msel.GalleryExhibitId,
                            TeamId = (Guid)team.GalleryTeamId,
                            ArticleId = galleryArticle.Id
                        };
                        await galleryApiClient.CreateTeamArticleAsync(newArticleTeam, ct);
                    }
                }));
            }
        }

        public static string GetArticleValue(string key, ICollection<DataValueEntity> dataValues, ICollection<DataFieldEntity> dataFields)
        {
            var dataField = dataFields.SingleOrDefault(df => df.GalleryArticleParameter == key);
            var dataValue = dataField == null ? null : dataValues.SingleOrDefault(dv => dv.DataFieldId == dataField.Id);
            return dataValue == null ? "" : dataValue.Value;
        }

        // Add User to Gallery Team
        public static async Task AddUserToTeamAsync(Guid userId, Guid teamId, GalleryApiClient galleryApiClient, BlueprintContext blueprintContext, CancellationToken ct)
        {
            // create Gallery TeamUsers
            var galleryTeamUser = new TeamUser(){TeamId = teamId, UserId = userId};
            await galleryApiClient.CreateTeamUserAsync(galleryTeamUser, ct);
        }

    }
}
