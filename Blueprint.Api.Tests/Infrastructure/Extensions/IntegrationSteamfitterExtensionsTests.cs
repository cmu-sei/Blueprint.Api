// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using Blueprint.Api.Data.Enumerations;
using Blueprint.Api.Data.Models;
using Blueprint.Api.Infrastructure.Extensions;
using Blueprint.Api.Tests.Support;
using Steamfitter.Api.Client;
using Xunit;

// Steamfitter's generated client declares a Task of its own, which is the type these methods pass to one
// another to chain one scenario task onto the last. Production reaches for the STT alias to keep the two
// apart; a test file that returns Task from every method is better served by aliasing the other one.
using Task = System.Threading.Tasks.Task;
using ScenarioTask = Steamfitter.Api.Client.Task;

namespace Blueprint.Api.Tests.Infrastructure.Extensions;

/// <summary><c>IntegrationSteamfitterExtensions</c> - the six calls blueprint makes to steamfitter.api when
/// a MSEL is pushed or pulled, and the scenario tasks it builds out of the timeline.</summary>
public class IntegrationSteamfitterExtensionsTests
{
    // ---------------------------------------------------------------------------------------------
    // GetSteamfitterApiClient
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task GetSteamfitterApiClient_BuildsAClientCarryingTheTokenAndTheApiUrl()
    {
        var handler = new SiblingApiHandler().Answers($"api/scenarios/{ScenarioId}", HttpStatusCode.NoContent);
        var client = IntegrationSteamfitterExtensions.GetSteamfitterApiClient(
            handler.AsFactory(), "http://steamfitter.example/", await Tokens.Bearer());

        await IntegrationSteamfitterExtensions.PullFromSteamfitterAsync(ScenarioId, client, Ct);

        var sent = Assert.Single(handler.Sent);

        Assert.Equal($"api/scenarios/{ScenarioId}", sent.Path);
        Assert.Equal(Tokens.Header, sent.Authorization);
    }

    // ---------------------------------------------------------------------------------------------
    // PullFromSteamfitterAsync
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task PullFromSteamfitter_DeletesTheScenario()
    {
        var handler = new SiblingApiHandler().Answers($"api/scenarios/{ScenarioId}", HttpStatusCode.NoContent);

        await IntegrationSteamfitterExtensions.PullFromSteamfitterAsync(ScenarioId, Client(handler), Ct);

        var sent = Assert.Single(handler.Sent);

        Assert.Equal(HttpMethod.Delete, sent.Method);
        Assert.Equal($"api/scenarios/{ScenarioId}", sent.Path);
    }

    /// <summary>A refused scenario delete is swallowed by an empty <c>catch</c>.</summary>
    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task PullFromSteamfitter_SwallowsARefusal(HttpStatusCode status)
    {
        var handler = new SiblingApiHandler().Answers($"api/scenarios/{ScenarioId}", status);

        await IntegrationSteamfitterExtensions.PullFromSteamfitterAsync(ScenarioId, Client(handler), Ct);

        Assert.Single(handler.Sent);
    }

    [Fact]
    public async Task PullFromSteamfitter_SwallowsAnUnreachableSteamfitter()
    {
        var handler = new SiblingApiHandler().Throws($"api/scenarios/{ScenarioId}");

        await IntegrationSteamfitterExtensions.PullFromSteamfitterAsync(ScenarioId, Client(handler), Ct);

        Assert.Single(handler.Sent);
    }

    // ---------------------------------------------------------------------------------------------
    // CreateScenarioAsync
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task CreateScenario_PostsTheMselsIdNameAndDescription()
    {
        var msel = Msel();
        var handler = Scenarios();

        await IntegrationSteamfitterExtensions.CreateScenarioAsync(msel, Client(handler), null, Ct);

        var sent = Assert.Single(handler.Sent);

        Assert.Equal(HttpMethod.Post, sent.Method);
        Assert.Equal("api/scenarios", sent.Path);

        var form = Body(sent.Body);

        Assert.Equal(ScenarioId.ToString(), form["id"].GetString());
        Assert.Equal(msel.Name, form["name"].GetString());
        Assert.Equal(msel.Description, form["description"].GetString());
    }

    [Fact]
    public async Task CreateScenario_AlwaysAsksForAnActiveScenario()
    {
        var handler = Scenarios();

        await IntegrationSteamfitterExtensions.CreateScenarioAsync(Msel(), Client(handler), null, Ct);

        Assert.Equal("Active", Body(Assert.Single(handler.Sent).Body)["status"].GetString());
    }

    /// <summary>The scenario carries the Player view id and, as its view name, the MSEL's name.</summary>
    [Fact]
    public async Task CreateScenario_SendsThePlayerViewIdAndTheMselNameAsTheView()
    {
        var msel = Msel();
        var handler = Scenarios();

        await IntegrationSteamfitterExtensions.CreateScenarioAsync(msel, Client(handler), null, Ct);

        var form = Body(Assert.Single(handler.Sent).Body);

        Assert.Equal(ViewId.ToString(), form["viewId"].GetString());
        Assert.Equal(msel.Name, form["view"].GetString());
    }

    /// <remarks>
    /// A MSEL whose start time has not arrived is scheduled for that time, and its end is the duration
    /// after it.
    /// </remarks>
    [Fact]
    public async Task CreateScenario_ForAFutureStart_UsesTheMselsOwnStartAndDuration()
    {
        var start = DateTime.UtcNow.AddDays(7);
        var msel = Msel();
        msel.StartTime = start;
        msel.DurationSeconds = 3600;

        var handler = Scenarios();

        await IntegrationSteamfitterExtensions.CreateScenarioAsync(msel, Client(handler), null, Ct);

        var form = Body(Assert.Single(handler.Sent).Body);

        Assert.Equal(start, form["startDate"].GetDateTimeOffset().UtcDateTime, TimeSpan.FromSeconds(1));
        Assert.Equal(
            start.AddSeconds(3600),
            form["endDate"].GetDateTimeOffset().UtcDateTime,
            TimeSpan.FromSeconds(1));
    }

    /// <summary>A start time already past is clamped to now and the scenario keeps its full duration.</summary>
    [Fact]
    public async Task CreateScenario_ForAStartAlreadyPast_ClampsItToNow()
    {
        var before = DateTime.UtcNow;
        var msel = Msel();
        msel.StartTime = DateTime.UtcNow.AddDays(-7);
        msel.DurationSeconds = 60;

        var handler = Scenarios();

        await IntegrationSteamfitterExtensions.CreateScenarioAsync(msel, Client(handler), null, Ct);

        var form = Body(Assert.Single(handler.Sent).Body);
        var startDate = form["startDate"].GetDateTimeOffset().UtcDateTime;

        Assert.InRange(startDate, before, DateTime.UtcNow);
        Assert.Equal(
            startDate.AddSeconds(60),
            form["endDate"].GetDateTimeOffset().UtcDateTime,
            TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task CreateScenario_ReturnsWhatSteamfitterAnswered()
    {
        var answered = Guid.Parse("44444444-4444-4444-4444-444444444444");
        var handler = new SiblingApiHandler().AnswersJson(
            "api/scenarios",
            $$"""{"id":"{{answered}}","name":"answered","status":"active"}""",
            HttpStatusCode.Created);

        var scenario = await IntegrationSteamfitterExtensions.CreateScenarioAsync(
            Msel(), Client(handler), null, Ct);

        Assert.Equal(answered, scenario.Id);
    }

    // ---------------------------------------------------------------------------------------------
    // CreateScenarioMembershipsAsync
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task CreateScenarioMemberships_ReadsTheRoleListThenPostsOneMembershipPerUser()
    {
        var msel = Msel();
        var ada = Guid.NewGuid();
        var grace = Guid.NewGuid();
        msel.UserMselRoles.Add(RoleFor(ada, "Manager"));
        msel.UserMselRoles.Add(RoleFor(grace, "Observer"));

        var handler = Memberships();

        await IntegrationSteamfitterExtensions.CreateScenarioMembershipsAsync(msel, Client(handler), null, Ct);

        Assert.Equal("api/scenario-roles", handler.Sent[0].Path);

        var posted = handler.Sent
            .Skip(1)
            .Select(x => Body(x.Body))
            .ToDictionary(x => x["userId"].GetString(), x => x["roleId"].GetString());

        Assert.Equal(ManagerRoleId.ToString(), posted[ada.ToString()]);
        Assert.Equal(ObserverRoleId.ToString(), posted[grace.ToString()]);
    }

    [Fact]
    public async Task CreateScenarioMemberships_PostsUnderTheScenario()
    {
        var msel = Msel();
        msel.UserMselRoles.Add(RoleFor(Guid.NewGuid(), "Manager"));

        var handler = Memberships();

        await IntegrationSteamfitterExtensions.CreateScenarioMembershipsAsync(msel, Client(handler), null, Ct);

        Assert.Equal($"api/scenarios/{ScenarioId}/memberships", handler.Sent[^1].Path);
        Assert.Equal(ScenarioId.ToString(), Body(handler.Sent[^1].Body)["scenarioId"].GetString());
    }

    [Fact]
    public async Task CreateScenarioMemberships_SkipsARoleWithNoSteamfitterName()
    {
        var msel = Msel();
        msel.UserMselRoles.Add(RoleFor(Guid.NewGuid(), null));
        msel.UserMselRoles.Add(RoleFor(Guid.NewGuid(), string.Empty));

        var handler = Memberships();

        await IntegrationSteamfitterExtensions.CreateScenarioMembershipsAsync(msel, Client(handler), null, Ct);

        Assert.Equal("api/scenario-roles", Assert.Single(handler.Paths));
    }

    /// <summary>A role name Steamfitter does not have creates no membership, with nothing logged.</summary>
    [Fact]
    public async Task CreateScenarioMemberships_SkipsARoleNameSteamfitterDoesNotHave()
    {
        var msel = Msel();
        msel.UserMselRoles.Add(RoleFor(Guid.NewGuid(), "NoSuchRole"));

        var handler = Memberships();

        await IntegrationSteamfitterExtensions.CreateScenarioMembershipsAsync(msel, Client(handler), null, Ct);

        Assert.Equal("api/scenario-roles", Assert.Single(handler.Paths));
    }

    /// <summary>A user with two roles gets one membership, with one of the two roles.</summary>
    [Fact]
    public async Task CreateScenarioMemberships_ForAUserWithTwoRoles_PostsOneMembershipWithOneOfThem()
    {
        var msel = Msel();
        var ada = Guid.NewGuid();
        msel.UserMselRoles.Add(RoleFor(ada, "Manager"));
        msel.UserMselRoles.Add(RoleFor(ada, "Observer"));

        var handler = Memberships();

        await IntegrationSteamfitterExtensions.CreateScenarioMembershipsAsync(msel, Client(handler), null, Ct);

        var membership = Body(Assert.Single(handler.Sent.Skip(1).ToList()).Body);

        Assert.Equal(ada.ToString(), membership["userId"].GetString());
        Assert.Contains(
            membership["roleId"].GetString(),
            new[] { ManagerRoleId.ToString(), ObserverRoleId.ToString() });
    }

    /// <summary>A refused membership is swallowed and the next is still sent.</summary>
    [Fact]
    public async Task CreateScenarioMemberships_SwallowsAFailedMembershipAndCarriesOn()
    {
        var msel = Msel();
        msel.UserMselRoles.Add(RoleFor(Guid.NewGuid(), "Manager"));
        msel.UserMselRoles.Add(RoleFor(Guid.NewGuid(), "Observer"));

        var handler = new SiblingApiHandler()
            .AnswersJson("api/scenario-roles", RolesJson)
            .Answers($"api/scenarios/{ScenarioId}/memberships", HttpStatusCode.Conflict);

        await IntegrationSteamfitterExtensions.CreateScenarioMembershipsAsync(msel, Client(handler), null, Ct);

        Assert.Equal(2, handler.Sent.Count(x => x.Path.EndsWith("memberships")));
    }

    /// <summary>Create scenario memberships with no scenario on the MSEL throws after fetching the roles.</summary>
    [Fact]
    public async Task CreateScenarioMemberships_WithNoScenarioOnTheMsel_ThrowsAfterFetchingTheRoles()
    {
        var msel = Msel();
        msel.SteamfitterScenarioId = null;
        msel.UserMselRoles.Add(RoleFor(Guid.NewGuid(), "Manager"));

        var handler = Memberships();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            IntegrationSteamfitterExtensions.CreateScenarioMembershipsAsync(msel, Client(handler), null, Ct));

        Assert.Equal("api/scenario-roles", Assert.Single(handler.Paths));
    }

    // ---------------------------------------------------------------------------------------------
    // CreateScenarioTasksAsync - the name
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task CreateScenarioTasks_NamesTheTaskWithThePaddedMoveAndGroup()
    {
        var handler = Tasks();

        await CreateScenarioTask(handler, Task_("Send the email"), moveNumber: 2, groupNumber: 7);

        Assert.Equal("02-07 Send the email", Body(Assert.Single(handler.Sent).Body)["name"].GetString());
    }

    /// <summary>Create scenario tasks numbers above ninety nine wrap to their last two digits.</summary>
    [Theory]
    [InlineData(0, "00")]
    [InlineData(1, "01")]
    [InlineData(9, "09")]
    [InlineData(10, "10")]
    [InlineData(99, "99")]
    [InlineData(100, "00")]
    [InlineData(1234, "34")]
    public async Task CreateScenarioTasks_NumbersAboveNinetyNineWrapToTheirLastTwoDigits(
        int number, string expected)
    {
        var handler = Tasks();

        await CreateScenarioTask(handler, Task_("named"), moveNumber: number, groupNumber: number);

        Assert.Equal(
            $"{expected}-{expected} named",
            Body(Assert.Single(handler.Sent).Body)["name"].GetString());
    }

    /// <summary>Any negative move or group number is named <c>-1</c>.</summary>
    [Theory]
    [InlineData(-1)]
    [InlineData(-5)]
    public async Task CreateScenarioTasks_AnyNegativeNumberBecomesMinusOne(int number)
    {
        var handler = Tasks();

        await CreateScenarioTask(handler, Task_("named"), moveNumber: number, groupNumber: number);

        Assert.Equal("-1--1 named", Body(Assert.Single(handler.Sent).Body)["name"].GetString());
    }

    // ---------------------------------------------------------------------------------------------
    // CreateScenarioTasksAsync - the task types
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task CreateScenarioTasks_ForANotification_BuildsThePlayerNotificationCall()
    {
        var entity = Task_("notify", SteamfitterIntegrationType.Notification,
            new Dictionary<string, string> { ["notificationText"] = "Stand by" });

        var handler = Tasks();

        await CreateScenarioTask(handler, entity);

        var form = Body(Assert.Single(handler.Sent).Body);
        var parameters = form["actionParameters"];

        Assert.Equal("Http_post", form["action"].GetString());
        Assert.Equal("http", form["apiUrl"].GetString());
        Assert.Equal($"{PlayerApiUrl}views/{ViewId}/notifications", parameters.GetProperty("Url").GetString());
        Assert.Equal("""{"text": "Stand by"}""", parameters.GetProperty("Body").GetString());
        Assert.Equal("Message was sent", form["expectedOutput"].GetString());
    }

    /// <summary>Create scenario tasks for a notification does not escape the text.</summary>
    [Fact]
    public async Task CreateScenarioTasks_ForANotification_DoesNotEscapeTheText()
    {
        var entity = Task_("notify", SteamfitterIntegrationType.Notification,
            new Dictionary<string, string> { ["notificationText"] = @"He said ""go""" });

        var handler = Tasks();

        await CreateScenarioTask(handler, entity);

        var body = Body(Assert.Single(handler.Sent).Body)["actionParameters"]
            .GetProperty("Body").GetString();

        Assert.Equal("""{"text": "He said "go""}""", body);

        Assert.IsAssignableFrom<JsonException>(Record.Exception(() => JsonDocument.Parse(body)));
    }

    [Fact]
    public async Task CreateScenarioTasks_ForASituationUpdate_BuildsTheCiteSituationCall()
    {
        var entity = Task_("situation", SteamfitterIntegrationType.SituationUpdate,
            new Dictionary<string, string>
            {
                ["situationTime"] = "2026-01-01T00:00:00Z",
                ["situationDescription"] = "All quiet"
            });

        var handler = Tasks();

        await CreateScenarioTask(handler, entity);

        var form = Body(Assert.Single(handler.Sent).Body);
        var parameters = form["actionParameters"];

        Assert.Equal("Http_put", form["action"].GetString());
        Assert.Equal(
            $"{CiteApiUrl}evaluations/{EvaluationId}/situation",
            parameters.GetProperty("Url").GetString());
        Assert.Equal(
            """{"situationTime": "2026-01-01T00:00:00Z", "situationDescription": "All quiet"}""",
            parameters.GetProperty("Body").GetString());
        Assert.Equal($"\"id\":\"{EvaluationId}\"", form["expectedOutput"].GetString());
    }

    /// <summary>An email task is sent with <c>apiUrl</c> <c>stackstorm</c> and its parameters as
    /// stored.</summary>
    [Fact]
    public async Task CreateScenarioTasks_ForAnEmail_AsksSteamfitterForStackstorm()
    {
        var entity = Task_("email", SteamfitterIntegrationType.Email,
            new Dictionary<string, string> { ["To"] = "someone@example.test" });

        var handler = Tasks();

        await CreateScenarioTask(handler, entity);

        var form = Body(Assert.Single(handler.Sent).Body);

        Assert.Equal("Send_email", form["action"].GetString());
        Assert.Equal("stackstorm", form["apiUrl"].GetString());
        Assert.Equal(
            "someone@example.test",
            form["actionParameters"].GetProperty("To").GetString());
    }

    /// <summary>A raw HTTP task type is translated and its parameters sent as stored.</summary>
    [Theory]
    [InlineData(SteamfitterIntegrationType.http_get, "Http_get")]
    [InlineData(SteamfitterIntegrationType.http_put, "Http_put")]
    [InlineData(SteamfitterIntegrationType.http_delete, "Http_delete")]
    public async Task CreateScenarioTasks_ForARawHttpType_TranslatesTheActionAndTouchesNothingElse(
        SteamfitterIntegrationType type, string expectedAction)
    {
        var entity = Task_("raw", type, actionParameters: null);
        var handler = Tasks();

        await CreateScenarioTask(handler, entity);

        var form = Body(Assert.Single(handler.Sent).Body);

        Assert.Equal(expectedAction, form["action"].GetString());
        Assert.Equal("http", form["apiUrl"].GetString());
        Assert.Equal(JsonValueKind.Null, form["actionParameters"].ValueKind);
    }

    /// <summary>A task type the switch does not handle is sent as an HTTP POST.</summary>
    [Fact]
    public async Task CreateScenarioTasks_ForATypeTheSwitchDoesNotHandle_FallsBackToHttpPost()
    {
        var entity = Task_("unhandled", SteamfitterIntegrationType.http_post, actionParameters: null);
        var handler = Tasks();

        await CreateScenarioTask(handler, entity);

        var form = Body(Assert.Single(handler.Sent).Body);

        Assert.Equal("Http_post", form["action"].GetString());
        Assert.Equal("http", form["apiUrl"].GetString());
    }

    /// <summary>Create scenario tasks with no action parameters throws for the types that write into them.</summary>
    [Theory]
    [InlineData(SteamfitterIntegrationType.Notification)]
    [InlineData(SteamfitterIntegrationType.SituationUpdate)]
    public async Task CreateScenarioTasks_WithNoActionParameters_ThrowsForTheTypesThatWriteIntoThem(
        SteamfitterIntegrationType type)
    {
        var entity = Task_("no parameters", type, actionParameters: null);
        var handler = Tasks();

        await Assert.ThrowsAsync<NullReferenceException>(() => CreateScenarioTask(handler, entity));
    }

    /// <summary>Create scenario tasks for a notification with no text throws naming only the key.</summary>
    [Fact]
    public async Task CreateScenarioTasks_ForANotificationWithNoText_ThrowsNamingOnlyTheKey()
    {
        var entity = Task_("notify", SteamfitterIntegrationType.Notification,
            new Dictionary<string, string>());

        var handler = Tasks();

        var thrown = await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            CreateScenarioTask(handler, entity));

        Assert.Contains("notificationText", thrown.Message);
    }

    // ---------------------------------------------------------------------------------------------
    // CreateScenarioTasksAsync - the rest of the form, and the entity
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task CreateScenarioTasks_CopiesTheEntitysTimingAndFlags()
    {
        var entity = Task_("timed", SteamfitterIntegrationType.http_get, actionParameters: null);
        var handler = Tasks();

        await CreateScenarioTask(handler, entity);

        var form = Body(Assert.Single(handler.Sent).Body);

        Assert.Equal(entity.ExpirationSeconds, form["expirationSeconds"].GetInt32());
        Assert.Equal(entity.DelaySeconds, form["delaySeconds"].GetInt32());
        Assert.Equal(entity.IntervalSeconds, form["intervalSeconds"].GetInt32());
        Assert.Equal(entity.Iterations, form["iterations"].GetInt32());
        Assert.Equal(entity.UserExecutable, form["userExecutable"].GetBoolean());
        Assert.Equal(entity.Repeatable, form["repeatable"].GetBoolean());
    }

    /// <summary>Every task is executable and terminated by its iteration count.</summary>
    [Fact]
    public async Task CreateScenarioTasks_AlwaysSendsAnExecutableTaskTerminatedByIterationCount()
    {
        var handler = Tasks();

        await CreateScenarioTask(handler, Task_("flags", SteamfitterIntegrationType.http_get, null));

        var form = Body(Assert.Single(handler.Sent).Body);

        Assert.True(form["executable"].GetBoolean());
        Assert.Equal("IterationCount", form["iterationTermination"].GetString());
    }

    [Fact]
    public async Task CreateScenarioTasks_WithNoTriggerTask_AsksForAManualTrigger()
    {
        var handler = Tasks();

        await CreateScenarioTask(handler, Task_("first", SteamfitterIntegrationType.http_get, null));

        var form = Body(Assert.Single(handler.Sent).Body);

        Assert.Equal("Manual", form["triggerCondition"].GetString());
        Assert.Equal(JsonValueKind.Null, form["triggerTaskId"].ValueKind);
    }

    /// <remarks>
    /// This is how the timeline becomes a chain: <c>IntegrationService</c> feeds each task's answer back in
    /// as the next one's trigger, so Steamfitter runs them in order without knowing about the MSEL.
    /// </remarks>
    [Fact]
    public async Task CreateScenarioTasks_WithATriggerTask_ChainsOntoItOnCompletion()
    {
        var previous = new ScenarioTask { Id = TriggerTaskId, Name = "previous" };
        var handler = Tasks();

        await CreateScenarioTask(
            handler, Task_("second", SteamfitterIntegrationType.http_get, null), triggerTask: previous);

        var form = Body(Assert.Single(handler.Sent).Body);

        Assert.Equal("Completion", form["triggerCondition"].GetString());
        Assert.Equal(TriggerTaskId.ToString(), form["triggerTaskId"].GetString());
    }

    [Fact]
    public async Task CreateScenarioTasks_ReturnsTheTaskSteamfitterAnswered()
    {
        var answered = Guid.Parse("55555555-5555-5555-5555-555555555555");
        var handler = new SiblingApiHandler().AnswersJson(
            "api/tasks", $$"""{"id":"{{answered}}","name":"answered"}""", HttpStatusCode.Created);

        var created = await CreateScenarioTask(
            handler, Task_("returns", SteamfitterIntegrationType.http_get, null));

        Assert.Equal(answered, created.Id);
    }

    /// <summary>Create scenario tasks mutates the entity it was given.</summary>
    [Fact]
    public async Task CreateScenarioTasks_MutatesTheEntityItWasGiven()
    {
        var entity = Task_("notify", SteamfitterIntegrationType.Notification,
            new Dictionary<string, string> { ["notificationText"] = "Stand by" });

        Assert.False(entity.ActionParameters.ContainsKey("Url"));
        Assert.Equal("expected", entity.ExpectedOutput);

        await CreateScenarioTask(Tasks(), entity);

        Assert.Equal($"{PlayerApiUrl}views/{ViewId}/notifications", entity.ActionParameters["Url"]);
        Assert.Equal("""{"text": "Stand by"}""", entity.ActionParameters["Body"]);
        Assert.Equal("Message was sent", entity.ExpectedOutput);
    }

    /// <summary>The <c>galleryApiUrl</c> argument is not read.</summary>
    [Fact]
    public async Task CreateScenarioTasks_NeverReadsTheGalleryUrl()
    {
        var entity = Task_("notify", SteamfitterIntegrationType.Notification,
            new Dictionary<string, string> { ["notificationText"] = "Stand by" });

        var handler = Tasks();

        await IntegrationSteamfitterExtensions.CreateScenarioTasksAsync(
            Msel(), entity, Client(handler), 1, 1, PlayerApiUrl, CiteApiUrl,
            galleryApiUrl: "!! not a url !!", triggerTask: null, ct: Ct);

        var parameters = Body(Assert.Single(handler.Sent).Body)["actionParameters"];

        Assert.Equal($"{PlayerApiUrl}views/{ViewId}/notifications", parameters.GetProperty("Url").GetString());
    }

    [Fact]
    public async Task CreateScenarioTasks_SendsTheScenarioInTheBodyRatherThanTheRoute()
    {
        var handler = Tasks();

        await CreateScenarioTask(handler, Task_("scoped", SteamfitterIntegrationType.http_get, null));

        var sent = Assert.Single(handler.Sent);

        Assert.Equal("api/tasks", sent.Path);
        Assert.Equal(ScenarioId.ToString(), Body(sent.Body)["scenarioId"].GetString());
    }

    // ---------------------------------------------------------------------------------------------
    // CreateNextMoveTasksAsync
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task CreateNextMoveTasks_ForCiteOnly_CreatesTheCiteMoveChange()
    {
        var msel = Msel();
        msel.UseCite = true;

        var handler = Tasks();

        await IntegrationSteamfitterExtensions.CreateNextMoveTasksAsync(
            msel, Client(handler), 3, CiteApiUrl, GalleryApiUrl, null, Ct);

        var form = Body(Assert.Single(handler.Sent).Body);

        Assert.Equal("03-00 CITE Move Change", form["name"].GetString());
        Assert.Equal("Change the move on the CITE Evaluation to 3", form["description"].GetString());
        Assert.Equal("Http_put", form["action"].GetString());
        Assert.Equal(
            $"{CiteApiUrl}evaluations/{EvaluationId}/move/3",
            form["actionParameters"].GetProperty("Url").GetString());
        Assert.Equal("\"currentMoveNumber\":3", form["expectedOutput"].GetString());
    }

    [Fact]
    public async Task CreateNextMoveTasks_ForGalleryOnly_CreatesTheGalleryMoveChange()
    {
        var msel = Msel();
        msel.UseGallery = true;

        var handler = Tasks();

        await IntegrationSteamfitterExtensions.CreateNextMoveTasksAsync(
            msel, Client(handler), 3, CiteApiUrl, GalleryApiUrl, null, Ct);

        var form = Body(Assert.Single(handler.Sent).Body);

        Assert.Equal("03-00 Gallery Move Change", form["name"].GetString());
        Assert.Equal(
            $"{GalleryApiUrl}exhibits/{ExhibitId}/move/3/inject/0",
            form["actionParameters"].GetProperty("Url").GetString());
        Assert.Equal("\"currentMove\":3", form["expectedOutput"].GetString());
    }

    /// <summary>With both integrations, each move-change task is sent with its own url although the two share
    /// one dictionary.</summary>
    [Fact]
    public async Task CreateNextMoveTasks_ForBoth_SendsTheRightUrlToEachDespiteSharingOneDictionary()
    {
        var msel = Msel();
        msel.UseCite = true;
        msel.UseGallery = true;

        var handler = Tasks();

        await IntegrationSteamfitterExtensions.CreateNextMoveTasksAsync(
            msel, Client(handler), 3, CiteApiUrl, GalleryApiUrl, null, Ct);

        Assert.Equal(2, handler.Sent.Count);

        var urls = handler.Sent
            .Select(x => Body(x.Body)["actionParameters"].GetProperty("Url").GetString())
            .ToList();

        Assert.Equal($"{CiteApiUrl}evaluations/{EvaluationId}/move/3", urls[0]);
        Assert.Equal($"{GalleryApiUrl}exhibits/{ExhibitId}/move/3/inject/0", urls[1]);
    }

    /// <remarks>
    /// The second task is chained onto the first, so with both integrations in use the Gallery change waits
    /// for the CITE change to complete. The order is the order of the two <c>if</c> blocks, so CITE always
    /// leads.
    /// </remarks>
    [Fact]
    public async Task CreateNextMoveTasks_ForBoth_ChainsTheGalleryChangeOntoTheCiteOne()
    {
        var msel = Msel();
        msel.UseCite = true;
        msel.UseGallery = true;

        var first = Guid.Parse("66666666-6666-6666-6666-666666666666");
        var handler = new SiblingApiHandler()
            .AnswersJson("api/tasks", $$"""{"id":"{{first}}","name":"cite"}""", HttpStatusCode.Created, once: true)
            .AnswersJson("api/tasks", TaskJson, HttpStatusCode.Created);

        var last = await IntegrationSteamfitterExtensions.CreateNextMoveTasksAsync(
            msel, Client(handler), 3, CiteApiUrl, GalleryApiUrl, null, Ct);

        var gallery = Body(handler.Sent[1].Body);

        Assert.Equal("Completion", gallery["triggerCondition"].GetString());
        Assert.Equal(first.ToString(), gallery["triggerTaskId"].GetString());
        Assert.Equal(TaskId, last.Id);
    }

    /// <remarks>
    /// A MSEL using neither integration gets no move-change tasks and the caller's trigger comes back
    /// untouched, so the chain continues from wherever it was.
    /// </remarks>
    [Fact]
    public async Task CreateNextMoveTasks_ForNeither_SendsNothingAndReturnsTheTriggerUnchanged()
    {
        var handler = new SiblingApiHandler();
        var previous = new ScenarioTask { Id = TriggerTaskId, Name = "previous" };

        var returned = await IntegrationSteamfitterExtensions.CreateNextMoveTasksAsync(
            Msel(), Client(handler), 3, CiteApiUrl, GalleryApiUrl, previous, Ct);

        Assert.Empty(handler.Sent);
        Assert.Same(previous, returned);
    }

    /// <summary>Move-change tasks use fixed timing: two minutes to expire, no delay, no interval, one
    /// iteration.</summary>
    [Fact]
    public async Task CreateNextMoveTasks_UsesItsOwnFixedTiming()
    {
        var msel = Msel();
        msel.UseCite = true;

        var handler = Tasks();

        await IntegrationSteamfitterExtensions.CreateNextMoveTasksAsync(
            msel, Client(handler), 3, CiteApiUrl, GalleryApiUrl, null, Ct);

        var form = Body(Assert.Single(handler.Sent).Body);

        Assert.Equal(120, form["expirationSeconds"].GetInt32());
        Assert.Equal(0, form["delaySeconds"].GetInt32());
        Assert.Equal(0, form["intervalSeconds"].GetInt32());
        Assert.Equal(1, form["iterations"].GetInt32());
    }

    // ---------------------------------------------------------------------------------------------
    // CreateNextGroupTasksAsync
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task CreateNextGroupTasks_ForGallery_CreatesTheInjectChange()
    {
        var msel = Msel();
        msel.UseGallery = true;

        var handler = Tasks();

        await IntegrationSteamfitterExtensions.CreateNextGroupTasksAsync(
            msel, Client(handler), 3, 4, CiteApiUrl, GalleryApiUrl, null, Ct);

        var form = Body(Assert.Single(handler.Sent).Body);

        Assert.Equal("03-04 Gallery Inject Change", form["name"].GetString());
        Assert.Equal(
            $"{GalleryApiUrl}exhibits/{ExhibitId}/move/3/inject/4",
            form["actionParameters"].GetProperty("Url").GetString());
        Assert.Equal("\"currentInject\":4", form["expectedOutput"].GetString());
    }

    /// <summary>Create next group tasks has no CITE branch at all.</summary>
    [Fact]
    public async Task CreateNextGroupTasks_HasNoCiteBranchAtAll()
    {
        var msel = Msel();
        msel.UseCite = true;

        var handler = new SiblingApiHandler();
        var previous = new ScenarioTask { Id = TriggerTaskId, Name = "previous" };

        var returned = await IntegrationSteamfitterExtensions.CreateNextGroupTasksAsync(
            msel, Client(handler), 3, 4, CiteApiUrl, GalleryApiUrl, previous, Ct);

        Assert.Empty(handler.Sent);
        Assert.Same(previous, returned);
    }

    [Fact]
    public async Task CreateNextGroupTasks_ForGallery_ChainsOntoTheTriggerItWasGiven()
    {
        var msel = Msel();
        msel.UseGallery = true;

        var handler = Tasks();
        var previous = new ScenarioTask { Id = TriggerTaskId, Name = "previous" };

        await IntegrationSteamfitterExtensions.CreateNextGroupTasksAsync(
            msel, Client(handler), 3, 4, CiteApiUrl, GalleryApiUrl, previous, Ct);

        var form = Body(Assert.Single(handler.Sent).Body);

        Assert.Equal("Completion", form["triggerCondition"].GetString());
        Assert.Equal(TriggerTaskId.ToString(), form["triggerTaskId"].GetString());
    }

    [Theory]
    [InlineData(100, 100, "00-00")]
    [InlineData(-1, 0, "-1-00")]
    public async Task CreateNextGroupTasks_NamesTheTaskWithTheSameWrappingArithmetic(
        int moveNumber, int groupNumber, string expected)
    {
        var msel = Msel();
        msel.UseGallery = true;

        var handler = Tasks();

        await IntegrationSteamfitterExtensions.CreateNextGroupTasksAsync(
            msel, Client(handler), moveNumber, groupNumber, CiteApiUrl, GalleryApiUrl, null, Ct);

        Assert.Equal(
            $"{expected} Gallery Inject Change",
            Body(Assert.Single(handler.Sent).Body)["name"].GetString());
    }

    // ---------------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------------

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly Guid ScenarioId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid ViewId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid EvaluationId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid ExhibitId = Guid.Parse("77777777-7777-7777-7777-777777777777");
    private static readonly Guid TaskId = Guid.Parse("88888888-8888-8888-8888-888888888888");
    private static readonly Guid TriggerTaskId = Guid.Parse("99999999-9999-9999-9999-999999999999");
    private static readonly Guid ManagerRoleId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid ObserverRoleId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000002");

    /// <summary>
    /// The urls as <c>IntegrationService</c> builds them - already ending in <c>api/</c>, since it appends
    /// that itself before calling in. The methods here concatenate onto them without a separator, so the
    /// trailing slash is part of the contract.
    /// </summary>
    private const string PlayerApiUrl = "http://player.example/api/";
    private const string CiteApiUrl = "http://cite.example/api/";
    private const string GalleryApiUrl = "http://gallery.example/api/";

    private static readonly string TaskJson = $$"""{"id":"{{TaskId}}","name":"task"}""";
    private static readonly string ScenarioJson =
        $$"""{"id":"{{ScenarioId}}","name":"scenario","status":"active"}""";
    private static readonly string RolesJson =
        $$"""[{"id":"{{ManagerRoleId}}","name":"Manager"},{"id":"{{ObserverRoleId}}","name":"Observer"}]""";

    private static SteamfitterApiClient Client(SiblingApiHandler handler) =>
        new(ApiClientsExtensions.GetHttpClient(handler.AsFactory(), "http://steamfitter.example/", null));

    private static SiblingApiHandler Scenarios() =>
        new SiblingApiHandler().AnswersJson("api/scenarios", ScenarioJson, HttpStatusCode.Created);

    private static SiblingApiHandler Tasks() =>
        new SiblingApiHandler().AnswersJson("api/tasks", TaskJson, HttpStatusCode.Created);

    /// <remarks>
    /// One rule per route, not one per expected call: a rule answers every request for its path unless it
    /// was registered <c>once</c>.
    /// </remarks>
    private static SiblingApiHandler Memberships() =>
        new SiblingApiHandler()
            .AnswersJson("api/scenario-roles", RolesJson)
            .AnswersJson($"api/scenarios/{ScenarioId}/memberships", "{}", HttpStatusCode.Created);

    /// <summary>
    /// A MSEL pushed as far as having a Player view, a CITE evaluation, a Gallery exhibit and a Steamfitter
    /// scenario - built in memory, because nothing in this file reads the database.
    /// </summary>
    private static MselEntity Msel()
    {
        var msel = TestData.Msel();

        msel.PlayerViewId = ViewId;
        msel.CiteEvaluationId = EvaluationId;
        msel.GalleryExhibitId = ExhibitId;
        msel.SteamfitterScenarioId = ScenarioId;
        msel.StartTime = DateTime.UtcNow.AddDays(1);
        msel.DurationSeconds = 7200;

        return msel;
    }

    /// <summary>
    /// One Steamfitter task definition. Named with a trailing underscore because <c>Task</c> is the aliased
    /// threading type in this file.
    /// </summary>
    private static SteamfitterTaskEntity Task_(
        string name,
        SteamfitterIntegrationType type = SteamfitterIntegrationType.http_get,
        Dictionary<string, string> actionParameters = null)
    {
        var entity = TestData.SteamfitterTask(
            Guid.NewGuid(), name, actionParameters: actionParameters);

        entity.TaskType = type;

        return entity;
    }

    private static UserMselRoleEntity RoleFor(Guid userId, string steamfitterRole) => new()
    {
        Id = Guid.NewGuid(),
        UserId = userId,
        Role = MselRole.Viewer,
        SteamfitterScenarioRole = steamfitterRole,
        CreatedBy = Guid.NewGuid()
    };

    private static async System.Threading.Tasks.Task<ScenarioTask> CreateScenarioTask(
        SiblingApiHandler handler,
        SteamfitterTaskEntity entity,
        int moveNumber = 1,
        int groupNumber = 1,
        ScenarioTask triggerTask = null) =>
        await IntegrationSteamfitterExtensions.CreateScenarioTasksAsync(
            Msel(), entity, Client(handler), moveNumber, groupNumber,
            PlayerApiUrl, CiteApiUrl, GalleryApiUrl, triggerTask, Ct);

    private static Dictionary<string, JsonElement> Body(string body) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(body);
}
