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
using Blueprint.Api.Tests.Infrastructure;
using Steamfitter.Api.Client;
using Xunit;

// Steamfitter's generated client declares a Task of its own, which is the type these methods pass to one
// another to chain one scenario task onto the last. Production reaches for the STT alias to keep the two
// apart; a test file that returns Task from every method is better served by aliasing the other one.
using Task = System.Threading.Tasks.Task;
using ScenarioTask = Steamfitter.Api.Client.Task;

namespace Blueprint.Api.Tests;

/// <summary>
/// <c>IntegrationSteamfitterExtensions</c> - the six calls blueprint makes to steamfitter.api when a MSEL
/// is pushed or pulled, and the scenario tasks it builds out of the timeline.
/// </summary>
/// <remarks>
/// <para>
/// No database and no host. Every method here either takes a <c>BlueprintContext</c> it never reads or
/// takes none at all, so the MSEL is built in memory and <see cref="TestHttpHandler"/> stands in for the
/// socket while the generated <c>SteamfitterApiClient</c> and every route it builds run for real. Three of
/// the six take a <c>BlueprintContext</c> parameter; all three tests pass <c>null</c>, which is the
/// cheapest proof that nothing reads it and turns red the moment something does.
/// </para>
/// <para>
/// The wire contract, since nothing else in the repository records it:
/// <c>GetAllScenarioRoles</c> is <c>GET api/scenario-roles</c> answering 200,
/// <c>DeleteScenario</c> is <c>DELETE api/scenarios/{id}</c> answering 204,
/// <c>CreateScenario</c> is <c>POST api/scenarios</c> answering 201,
/// <c>CreateScenarioMembership</c> is <c>POST api/scenarios/{id}/memberships</c> answering 201, and
/// <c>CreateTask</c> is <c>POST api/tasks</c> answering 201 - note the last builds no scenario into its
/// route, carrying <c>scenarioId</c> in the body instead.
/// </para>
/// <para>
/// <strong>The move and group numbers are formatted by string arithmetic that breaks at 100.</strong>
/// <c>"00" + n</c> then <c>Substring(length - 2)</c> gives <c>"00"</c> for move 100 and <c>"34"</c> for
/// 1234, so a hundredth move sorts and reads as the zeroth. Any negative number collapses to <c>"-1"</c>,
/// which is deliberate for the sentinel <c>-1</c> and wrong for anything else. Every task name blueprint
/// creates in Steamfitter is built this way, and the name is the only thing ordering them in the
/// Steamfitter UI. See <see cref="CreateScenarioTasks_NumbersAboveNinetyNineWrapToTheirLastTwoDigits"/>.
/// </para>
/// <para>
/// <strong>A notification body is built by string concatenation, so a quotation mark in the text produces
/// malformed JSON.</strong> <c>"{\"text\": \"" + text + "\"}"</c> - a notification reading
/// <c>He said "go"</c> becomes <c>{"text": "He said "go""}</c>, which Steamfitter will reject when the task
/// runs rather than when the MSEL is pushed, so the failure appears during the exercise. The same shape
/// builds the CITE situation update out of two parameters. See
/// <see cref="CreateScenarioTasks_ForANotification_DoesNotEscapeTheText"/>.
/// </para>
/// <para>
/// <strong><c>CreateScenarioTasksAsync</c> writes its computed values back onto the entity it was
/// given.</strong> It assigns <c>steamfitterTaskEntity.ActionParameters["Url"]</c>,
/// <c>["Body"]</c> and <c>.ExpectedOutput</c> on a row the caller loaded from the database and is still
/// tracking. Today nothing saves that context afterwards - <c>IntegrationService</c> saves before the loop
/// (line 312) and does its completion save on a fresh context - so this is latent rather than live. It is
/// pinned because it is a trap: one <c>SaveChangesAsync</c> added anywhere after the push loop would write
/// resolved URLs into blueprint's own stored task definitions. See
/// <see cref="CreateScenarioTasks_MutatesTheEntityItWasGiven"/>.
/// </para>
/// <para>
/// <strong>An entity whose <c>ActionParameters</c> is null is a <c>NullReferenceException</c></strong> for
/// the two task types that write into the dictionary, and fine for the four that do not. Nothing
/// initializes the column and nothing constrains it.
/// </para>
/// <para>
/// <strong><c>CreateNextGroupTasksAsync</c> has no CITE branch</strong> where
/// <c>CreateNextMoveTasksAsync</c> has one, so a group change advances the Gallery exhibit's inject and
/// leaves the CITE evaluation where it was. Whether CITE has a per-inject notion of position is a question
/// about CITE; what is pinned here is the asymmetry. See
/// <see cref="CreateNextGroupTasks_HasNoCiteBranchAtAll"/>.
/// </para>
/// <para>
/// <strong>The two task forms in <c>CreateNextMoveTasksAsync</c> share one
/// <c>ActionParameters</c> dictionary.</strong> One dictionary is built, given to the CITE form by
/// reference, then its <c>Url</c> is overwritten for Gallery and the same reference is given to the second
/// form. Both requests happen to be correct, because each is serialized and sent before the next
/// mutation - so this is latent too, and would become live the moment the two creates were batched or
/// deferred, which is exactly the change the <c>batchSize</c> defect elsewhere in this layer invites. See
/// <see cref="CreateNextMoveTasks_ForBoth_SendsTheRightUrlToEachDespiteSharingOneDictionary"/>.
/// </para>
/// <para>
/// Smaller things: <c>CreateScenarioMembershipsAsync</c> deduplicates a user's roles with
/// <c>GroupBy(...).First()</c>, so a user holding two MSEL roles that name different Steamfitter roles gets
/// an arbitrary one of them; a role name Steamfitter does not know is skipped silently; each membership is
/// created inside its own swallow; <c>galleryApiUrl</c> is passed to <c>CreateScenarioTasksAsync</c> and
/// never read; and <c>PullFromSteamfitterAsync</c> swallows everything, like its Player counterpart.
/// </para>
/// <para>
/// Per this branch's rule, every test above characterizes rather than fixes, and says what fixing it will do
/// to the test.
/// </para>
/// </remarks>
public class IntegrationSteamfitterExtensionsTests
{
    // ---------------------------------------------------------------------------------------------
    // GetSteamfitterApiClient
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task GetSteamfitterApiClient_BuildsAClientCarryingTheTokenAndTheApiUrl()
    {
        var handler = new TestHttpHandler().Answers($"api/scenarios/{ScenarioId}", HttpStatusCode.NoContent);
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
        var handler = new TestHttpHandler().Answers($"api/scenarios/{ScenarioId}", HttpStatusCode.NoContent);

        await IntegrationSteamfitterExtensions.PullFromSteamfitterAsync(ScenarioId, Client(handler), Ct);

        var sent = Assert.Single(handler.Sent);

        Assert.Equal(HttpMethod.Delete, sent.Method);
        Assert.Equal($"api/scenarios/{ScenarioId}", sent.Path);
    }

    /// <remarks>
    /// The same empty <c>catch</c> as <c>IntegrationPlayerExtensions.PullFromPlayerAsync</c>, and with the
    /// same consequence: <c>IntegrationService</c> clears the MSEL's <c>SteamfitterScenarioId</c> whatever
    /// happened, so a scenario that could not be deleted is orphaned with nothing pointing at it.
    /// </remarks>
    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task PullFromSteamfitter_SwallowsARefusal(HttpStatusCode status)
    {
        var handler = new TestHttpHandler().Answers($"api/scenarios/{ScenarioId}", status);

        await IntegrationSteamfitterExtensions.PullFromSteamfitterAsync(ScenarioId, Client(handler), Ct);

        Assert.Single(handler.Sent);
    }

    [Fact]
    public async Task PullFromSteamfitter_SwallowsAnUnreachableSteamfitter()
    {
        var handler = new TestHttpHandler().Throws($"api/scenarios/{ScenarioId}");

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

    /// <remarks>
    /// <c>ViewId</c> gets the Player view and <c>View</c> gets the MSEL's <em>name</em> - not the view's
    /// name, though the two are the same thing today because <c>CreateViewAsync</c> names the Player view
    /// after the MSEL as well. Renaming a view in Player would make them disagree, and nothing here would
    /// notice.
    /// </remarks>
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

    /// <remarks>
    /// A start time already past is clamped to now, so the scenario gets its full duration from the moment
    /// of the push rather than ending in the past. That is the right decision and worth pinning, because it
    /// means the MSEL's own <c>StartTime</c> is not what Steamfitter runs on for any MSEL pushed late.
    /// </remarks>
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
        var handler = new TestHttpHandler().AnswersJson(
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

    /// <remarks>
    /// Silently. A MSEL configured against a Steamfitter that has since renamed or removed a scenario role
    /// pushes without that membership and without complaint, so the user simply cannot see the scenario.
    /// </remarks>
    [Fact]
    public async Task CreateScenarioMemberships_SkipsARoleNameSteamfitterDoesNotHave()
    {
        var msel = Msel();
        msel.UserMselRoles.Add(RoleFor(Guid.NewGuid(), "NoSuchRole"));

        var handler = Memberships();

        await IntegrationSteamfitterExtensions.CreateScenarioMembershipsAsync(msel, Client(handler), null, Ct);

        Assert.Equal("api/scenario-roles", Assert.Single(handler.Paths));
    }

    /// <remarks>
    /// <para>
    /// The deduplication is deliberate and its result is not: a user holding two MSEL roles that name
    /// different Steamfitter roles gets whichever <c>GroupBy(...).First()</c> happens to yield, which is
    /// collection order. Both are asserted as acceptable here, because pinning one would pin an accident.
    /// </para>
    /// <para>
    /// What is worth pinning is that exactly one membership is posted - the comment above the query says
    /// deduplicate, and it does.
    /// </para>
    /// </remarks>
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

    /// <remarks>
    /// One membership per swallow, so a Steamfitter that refuses the first still gets the second asked for -
    /// which is the right shape, and means a push reports success having created none of them.
    /// </remarks>
    [Fact]
    public async Task CreateScenarioMemberships_SwallowsAFailedMembershipAndCarriesOn()
    {
        var msel = Msel();
        msel.UserMselRoles.Add(RoleFor(Guid.NewGuid(), "Manager"));
        msel.UserMselRoles.Add(RoleFor(Guid.NewGuid(), "Observer"));

        var handler = new TestHttpHandler()
            .AnswersJson("api/scenario-roles", RolesJson)
            .Answers($"api/scenarios/{ScenarioId}/memberships", HttpStatusCode.Conflict);

        await IntegrationSteamfitterExtensions.CreateScenarioMembershipsAsync(msel, Client(handler), null, Ct);

        Assert.Equal(2, handler.Sent.Count(x => x.Path.EndsWith("memberships")));
    }

    /// <remarks>
    /// <para>
    /// The role list is fetched before anything checks whether there is a scenario to add members to, so a
    /// MSEL with no <c>SteamfitterScenarioId</c> costs a request and then throws on the cast.
    /// </para>
    /// <para>
    /// It is the cast on line 50, building the membership, that surfaces - not the identical one on line 56
    /// passing the scenario into the route, because that one is inside the per-membership <c>catch</c> and
    /// would be swallowed. Removing only the first leaves this method silently doing nothing at all.
    /// </para>
    /// </remarks>
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

    /// <remarks>
    /// <para>
    /// <c>"00" + n</c> then <c>Substring(length - 2)</c>. Below a hundred it is zero padding; at a hundred it
    /// is modulo arithmetic. Move 100 is named <c>"00"</c>, which is what move 0 is named, and 1234 is
    /// <c>"34"</c>.
    /// </para>
    /// <para>
    /// Whether a MSEL ever has a hundred moves is a fair question; it certainly has more than a hundred
    /// scenario events, and <c>groupNumber</c> is a position within a move rather than a move count.
    /// Replacing this with <c>n.ToString("00")</c> turns the two wrapping cases red and leaves the rest
    /// green.
    /// </para>
    /// </remarks>
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

    /// <remarks>
    /// <c>-1</c> is the sentinel <c>IntegrationService</c> starts its move counter at, so a task created
    /// before the first move boundary is named <c>"-1"</c>. Any other negative number collapses to the same
    /// string, which is not a case the caller produces but is one the method accepts.
    /// </remarks>
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

    /// <remarks>
    /// <para>
    /// The body is <c>"{\"text\": \"" + text + "\"}"</c>, so a quotation mark in the notification closes the
    /// string early and the result is not JSON. Steamfitter rejects it when the task runs, which is during
    /// the exercise rather than at push time - and the operator who typed the text is not the one who sees
    /// the failure.
    /// </para>
    /// <para>
    /// Serializing the object instead turns this test red. A backslash, a newline and a control character are
    /// all the same class of problem; the quotation mark is the one a person types.
    /// </para>
    /// </remarks>
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

    /// <remarks>
    /// The one type that is not an HTTP call: <c>apiUrl</c> becomes <c>"stackstorm"</c>, which is how
    /// Steamfitter dispatches it to a different executor. Nothing sets a Url or a Body, so whatever the
    /// entity already carries in <c>ActionParameters</c> is sent as-is.
    /// </remarks>
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

    /// <remarks>
    /// The four raw HTTP types pass through: the action is translated and nothing else is touched, so the
    /// url and body are whatever the MSEL author put in <c>ActionParameters</c>. These are the types that
    /// work with a null <c>ActionParameters</c>, because nothing writes into it.
    /// </remarks>
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

    /// <remarks>
    /// <c>http_post</c> is in the enum and absent from the switch, so it falls through to the initializers -
    /// which happen to be <c>Http_post</c> and <c>"http"</c>, the same answer the case would have given. The
    /// switch has no <c>default</c>, so a type added to the enum and not to the switch would silently become
    /// an HTTP POST too, which is the part worth knowing.
    /// </remarks>
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

    /// <remarks>
    /// Nothing initializes <c>ActionParameters</c> - the column is a nullable dictionary with no
    /// configuration - so the two task types that write a Url into it throw on a row that has never had one
    /// set. The four raw HTTP types are fine, which is why this is a shape rather than a rule.
    /// </remarks>
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

    /// <remarks>
    /// The parameter the MSEL author is expected to have filled in. A notification with no
    /// <c>notificationText</c> is a <c>KeyNotFoundException</c> mid-push, naming the key and nothing else -
    /// not the task, not the MSEL, not the scenario event.
    /// </remarks>
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

    /// <remarks>
    /// <c>Executable</c> is hard-coded true and <c>IterationTermination</c> hard-coded to the iteration
    /// count, so a MSEL cannot describe a task that runs until some other condition.
    /// </remarks>
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
        var handler = new TestHttpHandler().AnswersJson(
            "api/tasks", $$"""{"id":"{{answered}}","name":"answered"}""", HttpStatusCode.Created);

        var created = await CreateScenarioTask(
            handler, Task_("returns", SteamfitterIntegrationType.http_get, null));

        Assert.Equal(answered, created.Id);
    }

    /// <remarks>
    /// <para>
    /// The defect in this class's remarks. The entity is a row the caller loaded and is still tracking, and
    /// this method writes the resolved Url, the built Body and the ExpectedOutput onto it.
    /// </para>
    /// <para>
    /// Nothing saves that context after the push loop today, so no resolved URL reaches the database - but
    /// the entity in memory is no longer what was read, and one added <c>SaveChangesAsync</c> would make it
    /// permanent. Building the form from locals instead turns this test red.
    /// </para>
    /// </remarks>
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

    /// <remarks>
    /// <c>galleryApiUrl</c> is a parameter of this method and is read nowhere in it, so the value
    /// <c>IntegrationService</c> takes the trouble to build and pass has no effect. Passing something absurd
    /// is the cheapest proof; it turns red the moment something reads it.
    /// </remarks>
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

    /// <remarks>
    /// <para>
    /// One <c>Dictionary&lt;string, string&gt;</c> is built at the top of the method, handed to the CITE form
    /// by reference, then has its <c>Url</c> overwritten for Gallery and is handed to the second form - the
    /// same instance. Both requests are nonetheless correct, because each form is serialized and sent before
    /// the next mutation happens.
    /// </para>
    /// <para>
    /// So this test passes today and would fail the moment anyone deferred or batched the two creates, which
    /// is exactly the refactor the ignored <c>batchSize</c> elsewhere in this layer needs. Giving each form
    /// its own dictionary keeps it green whatever happens next.
    /// </para>
    /// </remarks>
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
        var handler = new TestHttpHandler()
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
        var handler = new TestHttpHandler();
        var previous = new ScenarioTask { Id = TriggerTaskId, Name = "previous" };

        var returned = await IntegrationSteamfitterExtensions.CreateNextMoveTasksAsync(
            Msel(), Client(handler), 3, CiteApiUrl, GalleryApiUrl, previous, Ct);

        Assert.Empty(handler.Sent);
        Assert.Same(previous, returned);
    }

    /// <remarks>
    /// The four timing values are private constants rather than anything the MSEL can influence: two
    /// minutes to expire, no delay, no interval, one iteration. A move change that Steamfitter cannot
    /// complete inside two minutes is abandoned, and nothing in blueprint can say otherwise.
    /// </remarks>
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

    /// <remarks>
    /// <para>
    /// <c>CreateNextMoveTasksAsync</c> has an <c>if (msel.UseCite)</c> block and this method does not, so a
    /// group boundary advances Gallery's inject and tells CITE nothing. A MSEL using CITE and not Gallery
    /// gets no task at all from this method.
    /// </para>
    /// <para>
    /// Whether CITE has a per-inject position to advance is a question about CITE rather than about
    /// blueprint; what is pinned here is that blueprint does not ask. Adding the branch turns this test red.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task CreateNextGroupTasks_HasNoCiteBranchAtAll()
    {
        var msel = Msel();
        msel.UseCite = true;

        var handler = new TestHttpHandler();
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

    private static SteamfitterApiClient Client(TestHttpHandler handler) =>
        new(ApiClientsExtensions.GetHttpClient(handler.AsFactory(), "http://steamfitter.example/", null));

    private static TestHttpHandler Scenarios() =>
        new TestHttpHandler().AnswersJson("api/scenarios", ScenarioJson, HttpStatusCode.Created);

    private static TestHttpHandler Tasks() =>
        new TestHttpHandler().AnswersJson("api/tasks", TaskJson, HttpStatusCode.Created);

    /// <remarks>
    /// One rule per route, not one per expected call: a rule answers every request for its path unless it
    /// was registered <c>once</c>.
    /// </remarks>
    private static TestHttpHandler Memberships() =>
        new TestHttpHandler()
            .AnswersJson("api/scenario-roles", RolesJson)
            .AnswersJson($"api/scenarios/{ScenarioId}/memberships", "{}", HttpStatusCode.Created);

    /// <summary>
    /// A MSEL pushed as far as having a Player view, a CITE evaluation, a Gallery exhibit and a Steamfitter
    /// scenario - built in memory, because nothing in this file reads the database.
    /// </summary>
    private static MselEntity Msel()
    {
        var msel = BlueprintAppFactory.Msel();

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
        var entity = BlueprintAppFactory.SteamfitterTask(
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
        TestHttpHandler handler,
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
