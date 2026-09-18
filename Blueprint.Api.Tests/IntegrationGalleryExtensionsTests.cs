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
using Blueprint.Api.Tests.Infrastructure;
using Gallery.Api.Client;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Xunit;

// The generated Gallery client declares an Exception, a User, a Team and a TeamUser of its own. Only
// Exception has to be disambiguated - the rest are the types this file means - but it is worth knowing
// that `catch (Exception)` in a file with Gallery's namespace in scope does not compile.
using Exception = System.Exception;

namespace Blueprint.Api.Tests;

/// <summary>
/// <c>IntegrationGalleryExtensions</c> - the twelve calls blueprint makes to gallery.api when a MSEL is
/// pushed, pulled or joined, and the articles it builds out of the timeline's data values.
/// </summary>
/// <remarks>
/// <para>
/// The database is here for the three methods that read or write it; the rest take a
/// <c>BlueprintContext</c> they never touch and are given <c>null</c>.
/// <see cref="TestHttpHandler"/> stands in for the socket while the generated <c>GalleryApiClient</c> and
/// every route it builds run for real.
/// </para>
/// <para>
/// The wire contract: <c>DELETE api/collections/{id}</c> answering 204, and <c>POST</c> answering 201 to
/// <c>api/collections</c>, <c>api/exhibits</c>, <c>api/teams</c>, <c>api/users</c>, <c>api/teamusers</c>,
/// <c>api/exhibits/{id}/memberships</c>, <c>api/cards</c>, <c>api/teamcards</c>, <c>api/articles</c> and
/// <c>api/teamarticles</c>, plus <c>GET api/exhibit-roles</c> answering 200. Gallery's DTOs all carry
/// audit fields and blueprint sets none of them, so every object it creates arrives with
/// <c>createdBy</c> as the all-zeros guid and <c>dateCreated</c> as year one; Gallery presumably stamps
/// its own.
/// </para>
/// <para>
/// <strong>This file batches correctly, and that is the finding.</strong> <c>CreateCardsAsync</c> and
/// <c>CreateArticlesAsync</c> put the <c>Select(async ...)</c> <em>inside</em> the <c>for</c> loop, so each
/// slice of <c>batchSize</c> is started and awaited before the next begins - which is exactly what
/// <c>IntegrationPlayerExtensions.CreateApplicationsAsync</c> gets wrong by materializing the whole
/// sequence first. Same author, same intent, same comment about batches, opposite outcome. See
/// <see cref="CreateCards_HonoursTheBatchSize"/>, and
/// <c>IntegrationPlayerExtensionsTests.CreateApplications_IgnoresTheBatchSizeAndStartsEveryApplicationAtOnce</c>
/// for the contrast.
/// </para>
/// <para>
/// <strong><c>GetArticleValue</c> uses <c>SingleOrDefault</c> twice</strong>, so two data fields mapped to
/// the same Gallery parameter, or two values for one field, are an <c>InvalidOperationException</c> in the
/// middle of a push. Nothing constrains either: <c>GalleryArticleParameter</c> is free text on the field,
/// and the data-value unique index includes the always-null <c>inject_id</c>, which is why it forbids
/// nothing. See <see cref="GetArticleValue_ForTwoFieldsMappedToOneParameter_Throws"/>.
/// </para>
/// <para>
/// <strong>An article whose <c>ToOrg</c> is empty reaches no team at all.</strong> The value is split on
/// commas and matched against <c>"ALL"</c> or a team's <c>ShortName</c>; an empty string matches neither,
/// so the article is created in the collection and shown to nobody. Whether that is intended is a question
/// about Gallery, but nothing says so and nothing warns. See
/// <see cref="CreateArticles_WithNoToOrg_CreatesTheArticleAndGivesItToNoTeam"/>.
/// </para>
/// <para>
/// <strong>Every parse in the article builder fails quietly to a default.</strong> An unparseable status
/// becomes <c>Unused</c> and an unparseable date becomes <c>0001-01-01</c> - so an article whose
/// <c>DatePosted</c> cell is empty or misspelled is published dated year one, and one whose <c>Status</c>
/// cell says something Gallery does not know is published unused. The <c>status == null</c> guards are
/// necessary rather than defensive: <c>Enum.TryParse</c> assigns <em>null</em> to its <c>object</c> out
/// parameter when it fails, overwriting the initializer.
/// </para>
/// <para>
/// <strong><c>IsObserver</c> turns on a case-sensitive comparison</strong> against
/// <c>UserMselRole.GalleryExhibitRole == "Observer"</c>, a free-text column - so <c>"observer"</c> makes an
/// ordinary member. And the role is chosen with <c>FirstOrDefault</c> over a user's roles, so a user
/// holding two gets an arbitrary one, the same shape as the deduplication in
/// <c>CreateExhibitMembershipsAsync</c> two methods down.
/// </para>
/// <para>
/// <strong><c>CreateTeamsAsync</c> ends with a <c>SaveChangesAsync</c> that has nothing to save.</strong>
/// It creates Gallery objects and modifies no blueprint entity. <c>CreateCardsAsync</c>'s save is the real
/// one - it writes each card's <c>GalleryId</c> back - which is presumably where this one was copied from.
/// </para>
/// <para>
/// <c>CreateExhibitMembershipsAsync</c> is a near-exact copy of
/// <c>IntegrationSteamfitterExtensions.CreateScenarioMembershipsAsync</c>: same deduplication, same silent
/// skip of a role name the sibling API does not know, same per-membership swallow. The tests are the same
/// tests.
/// </para>
/// <para>
/// Per this branch's rule, every test above characterizes rather than fixes, and says what fixing it will do
/// to the test.
/// </para>
/// </remarks>
public class IntegrationGalleryExtensionsTests(DatabaseFixture fixture) : DatabaseTestBase(fixture)
{
    // ---------------------------------------------------------------------------------------------
    // GetGalleryApiClient and PullFromGalleryAsync
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task GetGalleryApiClient_BuildsAClientCarryingTheTokenAndTheApiUrl()
    {
        var handler = new TestHttpHandler().Answers($"api/collections/{CollectionId}", HttpStatusCode.NoContent);
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
        var handler = new TestHttpHandler().Answers($"api/collections/{CollectionId}", HttpStatusCode.NoContent);

        await IntegrationGalleryExtensions.PullFromGalleryAsync(CollectionId, Client(handler), Ct);

        var sent = Assert.Single(handler.Sent);

        Assert.Equal(HttpMethod.Delete, sent.Method);
        Assert.Equal($"api/collections/{CollectionId}", sent.Path);
    }

    /// <remarks>
    /// Deleting the collection is the whole of the Gallery pull - the exhibit, its teams, its cards and its
    /// articles all go with it, or none of them do and nobody is told. The third of three identical empty
    /// <c>catch</c> blocks across the integration layer.
    /// </remarks>
    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task PullFromGallery_SwallowsARefusal(HttpStatusCode status)
    {
        var handler = new TestHttpHandler().Answers($"api/collections/{CollectionId}", status);

        await IntegrationGalleryExtensions.PullFromGalleryAsync(CollectionId, Client(handler), Ct);

        Assert.Single(handler.Sent);
    }

    [Fact]
    public async Task PullFromGallery_SwallowsAnUnreachableGallery()
    {
        var handler = new TestHttpHandler().Throws($"api/collections/{CollectionId}");

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
        var handler = new TestHttpHandler().AnswersJson("api/collections", CollectionJson, HttpStatusCode.Created);

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

        var handler = new TestHttpHandler().AnswersJson("api/collections", CollectionJson, HttpStatusCode.Created);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            IntegrationGalleryExtensions.CreateCollectionAsync(msel, Client(handler), null, Ct));

        Assert.Empty(handler.Sent);
    }

    [Fact]
    public async Task CreateExhibit_PostsTheExhibitUnderTheCollectionAndTheScenario()
    {
        var msel = Msel();
        var handler = new TestHttpHandler().AnswersJson("api/exhibits", ExhibitJson, HttpStatusCode.Created);

        await IntegrationGalleryExtensions.CreateExhibitAsync(msel, Client(handler), null, Ct);

        var sent = Assert.Single(handler.Sent);

        Assert.Equal("api/exhibits", sent.Path);

        var body = Body(sent.Body);

        Assert.Equal(ExhibitId.ToString(), body["id"].GetString());
        Assert.Equal(CollectionId.ToString(), body["collectionId"].GetString());
        Assert.Equal(ScenarioId.ToString(), body["scenarioId"].GetString());
    }

    /// <remarks>
    /// A pushed exhibit starts at the beginning whatever the MSEL says, which is right - and arrives with no
    /// name and no description, though Gallery's <c>Exhibit</c> has both and the collection beside it gets
    /// the MSEL's. So the exhibit list in Gallery shows an unnamed row.
    /// </remarks>
    [Fact]
    public async Task CreateExhibit_StartsAtMoveZeroAndSendsNoNameOrDescription()
    {
        var handler = new TestHttpHandler().AnswersJson("api/exhibits", ExhibitJson, HttpStatusCode.Created);

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

        var handler = new TestHttpHandler().AnswersJson("api/exhibits", ExhibitJson, HttpStatusCode.Created);

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
        var team = BlueprintAppFactory.Team(msel.Id);
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
        var team = BlueprintAppFactory.Team(msel.Id);
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
        var team = BlueprintAppFactory.Team(msel.Id);
        await Seed(team);
        await Actor().OnTeam(team).SeedAsync();

        var galleryChose = Guid.Parse("44444444-4444-4444-4444-444444444444");
        var handler = new TestHttpHandler()
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
        var team = BlueprintAppFactory.Team(msel.Id);
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
        var first = BlueprintAppFactory.Team(msel.Id);
        var second = BlueprintAppFactory.Team(msel.Id);
        await Seed(first, second);

        await Actor().OnTeam(first).OnTeam(second).SeedAsync();

        var handler = Teams();
        var seen = new HashSet<Guid>();

        await IntegrationGalleryExtensions.CreateTeamsAsync(
            await Reload(msel.Id), Client(handler), Db, seen, Ct);

        Assert.Single(handler.Paths.Where(x => x == "api/users").ToList());
        Assert.Single(seen);
    }

    /// <remarks>
    /// The one thing <c>GalleryExhibitRole</c> does on this path. It is a free-text column compared with
    /// <c>==</c>, so the match is exact and case-sensitive; anything else - including <c>"observer"</c> -
    /// makes an ordinary member. See <see cref="CreateTeams_IsObserverIsCaseSensitive"/>.
    /// </remarks>
    [Fact]
    public async Task CreateTeams_MarksAnObserverAsOne()
    {
        var msel = await SeedMsel();
        var team = BlueprintAppFactory.Team(msel.Id);
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
        var team = BlueprintAppFactory.Team(msel.Id);
        await Seed(team);

        var actor = await Actor().OnTeam(team).SeedAsync();
        await Seed(RoleFor(msel.Id, actor.Id, role));

        var handler = Teams();

        await IntegrationGalleryExtensions.CreateTeamsAsync(
            await Reload(msel.Id), Client(handler), Db, [], Ct);

        Assert.False(Body(handler.Sent[^1].Body)["isObserver"].GetBoolean());
    }

    /// <remarks>
    /// <c>FirstOrDefault</c> over the user's roles, so which of two is consulted is collection order. Both
    /// answers are accepted here, because pinning one would pin an accident - what is pinned is that one
    /// team-user is created either way.
    /// </remarks>
    [Fact]
    public async Task CreateTeams_ForAUserWithTwoRoles_ConsultsAnArbitraryOne()
    {
        var msel = await SeedMsel();
        var team = BlueprintAppFactory.Team(msel.Id);
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

    /// <remarks>
    /// <para>
    /// The method ends with <c>await blueprintContext.SaveChangesAsync(ct)</c> and has modified nothing: it
    /// creates Gallery objects and reads eager-loaded blueprint ones. <c>CreateCardsAsync</c> ends the same
    /// way and needs to, because it writes each card's <c>GalleryId</c> back.
    /// </para>
    /// <para>
    /// The save is pinned by passing a context and asserting nothing was written, which is the only
    /// observable consequence of a save with an empty change tracker. Deleting the line leaves this test
    /// green - it is there to say the line is understood, not to defend it.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task CreateTeams_SavesAContextItNeverChanged()
    {
        var msel = await SeedMsel();
        var team = BlueprintAppFactory.Team(msel.Id);
        await Seed(team);

        var handler = Teams();
        var before = await NewContext().Teams.CountAsync(Ct);

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

        var handler = new TestHttpHandler()
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
        var handler = new TestHttpHandler()
            .AnswersJson("api/cards", $$"""{"id":"{{galleryChose}}","name":"card"}""", HttpStatusCode.Created);

        await CreateCards(msel.Id, handler);

        await using var after = NewContext();

        Assert.Equal(galleryChose, (await after.Cards.SingleAsync(x => x.Id == card.Id, Ct)).GalleryId);
    }

    [Fact]
    public async Task CreateCards_CreatesATeamCardPerTeamCarryingItsFlags()
    {
        var msel = await SeedMsel();
        var first = BlueprintAppFactory.Team(msel.Id);
        var second = BlueprintAppFactory.Team(msel.Id);
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

    /// <remarks>
    /// <para>
    /// The contrast that makes <c>IntegrationPlayerExtensions.CreateApplicationsAsync</c> a defect rather
    /// than a style. Here the <c>Select(async ...)</c> is <em>inside</em> the loop, so exactly
    /// <c>batchSize</c> requests are in flight at a time; there it is outside, and every item starts at
    /// once.
    /// </para>
    /// <para>
    /// Four cards, <c>batchSize: 2</c>, and a handler that never answers - so the requests pile up and
    /// <c>MaxInFlight</c> is exactly how many the method was willing to have open. Two, because the slice is
    /// awaited before the next begins. Moving the <c>Select</c> out of the loop - the Player shape - makes it
    /// four and turns this test red.
    /// </para>
    /// <para>
    /// <see cref="TestHttpHandler.Holds"/> rather than <see cref="TestHttpHandler.HoldsUntil"/>: a gate that
    /// opens after two arrivals cannot tell the two implementations apart, because everything after the
    /// second completes freely either way. The call never returns, which is the point, so it is expected to
    /// cancel.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task CreateCards_HonoursTheBatchSize()
    {
        var msel = await SeedMsel();

        for (var i = 0; i < 4; i++)
        {
            await Seed(Card(msel.Id, $"card-{i}"));
        }

        var handler = new TestHttpHandler().Holds()
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

    /// <remarks>
    /// <para>
    /// <c>CardId = (Guid)card.GalleryId</c> reads like a guard against a Gallery that answers without an id,
    /// and it is not one: Gallery's <c>Card.Id</c> is a non-nullable <c>Guid</c>, so a response with no
    /// <c>id</c> member deserializes to the all-zeros guid rather than to null. The cast therefore always
    /// succeeds, the blueprint card records all-zeros as its <c>GalleryId</c>, and every team-card for it
    /// points at a card that does not exist.
    /// </para>
    /// <para>
    /// Nothing notices. The push reports success, the cards appear in Gallery with ids of their own, and the
    /// team-cards reference nothing. Checking the answered id for <c>Guid.Empty</c> - or making the DTO
    /// nullable - turns this test red.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task CreateCards_WhenGalleryAnswersWithoutAnId_UsesTheAllZerosGuid()
    {
        var msel = await SeedMsel();
        var team = BlueprintAppFactory.Team(msel.Id);
        await Seed(team);

        var card = Card(msel.Id);
        await Seed(card);
        await Seed(new CardTeamEntity(card.Id, team.Id));

        var handler = new TestHttpHandler()
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

    /// <remarks>
    /// <para>
    /// <c>Enum.TryParse</c> assigns <em>null</em> to an <c>object</c> out parameter when it fails, wiping
    /// out the <c>ItemStatus.Unused</c> the variable was initialized with - which is why the
    /// <c>status == null ? Unused : (ItemStatus)status</c> guard on the article is load-bearing rather than
    /// defensive. The effect is that a misspelled status cell publishes the article as unused, silently.
    /// </para>
    /// <para>
    /// The empty case is the common one: a MSEL with no Status data field at all gets <c>""</c> from
    /// <c>GetArticleValue</c>.
    /// </para>
    /// </remarks>
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

    /// <remarks>
    /// <c>DateTime.TryParse</c> leaves <c>default(DateTime)</c> behind, so an article whose date cell is
    /// empty or unparseable is published dated the first of January in the year one. Gallery sorts a
    /// collection's articles by this.
    /// </remarks>
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

    /// <remarks>
    /// A card id naming nothing, and a card that was never pushed, both give an article with no card. The
    /// first is a stale reference in the MSEL, the second is an ordering problem - <c>CreateCardsAsync</c>
    /// runs first in the push, so a card whose create failed silently leaves its articles unattached.
    /// </remarks>
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
        var first = BlueprintAppFactory.Team(msel.Id);
        var second = BlueprintAppFactory.Team(msel.Id);
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
        var wanted = BlueprintAppFactory.Team(msel.Id);
        wanted.ShortName = "RED";
        var other = BlueprintAppFactory.Team(msel.Id);
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

    /// <remarks>
    /// <para>
    /// An empty <c>ToOrg</c> - which is what a MSEL with no such data field gets - splits to nothing that
    /// matches <c>"ALL"</c> or any team's short name, so the article is created in the collection and given
    /// to no team. In Gallery that is an article nobody can see.
    /// </para>
    /// <para>
    /// Treating an absent <c>ToOrg</c> as <c>"ALL"</c>, or refusing the push, turns this test red. Either
    /// would be a decision; today there is none.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task CreateArticles_WithNoToOrg_CreatesTheArticleAndGivesItToNoTeam()
    {
        var msel = await SeedMsel();
        await Seed(BlueprintAppFactory.Team(msel.Id));

        var fields = await SeedArticleFields(msel.Id);
        var scenarioEvent = await SeedArticleEvent(
            msel.Id, fields, new() { [GalleryArticleParameter.Name] = "unseen" });

        var handler = Articles();

        await CreateArticles(msel.Id, handler, moves: new() { [scenarioEvent.Id] = [0, 0] });

        Assert.Equal("api/articles", Assert.Single(handler.Paths));
    }

    /// <remarks>
    /// <c>movesAndInjects[scenarioEvent.Id]</c>, indexed rather than looked up - so an event the service did
    /// not answer for is a <c>KeyNotFoundException</c> mid-push. The service builds its dictionary from the
    /// MSEL's own events, so this is a disagreement between two reads of the same MSEL rather than bad
    /// input.
    /// </remarks>
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

        var handler = new TestHttpHandler().Holds()
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
        var field = BlueprintAppFactory.DataField();
        field.GalleryArticleParameter = "Name";

        var value = BlueprintAppFactory.DataValue(field.Id, Guid.NewGuid(), "Power out");

        Assert.Equal(
            "Power out",
            IntegrationGalleryExtensions.GetArticleValue("Name", [value], [field]));
    }

    /// <remarks>
    /// An empty string for both misses, which is what every caller then hands to a <c>TryParse</c> - so a
    /// MSEL that maps none of the Gallery parameters still pushes, producing articles that are entirely
    /// default.
    /// </remarks>
    [Fact]
    public void GetArticleValue_WithNoFieldMappedToTheParameter_IsEmpty()
    {
        var field = BlueprintAppFactory.DataField();
        field.GalleryArticleParameter = "Summary";

        Assert.Equal(
            string.Empty,
            IntegrationGalleryExtensions.GetArticleValue("Name", [], [field]));
    }

    [Fact]
    public void GetArticleValue_WithNoValueForTheField_IsEmpty()
    {
        var field = BlueprintAppFactory.DataField();
        field.GalleryArticleParameter = "Name";

        Assert.Equal(
            string.Empty,
            IntegrationGalleryExtensions.GetArticleValue("Name", [], [field]));
    }

    /// <remarks>
    /// <c>SingleOrDefault</c> on the field lookup. <c>GalleryArticleParameter</c> is a free-text column with
    /// no unique index and no validation, so two fields claiming <c>Name</c> is a state the UI can produce -
    /// and the push then fails on the first article, having already created the collection, the exhibit, the
    /// teams and the cards.
    /// </remarks>
    [Fact]
    public void GetArticleValue_ForTwoFieldsMappedToOneParameter_Throws()
    {
        var first = BlueprintAppFactory.DataField();
        first.GalleryArticleParameter = "Name";
        var second = BlueprintAppFactory.DataField();
        second.GalleryArticleParameter = "Name";

        Assert.Throws<InvalidOperationException>(() =>
            IntegrationGalleryExtensions.GetArticleValue("Name", [], [first, second]));
    }

    /// <remarks>
    /// <c>SingleOrDefault</c> on the value lookup. Two values for one field on one event is a pair the
    /// database permits - <c>DataValueEndpointTests</c> records why: the unique index includes the
    /// always-null <c>inject_id</c>, so it constrains nothing.
    /// </remarks>
    [Fact]
    public void GetArticleValue_ForTwoValuesOfOneField_Throws()
    {
        var field = BlueprintAppFactory.DataField();
        field.GalleryArticleParameter = "Name";

        var scenarioEventId = Guid.NewGuid();
        var first = BlueprintAppFactory.DataValue(field.Id, scenarioEventId, "one");
        var second = BlueprintAppFactory.DataValue(field.Id, scenarioEventId, "two");

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
        var team = BlueprintAppFactory.Team(msel.Id);
        await Seed(team);

        var actor = await Actor().WithName("Grace").SeedAsync();

        var handler = new TestHttpHandler()
            .AnswersJson("api/users", UserJson, HttpStatusCode.Created)
            .AnswersJson("api/teamusers", TeamUserJson, HttpStatusCode.Created);

        await IntegrationGalleryExtensions.AddUserToTeamAsync(actor.Id, team.Id, Client(handler), Db, Ct);

        Assert.Equal(["api/users", "api/teamusers"], handler.Paths);
        Assert.Equal("Grace", Body(handler.Sent[0].Body)["name"].GetString());

        var teamUser = Body(handler.Sent[1].Body);

        Assert.Equal(team.Id.ToString(), teamUser["teamId"].GetString());
        Assert.Equal(actor.Id.ToString(), teamUser["userId"].GetString());
    }

    /// <remarks>
    /// Unlike <c>CreateTeamsAsync</c>, this path never sets <c>IsObserver</c> - so somebody who joins an
    /// exhibit after the push is a participant whatever their <c>GalleryExhibitRole</c> says. The two ways
    /// into a Gallery team disagree.
    /// </remarks>
    [Fact]
    public async Task AddUserToTeam_NeverMarksAnObserver()
    {
        var msel = await SeedMsel();
        var team = BlueprintAppFactory.Team(msel.Id);
        await Seed(team);

        var actor = await Actor().SeedAsync();
        await Seed(RoleFor(msel.Id, actor.Id, "Observer"));

        var handler = new TestHttpHandler()
            .AnswersJson("api/users", UserJson, HttpStatusCode.Created)
            .AnswersJson("api/teamusers", TeamUserJson, HttpStatusCode.Created);

        await IntegrationGalleryExtensions.AddUserToTeamAsync(actor.Id, team.Id, Client(handler), Db, Ct);

        Assert.False(Body(handler.Sent[^1].Body)["isObserver"].GetBoolean());
    }

    /// <remarks>
    /// Two mechanisms produce this and either would do, exactly as in
    /// <c>IntegrationPlayerExtensions.AddUserToTeamAsync</c>: the <c>if (user != null)</c> guard skips the
    /// create, and the <c>catch</c> around it would absorb the <c>NullReferenceException</c> if the guard
    /// were removed. Replacing the guard with <c>if (true)</c> leaves this test green, so the guard is
    /// redundant in both files.
    /// </remarks>
    [Fact]
    public async Task AddUserToTeam_ForSomeoneBlueprintDoesNotKnow_AddsThemWithoutCreatingThem()
    {
        var msel = await SeedMsel();
        var team = BlueprintAppFactory.Team(msel.Id);
        await Seed(team);

        var stranger = Guid.NewGuid();
        var handler = new TestHttpHandler().AnswersJson("api/teamusers", TeamUserJson, HttpStatusCode.Created);

        await IntegrationGalleryExtensions.AddUserToTeamAsync(stranger, team.Id, Client(handler), Db, Ct);

        Assert.Equal("api/teamusers", Assert.Single(handler.Paths));
    }

    [Fact]
    public async Task AddUserToTeam_SwallowsAFailureCreatingTheUser()
    {
        var msel = await SeedMsel();
        var team = BlueprintAppFactory.Team(msel.Id);
        await Seed(team);

        var actor = await Actor().SeedAsync();

        var handler = new TestHttpHandler()
            .Answers("api/users", HttpStatusCode.Conflict)
            .AnswersJson("api/teamusers", TeamUserJson, HttpStatusCode.Created);

        await IntegrationGalleryExtensions.AddUserToTeamAsync(actor.Id, team.Id, Client(handler), Db, Ct);

        Assert.Equal(["api/users", "api/teamusers"], handler.Paths);
    }

    /// <remarks>
    /// The same half-idempotence as <c>IntegrationPlayerExtensions.AddUserToTeamAsync</c>, line for line:
    /// the create is inside a <c>try</c> commented "User might already exist, continue" and the add, one
    /// line below, is not.
    /// </remarks>
    [Fact]
    public async Task AddUserToTeam_DoesNotSwallowAFailureAddingToTheTeam()
    {
        var msel = await SeedMsel();
        var team = BlueprintAppFactory.Team(msel.Id);
        await Seed(team);

        var actor = await Actor().SeedAsync();

        var handler = new TestHttpHandler()
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

    private static GalleryApiClient Client(TestHttpHandler handler) =>
        new(ApiClientsExtensions.GetHttpClient(handler.AsFactory(), "http://gallery.example/", null));

    private static TestHttpHandler Teams() =>
        new TestHttpHandler()
            .AnswersJson("api/teams", TeamJson, HttpStatusCode.Created)
            .AnswersJson("api/users", UserJson, HttpStatusCode.Created)
            .AnswersJson("api/teamusers", TeamUserJson, HttpStatusCode.Created);

    private static TestHttpHandler Memberships() =>
        new TestHttpHandler()
            .AnswersJson("api/exhibit-roles", RolesJson)
            .AnswersJson($"api/exhibits/{ExhibitId}/memberships", "{}", HttpStatusCode.Created);

    private static TestHttpHandler Cards() =>
        new TestHttpHandler()
            .AnswersJson("api/cards", CardJson, HttpStatusCode.Created)
            .AnswersJson("api/teamcards", """{"id":"00000000-0000-0000-0000-000000000001"}""", HttpStatusCode.Created);

    private static TestHttpHandler Articles() =>
        new TestHttpHandler()
            .AnswersJson("api/articles", ArticleJson, HttpStatusCode.Created)
            .AnswersJson("api/teamarticles", """{"id":"00000000-0000-0000-0000-000000000002"}""", HttpStatusCode.Created);

    private static readonly string RolesJson =
        $$"""[{"id":"{{ObserverRoleId}}","name":"Observer"},{"id":"{{ParticipantRoleId}}","name":"Participant"}]""";

    /// <summary>A MSEL pushed as far as having a Gallery collection, an exhibit and a Steamfitter scenario.</summary>
    private static MselEntity Msel()
    {
        var msel = BlueprintAppFactory.Msel();

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
            var field = BlueprintAppFactory.DataField(mselId: mselId, name: parameter.ToString());
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
        var scenarioEvent = BlueprintAppFactory.ScenarioEvent(mselId);
        scenarioEvent.IntegrationTarget = target;
        await Seed(scenarioEvent);

        foreach (var (parameter, value) in values)
        {
            await Seed(BlueprintAppFactory.DataValue(fields[parameter].Id, scenarioEvent.Id, value));
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

    private async Task CreateCards(Guid mselId, TestHttpHandler handler, int batchSize = 10) =>
        await IntegrationGalleryExtensions.CreateCardsAsync(
            await Reload(mselId), Client(handler), Db, batchSize, Ct);

    private async Task CreateArticles(
        Guid mselId, TestHttpHandler handler, Dictionary<Guid, int[]> moves, int batchSize = 10) =>
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
