// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Blueprint.Api.Services;
using Blueprint.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Blueprint.Api.Tests;

/// <summary>
/// <c>JoinService</c> - the worker that puts one user on one team in Player, Gallery and CITE after they
/// accept an invitation.
/// </summary>
/// <remarks>
/// <para>
/// Driven the way production drives it: an item on the real <c>IJoinQueue</c>, taken by the worker's
/// loop and run on a thread whose delegate is <c>private async void</c>. The three
/// <c>AddUserToTeamAsync</c> calls it makes are already pinned by the three
/// <c>Integration*ExtensionsTests</c> files, so these tests are about the orchestration around them:
/// which of the three run, in what order, what a token costs, and what is left behind when one throws.
/// </para>
/// <para>
/// <strong>All three joins share one <c>try</c>.</strong> Where <c>IntegrationService</c> wraps each
/// pull in its own, this wraps all three together - so the first failure ends the join and the
/// applications after it are never contacted. Player is first, Gallery second, CITE third, and there is
/// no retry, no record and no cleanup: a Gallery outage leaves the user on Player's team, off Gallery's
/// and CITE's, and the only evidence anywhere is one line in blueprint's log. See
/// <see cref="Join_WhenPlayerFails_NeverReachesGalleryOrCite"/> and
/// <see cref="Join_WhenGalleryFails_LeavesTheUserJoinedToPlayerOnly"/>.
/// </para>
/// <para>
/// <strong>The user is never told.</strong> The worker is given an <c>IHubContext&lt;MainHub&gt;</c>,
/// holds it in a field, and sends nothing through it on any path - so the person who accepted the
/// invitation has no way to learn whether they joined, and neither has the caller: the request that
/// queued the work answered before it started. See
/// <see cref="Join_TellsNobodyAnything"/>.
/// </para>
/// <para>
/// <strong>A token is fetched per queue item, not per worker</strong>, and three round trips is what a
/// token costs - discovery, JWKS, then the token itself. So a MSEL whose thirty users all accept at
/// once costs ninety requests to Keycloak before any of them reaches a sibling API, and a join with
/// all three flags false costs three requests and does nothing at all. See
/// <see cref="Join_WithEveryFlagFalse_StillFetchesAToken"/> and
/// <see cref="Join_FetchesItsOwnTokenPerItem"/>.
/// </para>
/// <para>
/// <strong>A null queue item terminates the process</strong> and is deliberately not tested.
/// <c>ProcessTheJoin</c> casts and dereferences its argument on lines 88-89, <em>outside</em> the
/// <c>try</c> that begins on line 92, and it is an <c>async void</c> running on a foreground
/// <c>Thread</c> - so the <c>NullReferenceException</c> is unhandled and takes the host down rather
/// than being logged. Nothing stops <c>IJoinQueue.Add(null)</c>. A test for this would kill the test
/// run, so it is recorded here instead: moving the two lines inside the <c>try</c> is the fix.
/// </para>
/// <para>
/// Per this branch's rule, every test above characterizes rather than fixes, and says what fixing it
/// will do to the test.
/// </para>
/// </remarks>
public class JoinServiceTests(DatabaseFixture fixture) : DatabaseTestBase(fixture)
{
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

    /// <remarks>
    /// Awaited one after another inside one method, so the order is the source order and not a race -
    /// unlike the fan-outs in the extensions themselves, where an ordering assertion would be a flake.
    /// </remarks>
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

    /// <remarks>
    /// Three requests to Keycloak and nothing else. <c>MselService</c> sets all three flags from the
    /// MSEL's own <c>Use*</c> columns, so a MSEL integrated with nothing queues one of these per user
    /// who accepts an invitation. Moving the token fetch inside the first flag check turns this red.
    /// </remarks>
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

    /// <remarks>
    /// Two items, two tokens - the fetch is inside <c>ProcessTheJoin</c>, which runs once per item on a
    /// thread of its own. <c>IntegrationService</c> has the same shape, and both are the opposite of
    /// what <c>ApiClientsExtensions</c>' configured <c>TokenExpirationBufferSeconds</c> implies is
    /// happening. Caching a token turns this red, which is the point.
    /// </remarks>
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

    /// <remarks>
    /// The user row is blueprint's, and each <c>AddUserToTeamAsync</c> looks it up to decide whether to
    /// create the user in its own application first. A user id the database does not know skips all
    /// three creates and still gets added to all three teams - which is the right shape for an
    /// application that already has the user, and indistinguishable from a bad id.
    /// </remarks>
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

    /// <remarks>
    /// <para>
    /// One <c>try</c> for all three, so the first failure is the last thing that happens. Player is
    /// first, so an unreachable Player means the user joins nothing at all - and the request that
    /// queued this answered 200 long before.
    /// </para>
    /// <para>
    /// Giving each application its own <c>try</c>, as <c>IntegrationService.PullIntegrations</c> does,
    /// turns this red.
    /// </para>
    /// </remarks>
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

    /// <remarks>
    /// The partial join, which is the shape that matters: Player has the user on the team, Gallery and
    /// CITE do not, nothing records the discrepancy and nothing will retry. The user sees the Player
    /// view and an empty Gallery.
    /// </remarks>
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

    /// <remarks>
    /// The log line is better than <c>IntegrationService</c>'s: it names the step that actually failed
    /// and carries the user and the team, so an operator reading the log can tell who did not join
    /// what. It is still the only trace.
    /// </remarks>
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

    /// <remarks>
    /// <c>currentProcessStep</c> is still its initial <c>"Begin processing"</c> when
    /// <c>GetToken</c> throws, which is accurate as far as it goes and says nothing about the identity
    /// provider - so an operator reading this line learns that a join failed and has to guess why. Note
    /// the contrast with <c>IntegrationService</c>, whose equivalent message stringifies a
    /// dependency-injection scope into the user-visible text.
    /// </remarks>
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

    /// <remarks>
    /// Nothing on the hub, on any path. The field is assigned in the constructor and read nowhere,
    /// which is worth pinning because the fix is small and the absence is invisible: adding any
    /// notification at all turns this red.
    /// </remarks>
    [Fact]
    public async Task Join_TellsNobodyAnything()
    {
        var actor = await Actor().SeedAsync();
        var harness = Harness();

        await harness.JoinAsync(Information(actor.Id));
        await harness.WaitForRequests(9, Ct);

        Assert.Empty(harness.Hub.Sends);
    }

    /// <remarks>
    /// The <c>Run</c> loop catches around the <c>Take</c> and each item is processed on its own thread,
    /// so a failure cannot wedge the worker. The one property of this service that is unambiguously
    /// right.
    /// </remarks>
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

    /// <remarks>
    /// The join writes nothing to blueprint, so a MSEL's own <c>TeamUser</c> rows are whatever
    /// <c>MselService</c> wrote before queueing - and if this worker fails, blueprint still says the
    /// user is on the team. That disagreement is the reason a partial join cannot be detected.
    /// </remarks>
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
    private QueueWorkerHarness Harness(Func<TestHttpHandler, TestHttpHandler> first = null) =>
        new(Session, Everything(first));

    /// <summary>The identity provider and the six routes a join makes, and nothing else.</summary>
    private static TestHttpHandler Everything(Func<TestHttpHandler, TestHttpHandler> first = null) =>
        QueueWorkerHarness.IdentityProvider(first is null ? new TestHttpHandler() : first(new TestHttpHandler()))
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
