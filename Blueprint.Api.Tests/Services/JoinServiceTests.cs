// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Blueprint.Api.Services;
using Blueprint.Api.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Blueprint.Api.Tests.Services;

/// <summary><c>JoinService</c> - the worker that puts one user on one team in Player, Gallery and CITE
/// after they accept an invitation.</summary>
public class JoinServiceTests(DatabaseFixture fixture) : DatabaseTestBase(fixture)
{
    /// <summary>Starts describing an actor to seed over this test's database.</summary>
    private TestActorBuilder Actor() => new(Db, Ct);

    [Fact]
    public async Task Join_JoinsPlayerThenGalleryThenCite()
    {
        var actor = await Actor().WithName("Joiner").SeedAsync();
        var harness = Harness();

        await harness.JoinAsync(Information(actor.Id));
        await harness.WaitForRequests(9, Ct);

        Assert.Equal(
            [
                "player/api/users",
                $"player/api/teams/{TeamId}/users/{actor.Id}",
                "gallery/api/users",
                "gallery/api/teamusers",
                "cite/api/users",
                $"cite/api/teams/{TeamId}/memberships"
            ],
            harness.SiblingPaths);
    }

    /// <summary>Every request is a POST, sent in source order.</summary>
    [Fact]
    public async Task Join_SendsEveryRequestAsAPost()
    {
        var actor = await Actor().SeedAsync();
        var harness = Harness();

        await harness.JoinAsync(Information(actor.Id));
        await harness.WaitForRequests(9, Ct);

        Assert.All(harness.Siblings, x => Assert.Equal(HttpMethod.Post, x.Method));
    }

    [Theory]
    [InlineData(true, false, false, "player/")]
    [InlineData(false, true, false, "gallery/")]
    [InlineData(false, false, true, "cite/")]
    public async Task Join_ContactsOnlyTheApplicationsItsFlagsName(
        bool player, bool gallery, bool cite, string prefix)
    {
        var actor = await Actor().SeedAsync();
        var harness = Harness();

        await harness.JoinAsync(Information(actor.Id, player, gallery, cite));
        await harness.WaitForRequests(5, Ct);

        Assert.Equal(2, harness.SiblingPaths.Count);
        Assert.All(harness.SiblingPaths, x => Assert.StartsWith(prefix, x));
    }

    /// <summary>Join with every flag false still fetches a token.</summary>
    [Fact]
    public async Task Join_WithEveryFlagFalse_StillFetchesAToken()
    {
        var actor = await Actor().SeedAsync();
        var harness = Harness();

        await harness.JoinAsync(Information(actor.Id, player: false, gallery: false, cite: false));
        await harness.WaitForRequests(3, Ct);

        Assert.Equal(
            [
                IntegrationServiceHarness.DiscoveryPath,
                IntegrationServiceHarness.JwksPath,
                IntegrationServiceHarness.TokenPath
            ],
            harness.Handler.Paths);

        Assert.Empty(harness.SiblingPaths);
    }

    [Fact]
    public async Task Join_FetchesOneTokenForAllThreeApplications()
    {
        var actor = await Actor().SeedAsync();
        var harness = Harness();

        await harness.JoinAsync(Information(actor.Id));
        await harness.WaitForRequests(9, Ct);

        Assert.Single(
            harness.Handler.Paths.Where(x => x == IntegrationServiceHarness.TokenPath).ToList());
    }

    /// <summary>Each queue item fetches its own token.</summary>
    [Fact]
    public async Task Join_FetchesItsOwnTokenPerItem()
    {
        var actor = await Actor().SeedAsync();
        var harness = Harness();

        await harness.JoinAsync(Information(actor.Id, player: false, gallery: false, cite: false));

        harness.JoinQueue.Add(Information(actor.Id, player: false, gallery: false, cite: false));

        await harness.WaitForRequests(6, Ct);

        Assert.Equal(
            2, harness.Handler.Paths.Count(x => x == IntegrationServiceHarness.TokenPath));
    }

    /// <summary>A user id blueprint does not know skips the three creates and is still added to the three
    /// teams.</summary>
    [Fact]
    public async Task Join_ForAUserBlueprintDoesNotKnow_AddsThemToTheTeamsAnyway()
    {
        var harness = Harness();

        await harness.JoinAsync(Information(Guid.NewGuid()));
        await harness.WaitForRequests(6, Ct);

        Assert.DoesNotContain("player/api/users", harness.SiblingPaths);
        Assert.DoesNotContain("gallery/api/users", harness.SiblingPaths);
        Assert.DoesNotContain("cite/api/users", harness.SiblingPaths);
        Assert.Equal(3, harness.SiblingPaths.Count);
    }

    [Fact]
    public async Task Join_SendsTheUsersOwnNameToEachApplication()
    {
        var actor = await Actor().WithName("Ada Lovelace").SeedAsync();
        var harness = Harness();

        await harness.JoinAsync(Information(actor.Id));
        await harness.WaitForRequests(9, Ct);

        var creates = harness.Siblings.Where(x => x.Path.EndsWith("api/users")).ToList();

        Assert.Equal(3, creates.Count);
        Assert.All(creates, x => Assert.Contains("Ada Lovelace", x.Body));
        Assert.All(creates, x => Assert.Contains(actor.Id.ToString(), x.Body));
    }

    /// <summary>Join when player fails never reaches gallery or CITE.</summary>
    [Fact]
    public async Task Join_WhenPlayerFails_NeverReachesGalleryOrCite()
    {
        var actor = await Actor().SeedAsync();
        var harness = Harness(x => x.Throws($"player/api/teams/{TeamId}/users/*"));

        await harness.JoinAsync(Information(actor.Id));
        await harness.WaitForLog(x => x.Exception is not null, Ct);

        Assert.DoesNotContain("gallery/api/users", harness.SiblingPaths);
        Assert.DoesNotContain("cite/api/users", harness.SiblingPaths);
    }

    /// <summary>A Gallery failure leaves the user joined to Player only.</summary>
    [Fact]
    public async Task Join_WhenGalleryFails_LeavesTheUserJoinedToPlayerOnly()
    {
        var actor = await Actor().SeedAsync();
        var harness = Harness(x => x.Throws("gallery/api/teamusers"));

        await harness.JoinAsync(Information(actor.Id));
        await harness.WaitForLog(x => x.Exception is not null, Ct);

        Assert.Contains($"player/api/teams/{TeamId}/users/{actor.Id}", harness.SiblingPaths);
        Assert.Contains("gallery/api/teamusers", harness.SiblingPaths);
        Assert.DoesNotContain($"cite/api/teams/{TeamId}/memberships", harness.SiblingPaths);
    }

    /// <summary>A failed join logs the step that failed, the user and the team.</summary>
    [Fact]
    public async Task Join_WhenAnApplicationFails_LogsTheStepTheUserAndTheTeam()
    {
        var actor = await Actor().SeedAsync();
        var harness = Harness(x => x.Throws("gallery/api/teamusers"));

        await harness.JoinAsync(Information(actor.Id));

        var logged = await harness.WaitForLog(x => x.Exception is not null, Ct);

        Assert.Equal(
            $"Gallery - add user to team Join for User: {actor.Id}, Team: {TeamId}", logged.Message);
        Assert.Contains("gallery/api/teamusers", logged.Exception.Message);
    }

    /// <summary>When no token can be fetched the log names the initial step, <c>Begin processing</c>.</summary>
    [Fact]
    public async Task Join_WhenNoTokenCanBeFetched_LogsAStepThatNeverRan()
    {
        var actor = await Actor().SeedAsync();
        var harness = Harness(x => x.AnswersJson(
            IntegrationServiceHarness.DiscoveryPath, "not found", HttpStatusCode.NotFound));

        await harness.JoinAsync(Information(actor.Id));

        var logged = await harness.WaitForLog(x => x.Exception is not null, Ct);

        Assert.Equal($"Begin processing Join for User: {actor.Id}, Team: {TeamId}", logged.Message);
        Assert.Empty(harness.SiblingPaths);
    }

    /// <summary>Join tells nobody anything.</summary>
    [Fact]
    public async Task Join_TellsNobodyAnything()
    {
        var actor = await Actor().SeedAsync();
        var harness = Harness();

        await harness.JoinAsync(Information(actor.Id));
        await harness.WaitForRequests(9, Ct);

        Assert.Empty(harness.Hub.Sent(actor.Id));
    }

    /// <summary>A failed item does not stop the worker taking the next one.</summary>
    [Fact]
    public async Task Join_AfterAFailedItem_KeepsProcessingTheQueue()
    {
        var actor = await Actor().SeedAsync();
        var harness = Harness(x => x.Throws("gallery/api/teamusers"));

        await harness.JoinAsync(Information(actor.Id, player: false, gallery: true, cite: false));
        await harness.WaitForLog(x => x.Exception is not null, Ct);

        harness.JoinQueue.Add(Information(actor.Id, player: true, gallery: false, cite: false));

        await harness.WaitForRequests(9, Ct);

        Assert.Contains($"player/api/teams/{TeamId}/users/{actor.Id}", harness.SiblingPaths);
    }

    /// <summary>The join writes nothing to blueprint's database.</summary>
    [Fact]
    public async Task Join_WritesNothingToBlueprint()
    {
        var actor = await Actor().SeedAsync();
        var harness = Harness();

        await using var context = NewContext();

        var teamUsers = await context.TeamUsers.CountAsync(Ct);
        var users = await context.Users.CountAsync(Ct);

        await harness.JoinAsync(Information(actor.Id));
        await harness.WaitForRequests(9, Ct);

        await using var after = NewContext();

        Assert.Equal(teamUsers, await after.TeamUsers.CountAsync(Ct));
        Assert.Equal(users, await after.Users.CountAsync(Ct));
    }

    // ---------------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------------

    private static readonly Guid TeamId = Guid.Parse("77777777-7777-7777-7777-777777777777");

    private static JoinInformation Information(
        Guid userId, bool player = true, bool gallery = true, bool cite = true) =>
        new()
        {
            UserId = userId,
            TeamId = TeamId,
            UsePlayer = player,
            UseGallery = gallery,
            UseCite = cite
        };

    /// <remarks>
    /// <paramref name="first"/> is applied before the general rules, because rules are matched in the
    /// order they were added: a test that wants one route to fail has to register it first.
    /// </remarks>
    private QueueWorkerHarness Harness(Func<SiblingApiHandler, SiblingApiHandler> first = null) =>
        new(Session, Everything(first));

    /// <summary>The identity provider and the six routes a join makes, and nothing else.</summary>
    private static SiblingApiHandler Everything(Func<SiblingApiHandler, SiblingApiHandler> first = null) =>
        QueueWorkerHarness.IdentityProvider(first is null ? new SiblingApiHandler() : first(new SiblingApiHandler()))
            .AnswersJson("player/api/users", UserJson, HttpStatusCode.Created)
            .AnswersJson("player/api/teams/*/users/*", UserJson)
            .AnswersJson("gallery/api/users", UserJson, HttpStatusCode.Created)
            .AnswersJson("gallery/api/teamusers", TeamUserJson, HttpStatusCode.Created)
            .AnswersJson("cite/api/users", UserJson, HttpStatusCode.Created)
            .AnswersJson("cite/api/teams/*/memberships", MembershipJson, HttpStatusCode.Created);

    private static readonly string UserJson = $$"""{"id":"{{TeamId}}","name":"user"}""";

    private static readonly string TeamUserJson =
        $$"""{"id":"{{TeamId}}","teamId":"{{TeamId}}","userId":"{{TeamId}}"}""";

    private static readonly string MembershipJson =
        $$"""{"id":"{{TeamId}}","teamId":"{{TeamId}}","userId":"{{TeamId}}"}""";
}
