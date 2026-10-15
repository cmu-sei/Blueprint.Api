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
using Blueprint.Api.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Player.Api.Client;
using Xunit;

namespace Blueprint.Api.Tests.Services;

/// <summary><c>AddApplicationService</c> - the worker that puts one application in front of one Player team
/// after somebody pushes it from blueprint's application list.</summary>
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

    /// <summary>The application is posted under the view on the queued application; the worker loads no
    /// MSEL.</summary>
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

    /// <summary>The instance names the application id Player answered with, not the queued one.</summary>
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

    /// <summary>Add application when player answers without an id uses the all zeros guid.</summary>
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

    /// <summary>One <c>try</c> covers both calls, so a failed create makes no instance request.</summary>
    [Fact]
    public async Task AddApplication_WhenTheApplicationCannotBeCreated_NeverCreatesTheInstance()
    {
        var harness = Harness(x => x.Throws($"player/api/views/{ViewId}/applications"));

        await harness.AddApplicationAsync(Information());
        await harness.WaitForApplicationLog(x => x.Exception is not null, Ct);

        Assert.DoesNotContain(
            $"player/api/teams/{TeamId}/application-instances", harness.SiblingPaths);
    }

    /// <summary>Add application when the application cannot be created blames building the client.</summary>
    [Fact]
    public async Task AddApplication_WhenTheApplicationCannotBeCreated_BlamesBuildingTheClient()
    {
        var harness = Harness(x => x.Throws($"player/api/views/{ViewId}/applications"));

        await harness.AddApplicationAsync(Information());

        var logged = await harness.WaitForApplicationLog(x => x.Exception is not null, Ct);

        Assert.Equal("Player - get API client Adding Application", logged.Message);
    }

    /// <summary>A failed instance request is logged with the same step as a failed create.</summary>
    [Fact]
    public async Task AddApplication_WhenTheInstanceCannotBeCreated_IsReportedIdentically()
    {
        var harness = Harness(x => x.Throws($"player/api/teams/{TeamId}/application-instances"));

        await harness.AddApplicationAsync(Information());

        var logged = await harness.WaitForApplicationLog(x => x.Exception is not null, Ct);

        Assert.Equal("Player - get API client Adding Application", logged.Message);
        Assert.Contains($"player/api/views/{ViewId}/applications", harness.SiblingPaths);
    }

    /// <summary>Add application when a step fails logs no application no team and no view.</summary>
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

    /// <summary>When no token can be fetched the log names the initial step, <c>Begin processing</c>.</summary>
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

    /// <summary>Each queue item fetches its own token.</summary>
    [Fact]
    public async Task AddApplication_FetchesItsOwnTokenPerItem()
    {
        var harness = Harness();

        await harness.AddApplicationAsync(Information());

        harness.AddApplicationQueue.Add(Information());

        await harness.WaitForRequests(10, Ct);

        Assert.Equal(2, harness.Handler.Paths.Count(x => x == IntegrationServiceHarness.TokenPath));
    }

    /// <summary>The worker sends nothing over the hub on any path.</summary>
    [Fact]
    public async Task AddApplication_TellsNobodyAnything()
    {
        var harness = Harness();

        await harness.AddApplicationAsync(Information());
        await harness.WaitForRequests(5, Ct);

        Assert.Empty(harness.Hub.Sent(ViewId, TeamId));
    }

    /// <summary>A failed item does not stop the worker taking the next one.</summary>
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

    /// <summary>The worker neither reads nor writes blueprint's database.</summary>
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
    private QueueWorkerHarness Harness(Func<SiblingApiHandler, SiblingApiHandler> first = null) =>
        new(Session, Everything(first));

    /// <summary>The identity provider and the two routes an add makes, and nothing else.</summary>
    private static SiblingApiHandler Everything(Func<SiblingApiHandler, SiblingApiHandler> first = null) =>
        QueueWorkerHarness.IdentityProvider(first is null ? new SiblingApiHandler() : first(new SiblingApiHandler()))
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
