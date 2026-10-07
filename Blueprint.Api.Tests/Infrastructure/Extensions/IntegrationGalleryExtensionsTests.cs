// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Blueprint.Api.Data.Enumerations;
using Blueprint.Api.Data.Models;
using Blueprint.Api.Infrastructure.Extensions;
using Blueprint.Api.Services;
using Blueprint.Api.Tests.Support;
using Gallery.Api.Client;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Xunit;

// The generated Gallery client declares an Exception, a User, a Team and a TeamUser of its own. Only
// Exception has to be disambiguated - the rest are the types this file means - but it is worth knowing
// that `catch (Exception)` in a file with Gallery's namespace in scope does not compile.
using Exception = System.Exception;

namespace Blueprint.Api.Tests.Infrastructure.Extensions;

/// <summary><c>IntegrationGalleryExtensions</c> - the twelve calls blueprint makes to gallery.api when a
/// MSEL is pushed, pulled or joined, and the articles it builds out of the timeline's data
/// values.</summary>
public class IntegrationGalleryExtensionsTests(DatabaseFixture fixture) : DatabaseTestBase(fixture)
{
    /// <summary>Starts describing an actor to seed over this test's database.</summary>
    private TestActorBuilder Actor() => new(Db, Ct);

    // ---------------------------------------------------------------------------------------------
    // GetGalleryApiClient and PullFromGalleryAsync
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task GetGalleryApiClient_BuildsAClientCarryingTheTokenAndTheApiUrl()
    {
        var handler = new SiblingApiHandler().Answers($"api/collections/{CollectionId}", HttpStatusCode.NoContent);
        var client = IntegrationGalleryExtensions.GetGalleryApiClient(
            handler.AsFactory(), "http://gallery.example/", await Tokens.Bearer());

        await IntegrationGalleryExtensions.PullFromGalleryAsync(CollectionId, client, Ct);

        var sent = Assert.Single(handler.Sent);

        Assert.Equal($"api/collections/{CollectionId}", sent.Path);
        Assert.Equal(Tokens.Header, sent.Authorization);
    }

    [Fact]
    public async Task PullFromGallery_DeletesTheCollection()
    {
        var handler = new SiblingApiHandler().Answers($"api/collections/{CollectionId}", HttpStatusCode.NoContent);

        await IntegrationGalleryExtensions.PullFromGalleryAsync(CollectionId, Client(handler), Ct);

        var sent = Assert.Single(handler.Sent);

        Assert.Equal(HttpMethod.Delete, sent.Method);
        Assert.Equal($"api/collections/{CollectionId}", sent.Path);
    }

    /// <summary>A refused collection delete is swallowed by an empty <c>catch</c>.</summary>
    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task PullFromGallery_SwallowsARefusal(HttpStatusCode status)
    {
        var handler = new SiblingApiHandler().Answers($"api/collections/{CollectionId}", status);

        await IntegrationGalleryExtensions.PullFromGalleryAsync(CollectionId, Client(handler), Ct);

        Assert.Single(handler.Sent);
    }

    [Fact]
    public async Task PullFromGallery_SwallowsAnUnreachableGallery()
    {
        var handler = new SiblingApiHandler().Throws($"api/collections/{CollectionId}");

        await IntegrationGalleryExtensions.PullFromGalleryAsync(CollectionId, Client(handler), Ct);

        Assert.Single(handler.Sent);
    }

    // ---------------------------------------------------------------------------------------------
    // CreateCollectionAsync and CreateExhibitAsync
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task CreateCollection_PostsTheMselsIdNameAndDescription()
    {
        var msel = Msel();
        var handler = new SiblingApiHandler().AnswersJson("api/collections", CollectionJson, HttpStatusCode.Created);

        await IntegrationGalleryExtensions.CreateCollectionAsync(msel, Client(handler), null, Ct);

        var sent = Assert.Single(handler.Sent);

        Assert.Equal(HttpMethod.Post, sent.Method);
        Assert.Equal("api/collections", sent.Path);

        var body = Body(sent.Body);

        Assert.Equal(CollectionId.ToString(), body["id"].GetString());
        Assert.Equal(msel.Name, body["name"].GetString());
        Assert.Equal(msel.Description, body["description"].GetString());
    }

    [Fact]
    public async Task CreateCollection_WithNoCollectionIdOnTheMsel_Throws()
    {
        var msel = Msel();
        msel.GalleryCollectionId = null;

        var handler = new SiblingApiHandler().AnswersJson("api/collections", CollectionJson, HttpStatusCode.Created);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            IntegrationGalleryExtensions.CreateCollectionAsync(msel, Client(handler), null, Ct));

        Assert.Empty(handler.Sent);
    }

    [Fact]
    public async Task CreateExhibit_PostsTheExhibitUnderTheCollectionAndTheScenario()
    {
        var msel = Msel();
        var handler = new SiblingApiHandler().AnswersJson("api/exhibits", ExhibitJson, HttpStatusCode.Created);

        await IntegrationGalleryExtensions.CreateExhibitAsync(msel, Client(handler), null, Ct);

        var sent = Assert.Single(handler.Sent);

        Assert.Equal("api/exhibits", sent.Path);

        var body = Body(sent.Body);

        Assert.Equal(ExhibitId.ToString(), body["id"].GetString());
        Assert.Equal(CollectionId.ToString(), body["collectionId"].GetString());
        Assert.Equal(ScenarioId.ToString(), body["scenarioId"].GetString());
    }

    /// <summary>Create exhibit starts at move zero and sends no name or description.</summary>
    [Fact]
    public async Task CreateExhibit_StartsAtMoveZeroAndSendsNoNameOrDescription()
    {
        var handler = new SiblingApiHandler().AnswersJson("api/exhibits", ExhibitJson, HttpStatusCode.Created);

        await IntegrationGalleryExtensions.CreateExhibitAsync(Msel(), Client(handler), null, Ct);

        var body = Body(Assert.Single(handler.Sent).Body);

        Assert.Equal(0, body["currentMove"].GetInt32());
        Assert.Equal(0, body["currentInject"].GetInt32());
        Assert.Equal(JsonValueKind.Null, body["name"].ValueKind);
        Assert.Equal(JsonValueKind.Null, body["description"].ValueKind);
    }

    [Fact]
    public async Task CreateExhibit_WithNoExhibitIdOnTheMsel_Throws()
    {
        var msel = Msel();
        msel.GalleryExhibitId = null;

        var handler = new SiblingApiHandler().AnswersJson("api/exhibits", ExhibitJson, HttpStatusCode.Created);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            IntegrationGalleryExtensions.CreateExhibitAsync(msel, Client(handler), null, Ct));
    }

    // ---------------------------------------------------------------------------------------------
    // CreateTeamsAsync
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task CreateTeams_CreatesEachTeamUnderTheExhibit()
    {
        var msel = await SeedMsel();
        var team = TestData.Team(msel.Id);
        team.Email = "team@example.test";
        await Seed(team);

        var handler = Teams();

        await IntegrationGalleryExtensions.CreateTeamsAsync(
            await Reload(msel.Id), Client(handler), Db, [], Ct);

        var sent = Assert.Single(handler.Sent);

        Assert.Equal("api/teams", sent.Path);

        var body = Body(sent.Body);

        Assert.Equal(team.Id.ToString(), body["id"].GetString());
        Assert.Equal(team.Name, body["name"].GetString());
        Assert.Equal(team.ShortName, body["shortName"].GetString());
        Assert.Equal("team@example.test", body["email"].GetString());
        Assert.Equal(ExhibitId.ToString(), body["exhibitId"].GetString());
    }

    [Fact]
    public async Task CreateTeams_CreatesEachTeamsUsersAndTeamUsers()
    {
        var msel = await SeedMsel();
        var team = TestData.Team(msel.Id);
        await Seed(team);

        var actor = await Actor().WithName("Ada").OnTeam(team).SeedAsync();

        var handler = Teams();

        await IntegrationGalleryExtensions.CreateTeamsAsync(
            await Reload(msel.Id), Client(handler), Db, [], Ct);

        Assert.Equal(["api/teams", "api/users", "api/teamusers"], handler.Paths);
        Assert.Equal("Ada", Body(handler.Sent[1].Body)["name"].GetString());
        Assert.Equal(actor.Id.ToString(), Body(handler.Sent[2].Body)["userId"].GetString());
    }

    /// <remarks>
    /// The team-user is created against the id <em>Gallery answered with</em>, not the one blueprint asked
    /// for - so a Gallery that renumbers the team still gets consistent memberships. This is the one place
    /// in the integration layer that reads an id back out of a response and uses it, and it is right.
    /// </remarks>
    [Fact]
    public async Task CreateTeams_UsesTheTeamIdGalleryAnsweredWith()
    {
        var msel = await SeedMsel();
        var team = TestData.Team(msel.Id);
        await Seed(team);
        await Actor().OnTeam(team).SeedAsync();

        var galleryChose = Guid.Parse("44444444-4444-4444-4444-444444444444");
        var handler = new SiblingApiHandler()
            .AnswersJson("api/teams", $$"""{"id":"{{galleryChose}}","name":"team"}""", HttpStatusCode.Created)
            .AnswersJson("api/users", UserJson, HttpStatusCode.Created)
            .AnswersJson("api/teamusers", TeamUserJson, HttpStatusCode.Created);

        await IntegrationGalleryExtensions.CreateTeamsAsync(
            await Reload(msel.Id), Client(handler), Db, [], Ct);

        Assert.Equal(galleryChose.ToString(), Body(handler.Sent[^1].Body)["teamId"].GetString());
        Assert.NotEqual(team.Id, galleryChose);
    }

    [Fact]
    public async Task CreateTeams_SkipsCreatingAUserGalleryAlreadyHas()
    {
        var msel = await SeedMsel();
        var team = TestData.Team(msel.Id);
        await Seed(team);

        var actor = await Actor().OnTeam(team).SeedAsync();

        var handler = Teams();

        await IntegrationGalleryExtensions.CreateTeamsAsync(
            await Reload(msel.Id), Client(handler), Db, [actor.Id], Ct);

        Assert.Equal(["api/teams", "api/teamusers"], handler.Paths);
    }

    /// <remarks>
    /// The set is the push's memory of which users Gallery already knows about, and it is written to as well
    /// as read - so a user on two teams is created once. Same shape as the Player push, and here the outer
    /// loop over teams is sequential, so unlike Player's there is no unsynchronized concurrent write to it.
    /// </remarks>
    [Fact]
    public async Task CreateTeams_RecordsACreatedUserSoTheNextTeamDoesNotCreateThemAgain()
    {
        var msel = await SeedMsel();
        var first = TestData.Team(msel.Id);
        var second = TestData.Team(msel.Id);
        await Seed(first, second);

        await Actor().OnTeam(first).OnTeam(second).SeedAsync();

        var handler = Teams();
        var seen = new HashSet<Guid>();

        await IntegrationGalleryExtensions.CreateTeamsAsync(
            await Reload(msel.Id), Client(handler), Db, seen, Ct);

        Assert.Single(handler.Paths.Where(x => x == "api/users").ToList());
        Assert.Single(seen);
    }

    /// <summary>A user whose <c>GalleryExhibitRole</c> is exactly <c>Observer</c> is created as an
    /// observer.</summary>
    [Fact]
    public async Task CreateTeams_MarksAnObserverAsOne()
    {
        var msel = await SeedMsel();
        var team = TestData.Team(msel.Id);
        await Seed(team);

        var actor = await Actor().OnTeam(team).SeedAsync();
        await Seed(RoleFor(msel.Id, actor.Id, "Observer"));

        var handler = Teams();

        await IntegrationGalleryExtensions.CreateTeamsAsync(
            await Reload(msel.Id), Client(handler), Db, [], Ct);

        Assert.True(Body(handler.Sent[^1].Body)["isObserver"].GetBoolean());
    }

    [Theory]
    [InlineData("observer")]
    [InlineData("OBSERVER")]
    [InlineData("Viewer")]
    [InlineData(null)]
    public async Task CreateTeams_IsObserverIsCaseSensitive(string role)
    {
        var msel = await SeedMsel();
        var team = TestData.Team(msel.Id);
        await Seed(team);

        var actor = await Actor().OnTeam(team).SeedAsync();
        await Seed(RoleFor(msel.Id, actor.Id, role));

        var handler = Teams();

        await IntegrationGalleryExtensions.CreateTeamsAsync(
            await Reload(msel.Id), Client(handler), Db, [], Ct);

        Assert.False(Body(handler.Sent[^1].Body)["isObserver"].GetBoolean());
    }

    /// <summary>For a user with two roles, one is consulted and one team-user is created.</summary>
    [Fact]
    public async Task CreateTeams_ForAUserWithTwoRoles_ConsultsAnArbitraryOne()
    {
        var msel = await SeedMsel();
        var team = TestData.Team(msel.Id);
        await Seed(team);

        var actor = await Actor().OnTeam(team).SeedAsync();
        await Seed(
            RoleFor(msel.Id, actor.Id, "Observer", MselRole.Viewer),
            RoleFor(msel.Id, actor.Id, "Participant", MselRole.Editor));

        var handler = Teams();

        await IntegrationGalleryExtensions.CreateTeamsAsync(
            await Reload(msel.Id), Client(handler), Db, [], Ct);

        Assert.Single(handler.Paths.Where(x => x == "api/teamusers").ToList());
    }

    /// <summary>The method saves its context although it changed nothing.</summary>
    [Fact]
    public async Task CreateTeams_SavesAContextItNeverChanged()
    {
        var msel = await SeedMsel();
        var team = TestData.Team(msel.Id);
        await Seed(team);

        var handler = Teams();
        var before = await ReadBack(rb => rb.Teams.CountAsync(Ct));

        await IntegrationGalleryExtensions.CreateTeamsAsync(
            await Reload(msel.Id), Client(handler), Db, [], Ct);

        await using var after = NewContext();

        Assert.Equal(before, await after.Teams.CountAsync(Ct));
        Assert.Equal(team.Name, (await after.Teams.SingleAsync(x => x.Id == team.Id, Ct)).Name);
    }

    // ---------------------------------------------------------------------------------------------
    // CreateExhibitMembershipsAsync
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task CreateExhibitMemberships_ReadsTheRoleListThenPostsOneMembershipPerUser()
    {
        var msel = Msel();
        var ada = Guid.NewGuid();
        var grace = Guid.NewGuid();
        msel.UserMselRoles.Add(RoleFor(msel.Id, ada, "Observer"));
        msel.UserMselRoles.Add(RoleFor(msel.Id, grace, "Participant"));

        var handler = Memberships();

        await IntegrationGalleryExtensions.CreateExhibitMembershipsAsync(msel, Client(handler), null, Ct);

        Assert.Equal("api/exhibit-roles", handler.Sent[0].Path);
        Assert.Equal($"api/exhibits/{ExhibitId}/memberships", handler.Sent[1].Path);
        Assert.Equal(ExhibitId.ToString(), Body(handler.Sent[1].Body)["exhibitId"].GetString());

        var posted = handler.Sent
            .Skip(1)
            .Select(x => Body(x.Body))
            .ToDictionary(x => x["userId"].GetString(), x => x["roleId"].GetString());

        Assert.Equal(ObserverRoleId.ToString(), posted[ada.ToString()]);
        Assert.Equal(ParticipantRoleId.ToString(), posted[grace.ToString()]);
    }

    [Fact]
    public async Task CreateExhibitMemberships_SkipsARoleWithNoGalleryName()
    {
        var msel = Msel();
        msel.UserMselRoles.Add(RoleFor(msel.Id, Guid.NewGuid(), null));
        msel.UserMselRoles.Add(RoleFor(msel.Id, Guid.NewGuid(), string.Empty));

        var handler = Memberships();

        await IntegrationGalleryExtensions.CreateExhibitMembershipsAsync(msel, Client(handler), null, Ct);

        Assert.Equal("api/exhibit-roles", Assert.Single(handler.Paths));
    }

    [Fact]
    public async Task CreateExhibitMemberships_SkipsARoleNameGalleryDoesNotHave()
    {
        var msel = Msel();
        msel.UserMselRoles.Add(RoleFor(msel.Id, Guid.NewGuid(), "NoSuchRole"));

        var handler = Memberships();

        await IntegrationGalleryExtensions.CreateExhibitMembershipsAsync(msel, Client(handler), null, Ct);

        Assert.Equal("api/exhibit-roles", Assert.Single(handler.Paths));
    }

    [Fact]
    public async Task CreateExhibitMemberships_ForAUserWithTwoRoles_PostsOneMembership()
    {
        var msel = Msel();
        var ada = Guid.NewGuid();
        msel.UserMselRoles.Add(RoleFor(msel.Id, ada, "Observer", MselRole.Viewer));
        msel.UserMselRoles.Add(RoleFor(msel.Id, ada, "Participant", MselRole.Editor));

        var handler = Memberships();

        await IntegrationGalleryExtensions.CreateExhibitMembershipsAsync(msel, Client(handler), null, Ct);

        var membership = Body(Assert.Single(handler.Sent.Skip(1).ToList()).Body);

        Assert.Equal(ada.ToString(), membership["userId"].GetString());
        Assert.Contains(
            membership["roleId"].GetString(),
            new[] { ObserverRoleId.ToString(), ParticipantRoleId.ToString() });
    }

    [Fact]
    public async Task CreateExhibitMemberships_SwallowsAFailedMembershipAndCarriesOn()
    {
        var msel = Msel();
        msel.UserMselRoles.Add(RoleFor(msel.Id, Guid.NewGuid(), "Observer"));
        msel.UserMselRoles.Add(RoleFor(msel.Id, Guid.NewGuid(), "Participant"));

        var handler = new SiblingApiHandler()
            .AnswersJson("api/exhibit-roles", RolesJson)
            .Answers($"api/exhibits/{ExhibitId}/memberships", HttpStatusCode.Conflict);

        await IntegrationGalleryExtensions.CreateExhibitMembershipsAsync(msel, Client(handler), null, Ct);

        Assert.Equal(2, handler.Sent.Count(x => x.Path.EndsWith("memberships")));
    }

    // ---------------------------------------------------------------------------------------------
    // CreateCardsAsync
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task CreateCards_PostsEachCardIntoTheCollection()
    {
        var msel = await SeedMsel();
        await Seed(Card(msel.Id, "Front page", move: 2, inject: 3));

        var handler = Cards();

        await CreateCards(msel.Id, handler);

        var sent = Assert.Single(handler.Sent);

        Assert.Equal("api/cards", sent.Path);

        var body = Body(sent.Body);

        Assert.Equal(CollectionId.ToString(), body["collectionId"].GetString());
        Assert.Equal("Front page", body["name"].GetString());
        Assert.Equal("Seeded by IntegrationGalleryExtensionsTests", body["description"].GetString());
        Assert.Equal(2, body["move"].GetInt32());
        Assert.Equal(3, body["inject"].GetInt32());
    }

    /// <remarks>
    /// The one write-back in the file, and the reason its <c>SaveChangesAsync</c> exists: the blueprint card
    /// remembers the id Gallery gave it, so a later article can point at it.
    /// </remarks>
    [Fact]
    public async Task CreateCards_RecordsGallerysCardIdOnTheBlueprintRow()
    {
        var msel = await SeedMsel();
        var card = Card(msel.Id);
        await Seed(card);

        var galleryChose = Guid.Parse("55555555-5555-5555-5555-555555555555");
        var handler = new SiblingApiHandler()
            .AnswersJson("api/cards", $$"""{"id":"{{galleryChose}}","name":"card"}""", HttpStatusCode.Created);

        await CreateCards(msel.Id, handler);

        await using var after = NewContext();

        Assert.Equal(galleryChose, (await after.Cards.SingleAsync(x => x.Id == card.Id, Ct)).GalleryId);
    }

    [Fact]
    public async Task CreateCards_CreatesATeamCardPerTeamCarryingItsFlags()
    {
        var msel = await SeedMsel();
        var first = TestData.Team(msel.Id);
        var second = TestData.Team(msel.Id);
        await Seed(first, second);

        var card = Card(msel.Id);
        await Seed(card);
        await Seed(
            new CardTeamEntity(card.Id, first.Id) { IsShownOnWall = true, CanPostArticles = false },
            new CardTeamEntity(card.Id, second.Id) { IsShownOnWall = false, CanPostArticles = true });

        var handler = Cards();

        await CreateCards(msel.Id, handler);

        var teamCards = handler.Sent
            .Where(x => x.Path == "api/teamcards")
            .Select(x => Body(x.Body))
            .ToDictionary(x => x["teamId"].GetString(), x => x);

        Assert.True(teamCards[first.Id.ToString()]["isShownOnWall"].GetBoolean());
        Assert.False(teamCards[first.Id.ToString()]["canPostArticles"].GetBoolean());
        Assert.False(teamCards[second.Id.ToString()]["isShownOnWall"].GetBoolean());
        Assert.True(teamCards[second.Id.ToString()]["canPostArticles"].GetBoolean());
        Assert.Equal(CardId.ToString(), teamCards[first.Id.ToString()]["cardId"].GetString());
    }

    /// <summary>Cards are posted <c>batchSize</c> at a time.</summary>
    [Fact]
    public async Task CreateCards_HonoursTheBatchSize()
    {
        var msel = await SeedMsel();

        for (var i = 0; i < 4; i++)
        {
            await Seed(Card(msel.Id, $"card-{i}"));
        }

        var handler = new SiblingApiHandler().Holds()
            .AnswersJson("api/cards", CardJson, HttpStatusCode.Created);

        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        bounded.CancelAfter(TimeSpan.FromSeconds(2));

        var loaded = await Reload(msel.Id);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            IntegrationGalleryExtensions.CreateCardsAsync(
                loaded, Client(handler), Db, batchSize: 2, bounded.Token));

        Assert.Equal(2, handler.MaxInFlight);
    }

    [Fact]
    public async Task CreateCards_WithNoCards_SendsNothing()
    {
        var msel = await SeedMsel();
        var handler = Cards();

        await CreateCards(msel.Id, handler);

        Assert.Empty(handler.Sent);
    }

    /// <summary>Create cards when gallery answers without an id uses the all zeros guid.</summary>
    [Fact]
    public async Task CreateCards_WhenGalleryAnswersWithoutAnId_UsesTheAllZerosGuid()
    {
        var msel = await SeedMsel();
        var team = TestData.Team(msel.Id);
        await Seed(team);

        var card = Card(msel.Id);
        await Seed(card);
        await Seed(new CardTeamEntity(card.Id, team.Id));

        var handler = new SiblingApiHandler()
            .AnswersJson("api/cards", """{"name":"card"}""", HttpStatusCode.Created)
            .AnswersJson("api/teamcards", """{"id":"00000000-0000-0000-0000-000000000001"}""", HttpStatusCode.Created);

        await CreateCards(msel.Id, handler);

        var teamCard = Body(handler.Sent.First(x => x.Path == "api/teamcards").Body);

        Assert.Equal(Guid.Empty.ToString(), teamCard["cardId"].GetString());

        await using var after = NewContext();

        Assert.Equal(Guid.Empty, (await after.Cards.SingleAsync(x => x.Id == card.Id, Ct)).GalleryId);
    }

    // ---------------------------------------------------------------------------------------------
    // CreateArticlesAsync
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task CreateArticles_BuildsAnArticleFromTheScenarioEventsDataValues()
    {
        var msel = await SeedMsel();
        var fields = await SeedArticleFields(msel.Id);
        var scenarioEvent = await SeedArticleEvent(msel.Id, fields, new()
        {
            [GalleryArticleParameter.Name] = "Power out",
            [GalleryArticleParameter.Summary] = "Lights off",
            [GalleryArticleParameter.Description] = "The grid is down",
            [GalleryArticleParameter.SourceName] = "Wire service",
            [GalleryArticleParameter.Url] = "http://news.example/1",
            [GalleryArticleParameter.Status] = "Critical",
            [GalleryArticleParameter.SourceType] = "Social",
            [GalleryArticleParameter.DatePosted] = "2026-03-04T05:06:07Z",
            [GalleryArticleParameter.OpenInNewTab] = "true"
        });

        var handler = Articles();

        await CreateArticles(msel.Id, handler, moves: new() { [scenarioEvent.Id] = [4, 5] });

        var body = Body(Assert.Single(handler.Sent).Body);

        Assert.Equal(CollectionId.ToString(), body["collectionId"].GetString());
        Assert.Equal("Power out", body["name"].GetString());
        Assert.Equal("Lights off", body["summary"].GetString());
        Assert.Equal("The grid is down", body["description"].GetString());
        Assert.Equal("Wire service", body["sourceName"].GetString());
        Assert.Equal("http://news.example/1", body["url"].GetString());
        Assert.Equal("Critical", body["status"].GetString());
        Assert.Equal("Social", body["sourceType"].GetString());
        Assert.True(body["openInNewTab"].GetBoolean());
        Assert.Equal(4, body["move"].GetInt32());
        Assert.Equal(5, body["inject"].GetInt32());
    }

    /// <remarks>
    /// Only events whose <c>IntegrationTarget</c> mentions Gallery, by substring - so a target of
    /// <c>"Gallery,Steamfitter"</c> matches both integrations, which is how one event drives two.
    /// </remarks>
    [Fact]
    public async Task CreateArticles_IgnoresAnEventNotTargetedAtGallery()
    {
        var msel = await SeedMsel();
        var fields = await SeedArticleFields(msel.Id);

        var targeted = await SeedArticleEvent(msel.Id, fields, new() { [GalleryArticleParameter.Name] = "in" });
        var other = await SeedArticleEvent(
            msel.Id, fields, new() { [GalleryArticleParameter.Name] = "out" }, target: "Steamfitter");

        var handler = Articles();

        await CreateArticles(msel.Id, handler, moves: new()
        {
            [targeted.Id] = [0, 0],
            [other.Id] = [0, 0]
        });

        Assert.Equal("in", Body(Assert.Single(handler.Sent).Body)["name"].GetString());
    }

    /// <remarks>
    /// Case-insensitively - <c>Enum.TryParse</c> is called with <c>ignoreCase: true</c> - so the cell may
    /// say <c>critical</c> or <c>CRITICAL</c>. This is the one parse in the article builder that forgives
    /// anything.
    /// </remarks>
    [Theory]
    [InlineData("critical", "Critical")]
    [InlineData("CRITICAL", "Critical")]
    [InlineData("Open", "Open")]
    public async Task CreateArticles_ParsesTheStatusCaseInsensitively(string cell, string expected)
    {
        var body = await OneArticle(new() { [GalleryArticleParameter.Status] = cell });

        Assert.Equal(expected, body["status"].GetString());
    }

    /// <summary>Create articles for a status gallery does not know publishes it unused.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("nonsense")]
    public async Task CreateArticles_ForAStatusGalleryDoesNotKnow_PublishesItUnused(string cell)
    {
        var body = await OneArticle(new() { [GalleryArticleParameter.Status] = cell });

        Assert.Equal("Unused", body["status"].GetString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("nonsense")]
    public async Task CreateArticles_ForASourceTypeGalleryDoesNotKnow_PublishesItAsNews(string cell)
    {
        var body = await OneArticle(new() { [GalleryArticleParameter.SourceType] = cell });

        Assert.Equal("News", body["sourceType"].GetString());
    }

    /// <summary>Create articles for a date it cannot parse publishes it dated year one.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("last Tuesday")]
    public async Task CreateArticles_ForADateItCannotParse_PublishesItDatedYearOne(string cell)
    {
        var body = await OneArticle(new() { [GalleryArticleParameter.DatePosted] = cell });

        Assert.Equal(
            DateTime.MinValue,
            body["datePosted"].GetDateTimeOffset().UtcDateTime);
    }

    [Fact]
    public async Task CreateArticles_ResolvesTheCardIdThroughTheMselsCards()
    {
        var msel = await SeedMsel();
        var card = Card(msel.Id);
        card.GalleryId = Guid.Parse("66666666-6666-6666-6666-666666666666");
        await Seed(card);

        var body = await OneArticle(
            new() { [GalleryArticleParameter.CardId] = card.Id.ToString() }, mselId: msel.Id);

        Assert.Equal(card.GalleryId.ToString(), body["cardId"].GetString());
    }

    /// <summary>A card id that resolves to nothing, or to a card never pushed, sends the article with no
    /// card.</summary>
    [Fact]
    public async Task CreateArticles_ForACardIdThatResolvesToNothing_SendsNoCard()
    {
        var body = await OneArticle(new() { [GalleryArticleParameter.CardId] = Guid.NewGuid().ToString() });

        Assert.Equal(JsonValueKind.Null, body["cardId"].ValueKind);
    }

    [Fact]
    public async Task CreateArticles_ForACardIdThatIsNotAGuid_SendsNoCard()
    {
        var body = await OneArticle(new() { [GalleryArticleParameter.CardId] = "not a guid" });

        Assert.Equal(JsonValueKind.Null, body["cardId"].ValueKind);
    }

    [Fact]
    public async Task CreateArticles_ForToOrgAll_GivesTheArticleToEveryTeam()
    {
        var msel = await SeedMsel();
        var first = TestData.Team(msel.Id);
        var second = TestData.Team(msel.Id);
        await Seed(first, second);

        var fields = await SeedArticleFields(msel.Id);
        var scenarioEvent = await SeedArticleEvent(
            msel.Id, fields, new() { [GalleryArticleParameter.ToOrg] = "ALL" });

        var handler = Articles();

        await CreateArticles(msel.Id, handler, moves: new() { [scenarioEvent.Id] = [0, 0] });

        var teams = handler.Sent
            .Where(x => x.Path == "api/teamarticles")
            .Select(x => Body(x.Body)["teamId"].GetString())
            .ToList();

        Assert.Equal(2, teams.Count);
        Assert.Contains(first.Id.ToString(), teams);
        Assert.Contains(second.Id.ToString(), teams);
    }

    [Fact]
    public async Task CreateArticles_ForToOrgNamingOneTeam_GivesItToThatTeamOnly()
    {
        var msel = await SeedMsel();
        var wanted = TestData.Team(msel.Id);
        wanted.ShortName = "RED";
        var other = TestData.Team(msel.Id);
        other.ShortName = "BLUE";
        await Seed(wanted, other);

        var fields = await SeedArticleFields(msel.Id);
        var scenarioEvent = await SeedArticleEvent(
            msel.Id, fields, new() { [GalleryArticleParameter.ToOrg] = "RED, GREEN" });

        var handler = Articles();

        await CreateArticles(msel.Id, handler, moves: new() { [scenarioEvent.Id] = [0, 0] });

        var teamArticle = Assert.Single(handler.Sent.Where(x => x.Path == "api/teamarticles").ToList());

        Assert.Equal(wanted.Id.ToString(), Body(teamArticle.Body)["teamId"].GetString());
        Assert.Equal(ExhibitId.ToString(), Body(teamArticle.Body)["exhibitId"].GetString());
    }

    /// <summary>Create articles with no to org creates the article and gives it to no team.</summary>
    [Fact]
    public async Task CreateArticles_WithNoToOrg_CreatesTheArticleAndGivesItToNoTeam()
    {
        var msel = await SeedMsel();
        await Seed(TestData.Team(msel.Id));

        var fields = await SeedArticleFields(msel.Id);
        var scenarioEvent = await SeedArticleEvent(
            msel.Id, fields, new() { [GalleryArticleParameter.Name] = "unseen" });

        var handler = Articles();

        await CreateArticles(msel.Id, handler, moves: new() { [scenarioEvent.Id] = [0, 0] });

        Assert.Equal("api/articles", Assert.Single(handler.Paths));
    }

    /// <summary>An event the move-and-inject service did not answer for throws
    /// <c>KeyNotFoundException</c>.</summary>
    [Fact]
    public async Task CreateArticles_ForAnEventTheServiceDidNotAnswerFor_Throws()
    {
        var msel = await SeedMsel();
        var fields = await SeedArticleFields(msel.Id);
        await SeedArticleEvent(msel.Id, fields, new() { [GalleryArticleParameter.Name] = "orphan" });

        var handler = Articles();

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            CreateArticles(msel.Id, handler, moves: []));
    }

    [Fact]
    public async Task CreateArticles_HonoursTheBatchSize()
    {
        var msel = await SeedMsel();
        var fields = await SeedArticleFields(msel.Id);
        var moves = new Dictionary<Guid, int[]>();

        for (var i = 0; i < 4; i++)
        {
            var scenarioEvent = await SeedArticleEvent(
                msel.Id, fields, new() { [GalleryArticleParameter.Name] = $"article-{i}" });
            moves[scenarioEvent.Id] = [0, 0];
        }

        var handler = new SiblingApiHandler().Holds()
            .AnswersJson("api/articles", ArticleJson, HttpStatusCode.Created);

        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        bounded.CancelAfter(TimeSpan.FromSeconds(2));

        var loaded = await Reload(msel.Id);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            IntegrationGalleryExtensions.CreateArticlesAsync(
                loaded, Client(handler), Db, ScenarioEvents(moves), batchSize: 2, bounded.Token));

        Assert.Equal(2, handler.MaxInFlight);
    }

    // ---------------------------------------------------------------------------------------------
    // GetArticleValue
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void GetArticleValue_ReturnsTheValueOfTheFieldMappedToTheParameter()
    {
        var field = TestData.DataField();
        field.GalleryArticleParameter = "Name";

        var value = TestData.DataValue(field.Id, Guid.NewGuid(), "Power out");

        Assert.Equal(
            "Power out",
            IntegrationGalleryExtensions.GetArticleValue("Name", [value], [field]));
    }

    /// <summary>No field mapped to the parameter, or no value for it, gives the empty string.</summary>
    [Fact]
    public void GetArticleValue_WithNoFieldMappedToTheParameter_IsEmpty()
    {
        var field = TestData.DataField();
        field.GalleryArticleParameter = "Summary";

        Assert.Equal(
            string.Empty,
            IntegrationGalleryExtensions.GetArticleValue("Name", [], [field]));
    }

    [Fact]
    public void GetArticleValue_WithNoValueForTheField_IsEmpty()
    {
        var field = TestData.DataField();
        field.GalleryArticleParameter = "Name";

        Assert.Equal(
            string.Empty,
            IntegrationGalleryExtensions.GetArticleValue("Name", [], [field]));
    }

    /// <summary>Get article value for two fields mapped to one parameter throws.</summary>
    [Fact]
    public void GetArticleValue_ForTwoFieldsMappedToOneParameter_Throws()
    {
        var first = TestData.DataField();
        first.GalleryArticleParameter = "Name";
        var second = TestData.DataField();
        second.GalleryArticleParameter = "Name";

        Assert.Throws<InvalidOperationException>(() =>
            IntegrationGalleryExtensions.GetArticleValue("Name", [], [first, second]));
    }

    /// <summary>Two values of one field on one event throw.</summary>
    [Fact]
    public void GetArticleValue_ForTwoValuesOfOneField_Throws()
    {
        var field = TestData.DataField();
        field.GalleryArticleParameter = "Name";

        var scenarioEventId = Guid.NewGuid();
        var first = TestData.DataValue(field.Id, scenarioEventId, "one");
        var second = TestData.DataValue(field.Id, scenarioEventId, "two");

        Assert.Throws<InvalidOperationException>(() =>
            IntegrationGalleryExtensions.GetArticleValue("Name", [first, second], [field]));
    }

    // ---------------------------------------------------------------------------------------------
    // AddUserToTeamAsync
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task AddUserToTeam_CreatesTheUserThenAddsThem()
    {
        var msel = await SeedMsel();
        var team = TestData.Team(msel.Id);
        await Seed(team);

        var actor = await Actor().WithName("Grace").SeedAsync();

        var handler = new SiblingApiHandler()
            .AnswersJson("api/users", UserJson, HttpStatusCode.Created)
            .AnswersJson("api/teamusers", TeamUserJson, HttpStatusCode.Created);

        await IntegrationGalleryExtensions.AddUserToTeamAsync(actor.Id, team.Id, Client(handler), Db, Ct);

        Assert.Equal(["api/users", "api/teamusers"], handler.Paths);
        Assert.Equal("Grace", Body(handler.Sent[0].Body)["name"].GetString());

        var teamUser = Body(handler.Sent[1].Body);

        Assert.Equal(team.Id.ToString(), teamUser["teamId"].GetString());
        Assert.Equal(actor.Id.ToString(), teamUser["userId"].GetString());
    }

    /// <summary>Add user to team never marks an observer.</summary>
    [Fact]
    public async Task AddUserToTeam_NeverMarksAnObserver()
    {
        var msel = await SeedMsel();
        var team = TestData.Team(msel.Id);
        await Seed(team);

        var actor = await Actor().SeedAsync();
        await Seed(RoleFor(msel.Id, actor.Id, "Observer"));

        var handler = new SiblingApiHandler()
            .AnswersJson("api/users", UserJson, HttpStatusCode.Created)
            .AnswersJson("api/teamusers", TeamUserJson, HttpStatusCode.Created);

        await IntegrationGalleryExtensions.AddUserToTeamAsync(actor.Id, team.Id, Client(handler), Db, Ct);

        Assert.False(Body(handler.Sent[^1].Body)["isObserver"].GetBoolean());
    }

    /// <summary>A user blueprint does not know is added to the team without being created.</summary>
    [Fact]
    public async Task AddUserToTeam_ForSomeoneBlueprintDoesNotKnow_AddsThemWithoutCreatingThem()
    {
        var msel = await SeedMsel();
        var team = TestData.Team(msel.Id);
        await Seed(team);

        var stranger = Guid.NewGuid();
        var handler = new SiblingApiHandler().AnswersJson("api/teamusers", TeamUserJson, HttpStatusCode.Created);

        await IntegrationGalleryExtensions.AddUserToTeamAsync(stranger, team.Id, Client(handler), Db, Ct);

        Assert.Equal("api/teamusers", Assert.Single(handler.Paths));
    }

    [Fact]
    public async Task AddUserToTeam_SwallowsAFailureCreatingTheUser()
    {
        var msel = await SeedMsel();
        var team = TestData.Team(msel.Id);
        await Seed(team);

        var actor = await Actor().SeedAsync();

        var handler = new SiblingApiHandler()
            .Answers("api/users", HttpStatusCode.Conflict)
            .AnswersJson("api/teamusers", TeamUserJson, HttpStatusCode.Created);

        await IntegrationGalleryExtensions.AddUserToTeamAsync(actor.Id, team.Id, Client(handler), Db, Ct);

        Assert.Equal(["api/users", "api/teamusers"], handler.Paths);
    }

    /// <summary>A failure adding to the team escapes; a failure creating the user is swallowed.</summary>
    [Fact]
    public async Task AddUserToTeam_DoesNotSwallowAFailureAddingToTheTeam()
    {
        var msel = await SeedMsel();
        var team = TestData.Team(msel.Id);
        await Seed(team);

        var actor = await Actor().SeedAsync();

        var handler = new SiblingApiHandler()
            .AnswersJson("api/users", UserJson, HttpStatusCode.Created)
            .Answers("api/teamusers", HttpStatusCode.Conflict);

        await Assert.ThrowsAnyAsync<ApiException>(() =>
            IntegrationGalleryExtensions.AddUserToTeamAsync(actor.Id, team.Id, Client(handler), Db, Ct));
    }

    // ---------------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------------

    private static readonly Guid CollectionId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid ExhibitId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid ScenarioId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid CardId = Guid.Parse("77777777-7777-7777-7777-777777777777");
    private static readonly Guid ObserverRoleId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid ParticipantRoleId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000002");

    private static readonly string CollectionJson = $$"""{"id":"{{CollectionId}}","name":"collection"}""";
    private static readonly string ExhibitJson = $$"""{"id":"{{ExhibitId}}","collectionId":"{{CollectionId}}"}""";
    private static readonly string TeamJson = $$"""{"id":"{{ExhibitId}}","name":"team"}""";
    private static readonly string UserJson = $$"""{"id":"{{ExhibitId}}","name":"user"}""";
    private static readonly string TeamUserJson =
        $$"""{"id":"{{ExhibitId}}","teamId":"{{ExhibitId}}","userId":"{{ExhibitId}}"}""";
    private static readonly string CardJson = $$"""{"id":"{{CardId}}","name":"card"}""";
    private static readonly string ArticleJson = $$"""{"id":"{{CardId}}","name":"article"}""";

    private static GalleryApiClient Client(SiblingApiHandler handler) =>
        new(ApiClientsExtensions.GetHttpClient(handler.AsFactory(), "http://gallery.example/", null));

    private static SiblingApiHandler Teams() =>
        new SiblingApiHandler()
            .AnswersJson("api/teams", TeamJson, HttpStatusCode.Created)
            .AnswersJson("api/users", UserJson, HttpStatusCode.Created)
            .AnswersJson("api/teamusers", TeamUserJson, HttpStatusCode.Created);

    private static SiblingApiHandler Memberships() =>
        new SiblingApiHandler()
            .AnswersJson("api/exhibit-roles", RolesJson)
            .AnswersJson($"api/exhibits/{ExhibitId}/memberships", "{}", HttpStatusCode.Created);

    private static SiblingApiHandler Cards() =>
        new SiblingApiHandler()
            .AnswersJson("api/cards", CardJson, HttpStatusCode.Created)
            .AnswersJson("api/teamcards", """{"id":"00000000-0000-0000-0000-000000000001"}""", HttpStatusCode.Created);

    private static SiblingApiHandler Articles() =>
        new SiblingApiHandler()
            .AnswersJson("api/articles", ArticleJson, HttpStatusCode.Created)
            .AnswersJson("api/teamarticles", """{"id":"00000000-0000-0000-0000-000000000002"}""", HttpStatusCode.Created);

    private static readonly string RolesJson =
        $$"""[{"id":"{{ObserverRoleId}}","name":"Observer"},{"id":"{{ParticipantRoleId}}","name":"Participant"}]""";

    /// <summary>A MSEL pushed as far as having a Gallery collection, an exhibit and a Steamfitter scenario.</summary>
    private static MselEntity Msel()
    {
        var msel = TestData.Msel();

        msel.GalleryCollectionId = CollectionId;
        msel.GalleryExhibitId = ExhibitId;
        msel.SteamfitterScenarioId = ScenarioId;

        return msel;
    }

    private async Task<MselEntity> SeedMsel()
    {
        var msel = Msel();
        await Seed(msel);

        return msel;
    }

    private static CardEntity Card(Guid mselId, string name = null, int move = 0, int inject = 0) => new()
    {
        Id = Guid.NewGuid(),
        MselId = mselId,
        Name = name ?? "card",
        Description = "Seeded by IntegrationGalleryExtensionsTests",
        Move = move,
        Inject = inject,
        CreatedBy = Guid.NewGuid()
    };

    private static UserMselRoleEntity RoleFor(
        Guid mselId, Guid userId, string galleryRole, MselRole role = MselRole.Viewer) => new()
    {
        Id = Guid.NewGuid(),
        MselId = mselId,
        UserId = userId,
        Role = role,
        GalleryExhibitRole = galleryRole,
        CreatedBy = Guid.NewGuid()
    };

    /// <summary>
    /// The MSEL as the push loads it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Loaded through <see cref="DatabaseTestBase.Db"/> rather than a fresh context, and that is load-bearing
    /// for two of these methods: <c>CreateCardsAsync</c> writes each card's <c>GalleryId</c> onto the entity
    /// and then calls <c>SaveChangesAsync</c> on the context it was handed, so the entity has to be tracked
    /// by that same context or the write goes nowhere. Production does exactly this - one context loads the
    /// MSEL and later saves it. A test that reads through a second context tests a write-back that cannot
    /// work.
    /// </para>
    /// <para>
    /// <c>AsSplitQuery</c> is required rather than an optimization: blueprint configures
    /// <c>MultipleCollectionIncludeWarning</c> to throw, which <c>IntegrationService.cs:286</c> works around
    /// with the same call.
    /// </para>
    /// </remarks>
    private async Task<MselEntity> Reload(Guid mselId)
    {
        return await Db.Msels
            .Include(m => m.Teams)
                .ThenInclude(t => t.TeamUsers)
                    .ThenInclude(tu => tu.User)
            .Include(m => m.UserMselRoles)
            .Include(m => m.Cards)
            .Include(m => m.DataFields)
            .Include(m => m.ScenarioEvents)
                .ThenInclude(se => se.DataValues)
            .AsSplitQuery()
            .FirstAsync(m => m.Id == mselId, Ct);
    }

    /// <summary>One data field per Gallery article parameter, mapped by name as the push expects.</summary>
    private async Task<Dictionary<GalleryArticleParameter, DataFieldEntity>> SeedArticleFields(Guid mselId)
    {
        var fields = new Dictionary<GalleryArticleParameter, DataFieldEntity>();

        foreach (GalleryArticleParameter parameter in Enum.GetValues<GalleryArticleParameter>())
        {
            var field = TestData.DataField(mselId: mselId, name: parameter.ToString());
            field.GalleryArticleParameter = parameter.ToString();
            fields[parameter] = field;
            await Seed(field);
        }

        return fields;
    }

    private async Task<ScenarioEventEntity> SeedArticleEvent(
        Guid mselId,
        Dictionary<GalleryArticleParameter, DataFieldEntity> fields,
        Dictionary<GalleryArticleParameter, string> values,
        string target = "Gallery")
    {
        var scenarioEvent = TestData.ScenarioEvent(mselId);
        scenarioEvent.IntegrationTarget = target;
        await Seed(scenarioEvent);

        foreach (var (parameter, value) in values)
        {
            await Seed(TestData.DataValue(fields[parameter].Id, scenarioEvent.Id, value));
        }

        return scenarioEvent;
    }

    /// <summary>
    /// The move and inject numbers <c>CreateArticlesAsync</c> asks <c>IScenarioEventService</c> for. A
    /// substitute rather than the real service: what is under test is what the article does with the answer.
    /// </summary>
    private static IScenarioEventService ScenarioEvents(Dictionary<Guid, int[]> moves)
    {
        var service = Substitute.For<IScenarioEventService>();
        service.GetMovesAndInjects(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(moves);

        return service;
    }

    private async Task CreateCards(Guid mselId, SiblingApiHandler handler, int batchSize = 10) =>
        await IntegrationGalleryExtensions.CreateCardsAsync(
            await Reload(mselId), Client(handler), Db, batchSize, Ct);

    private async Task CreateArticles(
        Guid mselId, SiblingApiHandler handler, Dictionary<Guid, int[]> moves, int batchSize = 10) =>
        await IntegrationGalleryExtensions.CreateArticlesAsync(
            await Reload(mselId), Client(handler), Db, ScenarioEvents(moves), batchSize, Ct);

    /// <summary>
    /// The body of the one article a single scenario event produces, for the tests that vary one cell.
    /// </summary>
    private async Task<Dictionary<string, JsonElement>> OneArticle(
        Dictionary<GalleryArticleParameter, string> values, Guid? mselId = null)
    {
        var msel = mselId is null ? (await SeedMsel()).Id : mselId.Value;
        var fields = await SeedArticleFields(msel);
        var scenarioEvent = await SeedArticleEvent(msel, fields, values);

        var handler = Articles();

        await CreateArticles(msel, handler, moves: new() { [scenarioEvent.Id] = [0, 0] });

        return Body(handler.Sent.First(x => x.Path == "api/articles").Body);
    }

    private static Dictionary<string, JsonElement> Body(string body) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(body);
}
