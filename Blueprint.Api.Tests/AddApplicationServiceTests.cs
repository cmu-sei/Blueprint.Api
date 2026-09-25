// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Blueprint.Api.Services;
using Blueprint.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Player.Api.Client;
using Xunit;

namespace Blueprint.Api.Tests;

/// <summary>
/// <c>AddApplicationService</c> - the worker that puts one application in front of one Player team after
/// somebody pushes it from blueprint's application list.
/// </summary>
/// <remarks>
/// <para>
/// Two requests to player.api and nothing else: create the application under a view, then create an
/// instance of it on a team. It is the single-application version of
/// <c>IntegrationPlayerExtensions.CreateApplicationsAsync</c>, which the push loop runs over every
/// application a MSEL has - and the two do not agree, which is most of what this file records.
/// </para>
/// <para>
/// <strong>Nothing here reads the database.</strong> The worker resolves a <c>BlueprintContext</c>,
/// holds it open for the whole add and never asks it anything: the view id, the team id, the display
/// order and the application itself all arrive on the queue item, put there by
/// <c>PlayerService.PushApplication</c>. So an application can be sent to a view the MSEL no longer
/// points at, and the worker has no way to notice. The context is a dependency in name only, and the
/// <c>using</c> around it disposes something that was never used.
/// </para>
/// <para>
/// <strong>The step it reports is the one step that cannot fail.</strong> <c>currentProcessStep</c> is
/// set to <c>"Player - get API client"</c> before the client is built, and is never set again - so both
/// HTTP calls, which are the only things here that can go wrong, are reported as a failure to construct
/// a client object. See <see cref="AddApplication_WhenTheApplicationCannotBeCreated_BlamesBuildingTheClient"/>.
/// </para>
/// <para>
/// <strong>And the line names nothing.</strong> <c>loggerInformation</c> is the constant
/// <c>"Adding Application"</c>: no application, no team, no view, no user. <c>JoinService</c>, the other
/// queue worker, carries the user and the team in its equivalent - so a failed add leaves an operator
/// knowing only that some application somewhere was not added. See
/// <see cref="AddApplication_WhenAStepFails_LogsNoApplicationNoTeamAndNoView"/>.
/// </para>
/// <para>
/// <strong>The user is never told</strong>, on any path. The worker is given an
/// <c>IHubContext&lt;MainHub&gt;</c> and sends nothing through it, and the request that queued the work
/// answered before it started - so blueprint's UI shows the application as pushed whether Player took
/// it or not. The same finding as <c>JoinServiceTests</c>, and from the same author. See
/// <see cref="AddApplication_TellsNobodyAnything"/>.
/// </para>
/// <para>
/// <strong>A null queue item terminates the process</strong> and is deliberately not tested, for the
/// reason <c>JoinServiceTests</c> gives: <c>ProcessTheAddApplication</c> casts its argument on line 88,
/// outside the <c>try</c> that begins on line 92, in an <c>async void</c> on a foreground
/// <c>Thread</c>. Moving the cast inside the <c>try</c> is the fix.
/// </para>
/// <para>
/// Per this branch's rule, every test above characterizes rather than fixes, and says what fixing it
/// will do to the test.
/// </para>
/// </remarks>
public class AddApplicationServiceTests(DatabaseFixture fixture) : DatabaseTestBase(fixture)
{
    [Fact]
    public async Task AddApplication_CreatesTheApplicationThenTheInstance()
    {
        var harness = Harness();

        await harness.AddApplicationAsync(Information());
        await harness.WaitForRequests(5, Ct);

        Assert.Equal(
            [
                $"player/api/views/{ViewId}/applications",
                $"player/api/teams/{TeamId}/application-instances"
            ],
            harness.SiblingPaths);
    }

    [Fact]
    public async Task AddApplication_SendsBothRequestsAsAPost()
    {
        var harness = Harness();

        await harness.AddApplicationAsync(Information());
        await harness.WaitForRequests(5, Ct);

        Assert.All(harness.Siblings, x => Assert.Equal(HttpMethod.Post, x.Method));
    }

    /// <remarks>
    /// The view comes off the queued application, not from the MSEL - the worker never loads one. So
    /// whichever view <c>PlayerService.PushApplication</c> read at the time the request was made is the
    /// view the application lands in, however long the item then sat on the queue and whatever the MSEL
    /// says by the time it is taken. The push loop, by contrast, reads
    /// <c>msel.PlayerViewId</c> for every application as it goes.
    /// </remarks>
    [Fact]
    public async Task AddApplication_PostsUnderTheViewTheQueuedApplicationNames()
    {
        var other = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var harness = Harness(x => x.AnswersJson(
            $"player/api/views/{other}/applications", ApplicationJson, HttpStatusCode.Created));

        await harness.AddApplicationAsync(Information(viewId: other));
        await harness.WaitForRequests(5, Ct);

        Assert.Equal($"player/api/views/{other}/applications", harness.SiblingPaths[0]);
    }

    [Fact]
    public async Task AddApplication_SendsTheApplicationItWasGiven()
    {
        var harness = Harness();

        await harness.AddApplicationAsync(Information());
        await harness.WaitForRequests(5, Ct);

        var body = Body(harness.Siblings[0].Body);

        Assert.Equal("Console", body["name"].GetString());
        Assert.Equal("http://console.example/", body["url"].GetString());
        Assert.Equal("star.png", body["icon"].GetString());
        Assert.Equal(ViewId.ToString(), body["viewId"].GetString());
        Assert.True(body["embeddable"].GetBoolean());
        Assert.False(body["loadInBackground"].GetBoolean());
    }

    /// <remarks>
    /// The instance names the id Player answered with, not the id on the queued application - the same
    /// as the push loop does, and right: blueprint's own application row and Player's are different
    /// rows with different ids.
    /// </remarks>
    [Fact]
    public async Task AddApplication_UsesTheApplicationIdPlayerAnsweredWith()
    {
        var playerSaid = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        var harness = Harness(x => x.AnswersJson(
            $"player/api/views/{ViewId}/applications",
            $$"""{"id":"{{playerSaid}}","name":"Console","viewId":"{{ViewId}}"}""",
            HttpStatusCode.Created));

        await harness.AddApplicationAsync(Information());
        await harness.WaitForRequests(5, Ct);

        var body = Body(harness.Siblings[1].Body);

        Assert.Equal(playerSaid.ToString(), body["applicationId"].GetString());
        Assert.Equal(TeamId.ToString(), body["teamId"].GetString());
    }

    /// <remarks>
    /// <para>
    /// Player's <c>Application.Id</c> is a non-nullable <c>Guid</c>, so an answer without one is not an
    /// error anywhere - it is <c>Guid.Empty</c>, and an instance is created pointing at an application
    /// that does not exist. <c>CreateApplicationsAsync</c> writes the same value through an explicit
    /// <c>(Guid)</c> cast that cannot fail and reads like a guard, which is exactly the shape of
    /// <c>CreateCardsAsync</c>'s <c>CardId = (Guid)card.GalleryId</c> in the Gallery extensions.
    /// </para>
    /// <para>
    /// Checking the id against <c>Guid.Empty</c> before building the form turns this red. Nothing else
    /// will: the cast is not a guard and adding one to this method would not make it one.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AddApplication_WhenPlayerAnswersWithoutAnId_UsesTheAllZerosGuid()
    {
        var harness = Harness(x => x.AnswersJson(
            $"player/api/views/{ViewId}/applications",
            $$"""{"name":"Console","viewId":"{{ViewId}}"}""",
            HttpStatusCode.Created));

        await harness.AddApplicationAsync(Information());
        await harness.WaitForRequests(5, Ct);

        var body = Body(harness.Siblings[1].Body);

        Assert.Equal(Guid.Empty.ToString(), body["applicationId"].GetString());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(17)]
    public async Task AddApplication_PassesTheDisplayOrderThrough(int displayOrder)
    {
        var harness = Harness();

        await harness.AddApplicationAsync(Information(displayOrder: displayOrder));
        await harness.WaitForRequests(5, Ct);

        Assert.Equal(displayOrder, Body(harness.Siblings[1].Body)["displayOrder"].GetInt32());
    }

    /// <remarks>
    /// One <c>try</c> for both calls, so an unreachable Player means nothing at all happens - which is
    /// the right shape here, unlike in <c>JoinService</c>, because the second call is meaningless
    /// without the first.
    /// </remarks>
    [Fact]
    public async Task AddApplication_WhenTheApplicationCannotBeCreated_NeverCreatesTheInstance()
    {
        var harness = Harness(x => x.Throws($"player/api/views/{ViewId}/applications"));

        await harness.AddApplicationAsync(Information());
        await harness.WaitForApplicationLog(x => x.Exception is not null, Ct);

        Assert.DoesNotContain(
            $"player/api/teams/{TeamId}/application-instances", harness.SiblingPaths);
    }

    /// <remarks>
    /// <c>currentProcessStep</c> is set once, to <c>"Player - get API client"</c>, on the line before
    /// the client is constructed - and constructing a client cannot fail: it is
    /// <c>new PlayerApiClient(...)</c> over a factory-supplied <c>HttpClient</c>. So the only step this
    /// worker ever blames is the only step that never fails, and the two that do are invisible.
    /// Setting the step before each call turns this red.
    /// </remarks>
    [Fact]
    public async Task AddApplication_WhenTheApplicationCannotBeCreated_BlamesBuildingTheClient()
    {
        var harness = Harness(x => x.Throws($"player/api/views/{ViewId}/applications"));

        await harness.AddApplicationAsync(Information());

        var logged = await harness.WaitForApplicationLog(x => x.Exception is not null, Ct);

        Assert.Equal("Player - get API client Adding Application", logged.Message);
    }

    /// <remarks>
    /// The instance call fails and is reported identically to the create failing, because the two
    /// share a step label and the label mentions neither.
    /// </remarks>
    [Fact]
    public async Task AddApplication_WhenTheInstanceCannotBeCreated_IsReportedIdentically()
    {
        var harness = Harness(x => x.Throws($"player/api/teams/{TeamId}/application-instances"));

        await harness.AddApplicationAsync(Information());

        var logged = await harness.WaitForApplicationLog(x => x.Exception is not null, Ct);

        Assert.Equal("Player - get API client Adding Application", logged.Message);
        Assert.Contains($"player/api/views/{ViewId}/applications", harness.SiblingPaths);
    }

    /// <remarks>
    /// Not one identifier in it. The team, the view, the display order and the application's own name
    /// are all in hand on the line that logs this, and <c>JoinService</c>'s equivalent carries its user
    /// and team - so this is a house style broken rather than information unavailable. Interpolating
    /// anything at all into <c>loggerInformation</c> turns this red.
    /// </remarks>
    [Fact]
    public async Task AddApplication_WhenAStepFails_LogsNoApplicationNoTeamAndNoView()
    {
        var harness = Harness(x => x.Throws($"player/api/views/{ViewId}/applications"));

        await harness.AddApplicationAsync(Information());

        var logged = await harness.WaitForApplicationLog(x => x.Exception is not null, Ct);

        Assert.DoesNotContain(TeamId.ToString(), logged.Message);
        Assert.DoesNotContain(ViewId.ToString(), logged.Message);
        Assert.DoesNotContain("Console", logged.Message);
    }

    /// <remarks>
    /// <c>currentProcessStep</c> is still <c>"Begin processing"</c> when <c>GetToken</c> throws, which
    /// is accurate and says nothing about the identity provider. Note that this is the one failure the
    /// step label describes correctly, and it describes it by not having been updated yet.
    /// </remarks>
    [Fact]
    public async Task AddApplication_WhenNoTokenCanBeFetched_LogsAStepThatNeverRan()
    {
        var harness = Harness(x => x.AnswersJson(
            IntegrationServiceHarness.DiscoveryPath, "not found", HttpStatusCode.NotFound));

        await harness.AddApplicationAsync(Information());

        var logged = await harness.WaitForApplicationLog(x => x.Exception is not null, Ct);

        Assert.Equal("Begin processing Adding Application", logged.Message);
        Assert.Empty(harness.SiblingPaths);
    }

    [Fact]
    public async Task AddApplication_FetchesOneTokenForBothRequests()
    {
        var harness = Harness();

        await harness.AddApplicationAsync(Information());
        await harness.WaitForRequests(5, Ct);

        Assert.Single(
            harness.Handler.Paths.Where(x => x == IntegrationServiceHarness.TokenPath).ToList());
    }

    /// <remarks>
    /// Two items, two tokens, at three round trips each - the fetch is inside
    /// <c>ProcessTheAddApplication</c>, which runs once per item. Same as <c>JoinService</c> and
    /// <c>IntegrationService</c>: nothing in blueprint caches a token.
    /// </remarks>
    [Fact]
    public async Task AddApplication_FetchesItsOwnTokenPerItem()
    {
        var harness = Harness();

        await harness.AddApplicationAsync(Information());

        harness.AddApplicationQueue.Add(Information());

        await harness.WaitForRequests(10, Ct);

        Assert.Equal(2, harness.Handler.Paths.Count(x => x == IntegrationServiceHarness.TokenPath));
    }

    /// <remarks>
    /// The field is assigned in the constructor and read nowhere. blueprint.ui's application list is
    /// written from blueprint's own rows, which already existed before the push, so the absence is
    /// invisible: the row says the application is there and only Player knows whether it is.
    /// </remarks>
    [Fact]
    public async Task AddApplication_TellsNobodyAnything()
    {
        var harness = Harness();

        await harness.AddApplicationAsync(Information());
        await harness.WaitForRequests(5, Ct);

        Assert.Empty(harness.Hub.Sends);
    }

    /// <remarks>
    /// The <c>Run</c> loop catches around the <c>Take</c> and each item is processed on its own thread,
    /// so a failed add cannot wedge the worker. The one property this service shares with the other two
    /// that is unambiguously right.
    /// </remarks>
    [Fact]
    public async Task AddApplication_AfterAFailedItem_KeepsProcessingTheQueue()
    {
        var other = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
        var harness = Harness(x => x
            .Throws($"player/api/views/{other}/applications")
            .AnswersJson(
                $"player/api/views/{ViewId}/applications", ApplicationJson, HttpStatusCode.Created));

        await harness.AddApplicationAsync(Information(viewId: other));
        await harness.WaitForApplicationLog(x => x.Exception is not null, Ct);

        harness.AddApplicationQueue.Add(Information());

        await harness.WaitForRequests(9, Ct);

        Assert.Contains($"player/api/teams/{TeamId}/application-instances", harness.SiblingPaths);
    }

    /// <remarks>
    /// The context the worker resolves is never read and never written. Blueprint's
    /// <c>PlayerApplicationEntity</c> row was created by the request that queued this, so an add that
    /// fails leaves a row claiming an application Player has never heard of - the same disagreement
    /// <c>JoinService</c> leaves behind, and undetectable for the same reason.
    /// </remarks>
    [Fact]
    public async Task AddApplication_WritesNothingToBlueprint()
    {
        var harness = Harness();

        await using var context = NewContext();

        var applications = await context.PlayerApplications.CountAsync(Ct);
        var users = await context.Users.CountAsync(Ct);

        await harness.AddApplicationAsync(Information());
        await harness.WaitForRequests(5, Ct);

        await using var after = NewContext();

        Assert.Equal(applications, await after.PlayerApplications.CountAsync(Ct));
        Assert.Equal(users, await after.Users.CountAsync(Ct));
    }

    // ---------------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------------

    private static readonly Guid ViewId = Guid.Parse("88888888-8888-8888-8888-888888888888");
    private static readonly Guid TeamId = Guid.Parse("99999999-9999-9999-9999-999999999999");
    private static readonly Guid ApplicationId = Guid.Parse("12121212-1212-1212-1212-121212121212");

    /// <summary>
    /// A queue item as <c>PlayerService.PushApplication</c> builds one: a Player
    /// <c>Application</c> already filled in, a blueprint team id, and a display order.
    /// </summary>
    private static AddApplicationInformation Information(
        Guid? viewId = null, int displayOrder = 3) =>
        new()
        {
            Application = new Application
            {
                Name = "Console",
                Url = new Uri("http://console.example/"),
                Icon = "star.png",
                Embeddable = true,
                LoadInBackground = false,
                ViewId = viewId ?? ViewId
            },
            TeamId = TeamId,
            DisplayOrder = displayOrder
        };

    /// <remarks>
    /// <paramref name="first"/> is applied before the general rules, because rules are matched in the
    /// order they were added: a test that wants one route to fail, or to answer differently, has to
    /// register it first.
    /// </remarks>
    private QueueWorkerHarness Harness(Func<TestHttpHandler, TestHttpHandler> first = null) =>
        new(Session, Everything(first));

    /// <summary>The identity provider and the two routes an add makes, and nothing else.</summary>
    private static TestHttpHandler Everything(Func<TestHttpHandler, TestHttpHandler> first = null) =>
        QueueWorkerHarness.IdentityProvider(first is null ? new TestHttpHandler() : first(new TestHttpHandler()))
            .AnswersJson("player/api/views/*/applications", ApplicationJson, HttpStatusCode.Created)
            .AnswersJson("player/api/teams/*/application-instances", InstanceJson, HttpStatusCode.Created);

    private static readonly string ApplicationJson =
        $$"""{"id":"{{ApplicationId}}","name":"Console","viewId":"{{ViewId}}"}""";

    private static readonly string InstanceJson =
        $$"""{"id":"{{ApplicationId}}","teamId":"{{TeamId}}","applicationId":"{{ApplicationId}}"}""";

    /// <summary>The request body as a property bag, which is how these tests read what went out.</summary>
    private static Dictionary<string, JsonElement> Body(string body) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(body);
}
