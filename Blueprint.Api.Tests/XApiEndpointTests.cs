// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Blueprint.Api.Data.Models;
using Blueprint.Api.Tests.Infrastructure;
using Blueprint.Api.ViewModels;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Xunit;

namespace Blueprint.Api.Tests;

/// <summary>
/// <c>XApiController</c> - the four routes blueprint's UI calls to record what a participant did and to
/// read back what the LRS holds.
/// </summary>
/// <remarks>
/// <para>
/// This class drives them against the substituted <c>IXApiService</c> the standard harness installs, so
/// what it pins is the controller: what a route forwards, what it answers, and what it asks of the
/// caller. The statements themselves are <c>XApiServiceTests</c>' business, and one request per route is
/// followed all the way to the queued row in <see cref="XApiLiveEndpointTests"/>.
/// </para>
/// <para>
/// <strong>Not one of the four is authorized beyond being signed in.</strong> The controller carries only
/// <c>BaseController</c>'s bare <c>[Authorize]</c> and neither it nor <c>XApiService</c> consults
/// <c>IBlueprintAuthorizationService</c> or any of the eight <c>Msel*Requirement</c> helpers - the only
/// controller family in the API where a MSEL id in the query string or the body is taken on trust. So any
/// authenticated caller may assert any competency about any MSEL and read every statement the LRS holds
/// for one, including exercises they have no role on and cannot otherwise see. See
/// <see cref="EveryRoute_ForACallerWithNoPermissions_Is200"/>, and
/// <see cref="XApiLiveEndpointTests.CreateAssertion_ByACallerWithNoRoleOnTheMsel_IsRecordedAnyway"/> for
/// the same thing with a statement at the end of it.
/// </para>
/// <para>
/// <strong>Three of the four throw the answer away.</strong> Every <c>IXApiService</c> method returns
/// <c>bool</c>, and <c>CreateAssertion</c>, <c>Viewed</c> and <c>ViewedJoinPage</c> all discard it and
/// answer a bare <c>Ok()</c> - so a caller cannot tell a statement that was queued from one that was
/// skipped because xAPI is switched off, nor a MSEL that was viewed from one that does not exist. See
/// <see cref="Viewed_ForAMselTheServiceCouldNotFind_IsStill200"/>.
/// </para>
/// <para>
/// Per this branch's rule, every test characterizes rather than fixes, and says what fixing it will do to
/// the test.
/// </para>
/// </remarks>
public class XApiEndpointTests(DatabaseFixture fixture, BlueprintAppFactory factory)
    : ApiTestBase(fixture, factory), IClassFixture<BlueprintAppFactory>
{
    // -------------------------------------------------------------------------------------------------
    // The surface: who may call these routes
    // -------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("GET", Statements)]
    [InlineData("POST", Assertions)]
    [InlineData("POST", ViewedSomeMsel)]
    [InlineData("POST", ViewedJoinPage)]
    public async Task EveryRoute_Anonymously_Is401(string method, string route)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), route);

        var response = await AnonymousClient.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <remarks>
    /// A caller with no system role, no team and no role on any MSEL, naming a MSEL that is not there -
    /// and all four routes answer <c>200</c>. Requiring any permission at all, or asking
    /// <c>MselViewRequirement</c> about the MSEL in the query string, turns this red on three of the four;
    /// the join-page statement names no MSEL and is the one that genuinely needs nothing.
    /// </remarks>
    [Theory]
    [InlineData("GET", Statements)]
    [InlineData("POST", Assertions)]
    [InlineData("POST", ViewedSomeMsel)]
    [InlineData("POST", ViewedJoinPage)]
    public async Task EveryRoute_ForACallerWithNoPermissions_Is200(string method, string route)
    {
        StatementsAre("{\"statements\":[]}");

        var actor = await Actor().SeedAsync();

        using var request = new HttpRequestMessage(new HttpMethod(method), route);
        if (method == "POST")
        {
            request.Content = EmptyJsonObject();
        }

        var response = await Client(actor).SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <remarks>
    /// MVC binds a <c>CancellationToken</c> parameter to <c>HttpContext.RequestAborted</c>, and all four
    /// actions declare one and pass it on. Matched on <c>CanBeCanceled</c> rather than with
    /// <c>Arg.Any</c>, because <c>CancellationToken.None</c> is a legal token and an assertion that
    /// accepts it cannot tell a forwarded token from a dropped one.
    /// </remarks>
    [Fact]
    public async Task EveryRoute_PassesTheRequestsCancellationToken()
    {
        StatementsAre("{}");

        var client = Client(await Actor().SeedAsync());
        var mselId = Guid.NewGuid();

        await client.GetAsync(Statements, Ct);
        await client.PostAsJsonAsync(Assertions, new { mselId }, Ct);
        await client.PostAsync(Viewed(mselId), null, Ct);
        await client.PostAsync(ViewedJoinPage, null, Ct);

        await Factory.XApi.Received(1).GetStatementsAsync(
            Arg.Any<Guid>(),
            Arg.Any<DateTime?>(),
            Arg.Any<DateTime?>(),
            Arg.Any<int>(),
            Arg.Any<string>(),
            Cancellable);
        await Factory.XApi.Received(1)
            .AssertCompetencyAsync(Arg.Any<CompetencyAssertion>(), Cancellable);
        await Factory.XApi.Received(1).MselViewedAsync(mselId, Cancellable);
        await Factory.XApi.Received(1).JoinPageViewedAsync(Cancellable);
    }

    // -------------------------------------------------------------------------------------------------
    // GET xapi/statements
    // -------------------------------------------------------------------------------------------------

    /// <remarks>
    /// The body is whatever the service answered, written with <c>Content(result, "application/json")</c> -
    /// so it crosses the wire exactly as the LRS wrote it, rather than being deserialized into blueprint's
    /// own view models and re-serialized. That is the right call for a passthrough and it is worth pinning:
    /// the route's declared response type is <c>string</c>, and returning <c>Ok(result)</c> instead would
    /// double-encode the document into a JSON string literal.
    /// </remarks>
    [Fact]
    public async Task GetStatements_ForwardsTheQueryString_AndAnswersTheLrsDocumentVerbatim()
    {
        const string document = "{\"statements\":[{\"id\":\"9e1c0a7a\"}],\"more\":\"\"}";
        StatementsAre(document);

        var mselId = Guid.NewGuid();
        var route = $"{Statements}?mselId={mselId}" +
            "&since=2026-01-01T08:30:00Z&until=2026-01-02T09:45:00Z&limit=7&source=cite";

        var response = await Client(await Actor().SeedAsync()).GetAsync(route, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(document, await response.Content.ReadAsStringAsync(Ct));

        await Factory.XApi.Received(1).GetStatementsAsync(
            Arg.Is(mselId),
            Utc(new DateTime(2026, 1, 1, 8, 30, 0, DateTimeKind.Utc)),
            Utc(new DateTime(2026, 1, 2, 9, 45, 0, DateTimeKind.Utc)),
            Arg.Is(7),
            Arg.Is("cite"),
            Cancellable);
    }

    /// <remarks>
    /// <c>limit</c> defaults to 100 and everything else to null, so the shortest useful request asks for
    /// every source and the hundred most recent statements. Changing the default turns this red.
    /// </remarks>
    [Fact]
    public async Task GetStatements_WithOnlyAMselId_AsksForAHundredStatementsFromEverySource()
    {
        StatementsAre("{}");

        var mselId = Guid.NewGuid();

        await Client(await Actor().SeedAsync()).GetAsync($"{Statements}?mselId={mselId}", Ct);

        await Factory.XApi.Received(1).GetStatementsAsync(
            Arg.Is(mselId), Missing, Missing, Arg.Is(100), NoSource, Cancellable);
    }

    /// <remarks>
    /// <c>mselId</c> is not <c>[Required]</c> and <c>Guid</c> is not nullable, so a request that names no
    /// MSEL asks about the all-zeros one instead of being refused - and the answer is an empty statement
    /// list, indistinguishable from an exercise nobody has touched. Making the parameter
    /// <c>Guid?</c> and answering <c>400</c>, as the rest of the API does for a missing route value,
    /// turns this red.
    /// </remarks>
    [Fact]
    public async Task GetStatements_NamingNoMselAtAll_AsksAboutTheAllZerosMsel()
    {
        StatementsAre("{\"statements\":[]}");

        var response = await Client(await Actor().SeedAsync()).GetAsync(Statements, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await Factory.XApi.Received(1).GetStatementsAsync(
            Arg.Is(Guid.Empty), Missing, Missing, Arg.Is(100), NoSource, Cancellable);
    }

    /// <remarks>
    /// <c>Content(null, "application/json")</c> is a 200 with an empty body and a JSON content type, which
    /// no JSON parser accepts. Today's <c>XApiService</c> never answers null - all three of its early
    /// returns are the literal <c>{"statements":[]}</c> - so this pins the controller's lack of a guard
    /// rather than a live path. Returning <c>Ok(result)</c>, or coalescing to an empty document, turns it
    /// red.
    /// </remarks>
    [Fact]
    public async Task GetStatements_WhenTheServiceAnswersNothing_Is200WithABodyThatIsNotJson()
    {
        StatementsAre(null);

        var response = await Client(await Actor().SeedAsync()).GetAsync(Statements, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(string.Empty, await response.Content.ReadAsStringAsync(Ct));
    }

    // -------------------------------------------------------------------------------------------------
    // POST xapi/assertions
    // -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task CreateAssertion_ForwardsTheBodyAsSent()
    {
        var assertion = new
        {
            mselId = Guid.NewGuid(),
            competencyId = Guid.NewGuid(),
            scenarioEventId = Guid.NewGuid(),
            teamId = Guid.NewGuid(),
            proficiencyLevelId = Guid.NewGuid(),
            comment = "kept the team informed",
            moveNumber = 2,
            groupNumber = 3
        };

        var response = await Client(await Actor().SeedAsync())
            .PostAsJsonAsync(Assertions, assertion, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await Factory.XApi.Received(1).AssertCompetencyAsync(
            Arg.Is<CompetencyAssertion>(x =>
                x.MselId == assertion.mselId &&
                x.CompetencyId == assertion.competencyId &&
                x.ScenarioEventId == assertion.scenarioEventId &&
                x.TeamId == assertion.teamId &&
                x.ProficiencyLevelId == assertion.proficiencyLevelId &&
                x.Comment == assertion.comment &&
                x.MoveNumber == assertion.moveNumber &&
                x.GroupNumber == assertion.groupNumber),
            Cancellable);
    }

    /// <remarks>
    /// <c>[FromBody]</c> with no explicit default makes the body mandatory, so this is MVC's own 400 and
    /// the service is never reached. Sending no content type at all would be a 415 instead, which is why
    /// this posts an empty <c>application/json</c> body rather than <c>null</c>.
    /// </remarks>
    [Fact]
    public async Task CreateAssertion_WithNoBody_Is400()
    {
        using var content = new StringContent(string.Empty, Encoding.UTF8, "application/json");

        var response = await Client(await Actor().SeedAsync()).PostAsync(Assertions, content, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await Factory.XApi.DidNotReceiveWithAnyArgs()
            .AssertCompetencyAsync(Arg.Any<CompetencyAssertion>(), Arg.Any<CancellationToken>());
    }

    /// <remarks>
    /// <c>CompetencyAssertion</c> carries no validation attributes, so <c>{}</c> is a valid body: three
    /// required ids arrive as <see cref="Guid.Empty"/> and are forwarded. The service then answers
    /// <c>500</c> for the missing MSEL (<see cref="XApiLiveEndpointTests.CreateAssertion_ForAMselThatIsNotThere_Is500NamingTheMsel"/>),
    /// so nothing is recorded - but the refusal comes from three database lookups rather than from the
    /// model. <c>[Required]</c> on the three <c>Guid</c> properties turns this red, and is the fix.
    /// </remarks>
    [Fact]
    public async Task CreateAssertion_WithAnEmptyJsonObject_IsForwardedWithAllZeroIds()
    {
        using var content = EmptyJsonObject();

        var response = await Client(await Actor().SeedAsync()).PostAsync(Assertions, content, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await Factory.XApi.Received(1).AssertCompetencyAsync(
            Arg.Is<CompetencyAssertion>(x =>
                x.MselId == Guid.Empty &&
                x.CompetencyId == Guid.Empty &&
                x.ProficiencyLevelId == Guid.Empty &&
                x.ScenarioEventId == null &&
                x.TeamId == null &&
                x.Comment == null &&
                x.MoveNumber == null &&
                x.GroupNumber == null),
            Cancellable);
    }

    // -------------------------------------------------------------------------------------------------
    // POST xapi/viewed/msel/{id} and POST xapi/viewed/joinpage
    // -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Viewed_ForwardsTheIdFromTheRoute()
    {
        Factory.XApi.MselViewedAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(true);

        var mselId = Guid.NewGuid();

        var response = await Client(await Actor().SeedAsync()).PostAsync(Viewed(mselId), null, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await Factory.XApi.Received(1).MselViewedAsync(mselId, Cancellable);
    }

    /// <remarks>
    /// <c>MselViewedAsync(Guid)</c> answers <c>false</c> for a MSEL it cannot find, and the controller
    /// discards the answer: <c>await _xApiService.MselViewedAsync(id, ct); return Ok();</c>. So the UI is
    /// told the view was recorded either way. Answering <c>404</c> on <c>false</c> turns this red - though
    /// note <c>false</c> would then also mean "xAPI is switched off", which is the reason the bool is not
    /// usable as it stands.
    /// </remarks>
    [Fact]
    public async Task Viewed_ForAMselTheServiceCouldNotFind_IsStill200()
    {
        Factory.XApi.MselViewedAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(false);

        var response = await Client(await Actor().SeedAsync())
            .PostAsync(Viewed(Guid.NewGuid()), null, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task ViewedJoinPage_TakesNothingAndAnswers200()
    {
        Factory.XApi.JoinPageViewedAsync(Arg.Any<CancellationToken>()).Returns(true);

        var response = await Client(await Actor().SeedAsync()).PostAsync(ViewedJoinPage, null, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await Factory.XApi.Received(1).JoinPageViewedAsync(Cancellable);
    }

    // -------------------------------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------------------------------

    internal const string Statements = "/api/xapi/statements";
    internal const string Assertions = "/api/xapi/assertions";
    internal const string ViewedJoinPage = "/api/xapi/viewed/joinpage";

    /// <summary>A MSEL id no test seeds, for the two route sweeps.</summary>
    private const string ViewedSomeMsel = "/api/xapi/viewed/msel/6f1f0d9e-0000-4000-8000-000000000001";

    internal static string Viewed(Guid mselId) => $"/api/xapi/viewed/msel/{mselId}";

    /// <summary>
    /// A token the request could actually have been aborted with. See
    /// <see cref="EveryRoute_PassesTheRequestsCancellationToken"/> for why <c>Arg.Any</c> will not do.
    /// </summary>
    private static CancellationToken Cancellable =>
        Arg.Is<CancellationToken>(x => x.CanBeCanceled);

    private static StringContent EmptyJsonObject() =>
        new("{}", Encoding.UTF8, "application/json");

    private void StatementsAre(string document) =>
        Factory.XApi.GetStatementsAsync(
                Arg.Any<Guid>(),
                Arg.Any<DateTime?>(),
                Arg.Any<DateTime?>(),
                Arg.Any<int>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(document);

    /// <summary>
    /// Matches a bound <c>DateTime?</c> by the instant it names rather than by its
    /// <c>DateTimeKind</c>, which is model binding's business and varies with the machine's time zone.
    /// </summary>
    private static DateTime? Utc(DateTime expected) =>
        Arg.Is<DateTime?>(x => x.HasValue && x.Value.ToUniversalTime() == expected);

    private static DateTime? Missing => Arg.Is<DateTime?>(x => !x.HasValue);

    private static string NoSource => Arg.Is<string>(x => x == null);
}

/// <summary>
/// The same four routes over the real <c>XApiService</c>, with xAPI switched on, so a request can be
/// followed to the row it leaves in the queue.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="XApiEnabledFactory"/> is why this is a second class: both the feature flag and the service
/// registration are settled before a test can reach them. What is asserted here is deliberately shallow -
/// that a statement of the right verb reached the queue naming the right MSEL and the caller - because
/// <c>XApiServiceTests</c> already pins every field of every statement shape over the same code. The
/// value of driving it over HTTP is the three things only a host can show: that the request's principal
/// becomes the statement's actor, that an unauthorized caller gets to write one anyway, and what an
/// unreachable LRS looks like to whoever clicked.
/// </para>
/// <para>
/// No background service is running - the base factory removes all four - so the queue is where every
/// statement stops. <c>XApiBackgroundServiceTests</c> covers what would happen next.
/// </para>
/// </remarks>
public class XApiLiveEndpointTests(DatabaseFixture fixture, XApiEnabledFactory factory)
    : ApiTestBase(fixture, factory), IClassFixture<XApiEnabledFactory>
{
    [Fact]
    public async Task Viewed_QueuesAViewedStatementNamingTheMselAndTheCaller()
    {
        var actor = await Actor().SeedAsync();
        var msel = BlueprintAppFactory.Msel();
        await Seed(msel);

        var response = await Client(actor)
            .PostAsync(XApiEndpointTests.Viewed(msel.Id), null, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var row = await Queued();
        Assert.Equal("viewed", row.Verb);
        Assert.Equal(msel.Id, row.MselId);
        Assert.Equal(XApiEnabledFactory.ApiUrl + "msel/" + msel.Id, row.ActivityId);
        Assert.Equal(XApiQueueStatus.Pending, row.Status);

        var statement = Statement(row);
        Assert.Equal("http://id.tincanapi.com/verb/viewed", Text(statement, "verb.id"));
        Assert.Equal(msel.Name, Text(statement, "object.definition.name.en-US"));
        Assert.Equal(msel.Id.ToString(), Text(statement, "context.registration"));
        Assert.Equal(actor.Name, Text(statement, "actor.name"));
        Assert.Equal(actor.Id.ToString(), Text(statement, "actor.account.name"));
        Assert.Equal(TestAuthHandler.Issuer, Text(statement, "actor.account.homePage"));
    }

    /// <remarks>
    /// The MSEL lookup fails, <c>MselViewedAsync</c> answers <c>false</c>, the controller discards it.
    /// Nothing is queued and the caller is told everything is fine - the pairing this file exists to show
    /// alongside <c>XApiEndpointTests.Viewed_ForAMselTheServiceCouldNotFind_IsStill200</c>, which pins the
    /// same thing one layer up.
    /// </remarks>
    [Fact]
    public async Task Viewed_ForAMselThatIsNotThere_Is200AndQueuesNothing()
    {
        var response = await Client(await Actor().SeedAsync())
            .PostAsync(XApiEndpointTests.Viewed(Guid.NewGuid()), null, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, await QueuedCount());
    }

    /// <remarks>
    /// The one statement in the codebase that names no MSEL, so the queued row's <c>MselId</c> is null and
    /// the statement carries no <c>context.registration</c> - which is what the LRS query in
    /// <c>GetStatementsAsync</c> filters on. A join-page view is therefore written and can never be read
    /// back through blueprint.
    /// </remarks>
    [Fact]
    public async Task ViewedJoinPage_QueuesAStatementThatNamesNoMsel()
    {
        var response = await Client(await Actor().SeedAsync()).PostAsync(
            XApiEndpointTests.ViewedJoinPage, null, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var row = await Queued();
        Assert.Equal("viewed", row.Verb);
        Assert.Null(row.MselId);
        Assert.Equal(XApiEnabledFactory.ApiUrl + "page/join-page", row.ActivityId);

        var statement = Statement(row);
        Assert.Equal(XApiEnabledFactory.ApiUrl + "page/join-page", Text(statement, "object.id"));
        Assert.Equal("Join Event Page", Text(statement, "object.definition.name.en-US"));
        Assert.False(At(statement, "context").TryGetProperty("registration", out _));
    }

    [Fact]
    public async Task CreateAssertion_QueuesAnAssertedStatement()
    {
        var actor = await Actor().SeedAsync();
        var graph = await SeedAssertionGraph();

        var response = await Client(actor).PostAsJsonAsync(
            XApiEndpointTests.Assertions,
            new
            {
                mselId = graph.MselId,
                competencyId = graph.CompetencyId,
                proficiencyLevelId = graph.LevelId,
                comment = "held the line"
            },
            Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var row = await Queued();
        Assert.Equal("asserted", row.Verb);
        Assert.Equal(graph.MselId, row.MselId);
        Assert.Equal(CompetencyIri, row.ActivityId);

        var statement = Statement(row);
        Assert.Equal("https://w3id.org/xapi/tla/verbs/asserted", Text(statement, "verb.id"));
        Assert.Equal(CompetencyIri, Text(statement, "object.id"));
        Assert.Equal("held the line", Text(statement, "result.response"));
        Assert.Equal(3, At(statement, "result.score.raw").GetDouble());
        Assert.Equal(actor.Id.ToString(), Text(statement, "actor.account.name"));
    }

    /// <remarks>
    /// The assertion names a MSEL this caller has no role on, is not a member of a team on, and did not
    /// create, and the statement is written anyway - a rating attributed to them, against somebody else's
    /// exercise, that the LRS will hold permanently. Asking <c>MselViewRequirement</c> or
    /// <c>EvaluatorRequirement</c> about <c>assertion.MselId</c>, which is the only MSEL id in play, turns
    /// this red and is the fix.
    /// </remarks>
    [Fact]
    public async Task CreateAssertion_ByACallerWithNoRoleOnTheMsel_IsRecordedAnyway()
    {
        var graph = await SeedAssertionGraph();
        var stranger = await Actor().SeedAsync();

        var response = await Client(stranger).PostAsJsonAsync(
            XApiEndpointTests.Assertions,
            new { mselId = graph.MselId, competencyId = graph.CompetencyId, proficiencyLevelId = graph.LevelId },
            Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, await QueuedCount());
    }

    /// <remarks>
    /// <c>AssertCompetencyAsync</c> throws <c>ArgumentException</c> for each of the four rows it cannot
    /// find, and <c>ArgumentException</c> does not implement <c>IApiException</c>, so
    /// <c>JsonExceptionFilter</c> maps it to <c>500</c> where every other not-found in the API is a
    /// <c>404</c> - and the caller is shown the message as an internal error, with the stack trace in
    /// <c>detail</c> because the harness runs the host in Development. Throwing
    /// <c>EntityNotFoundException</c> instead turns this red, and is the fix.
    /// </remarks>
    [Fact]
    public async Task CreateAssertion_ForAMselThatIsNotThere_Is500NamingTheMsel()
    {
        var mselId = Guid.NewGuid();

        var response = await Client(await Actor().SeedAsync()).PostAsJsonAsync(
            XApiEndpointTests.Assertions,
            new { mselId, competencyId = Guid.NewGuid(), proficiencyLevelId = Guid.NewGuid() },
            Ct);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal($"MSEL {mselId} not found", await Title(response));
        Assert.Equal(0, await QueuedCount());
    }

    // -------------------------------------------------------------------------------------------------
    // GET xapi/statements, which is the only route that talks to the LRS on the request thread
    // -------------------------------------------------------------------------------------------------

    /// <remarks>
    /// An empty statement list, the same answer as a MSEL with nothing recorded against it and the same
    /// answer as xAPI being switched off. Three states, one body, no status code between them.
    /// </remarks>
    [Fact]
    public async Task GetStatements_ForAMselThatIsNotThere_IsAnEmptyStatementList()
    {
        var response = await Client(await Actor().SeedAsync())
            .GetAsync($"{XApiEndpointTests.Statements}?mselId={Guid.NewGuid()}", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("{\"statements\":[]}", await response.Content.ReadAsStringAsync(Ct));
    }

    /// <remarks>
    /// A statement's registration is the id of the thing it happened in - the MSEL's own id for
    /// blueprint, and the CITE evaluation, Steamfitter scenario, Player view or Gallery collection ids for
    /// the others. A MSEL that was never pushed to CITE has no evaluation id, so asking for its CITE
    /// statements produces no registrations at all and the LRS is never contacted. Nothing says so: the
    /// body is the same empty list an unreachable LRS produces.
    /// </remarks>
    [Fact]
    public async Task GetStatements_NamingAnIntegrationTheMselDoesNotUse_NeverReachesTheLrs()
    {
        var msel = BlueprintAppFactory.Msel();
        await Seed(msel);

        var response = await Client(await Actor().SeedAsync()).GetAsync(
            $"{XApiEndpointTests.Statements}?mselId={msel.Id}&source=cite", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("{\"statements\":[]}", await response.Content.ReadAsStringAsync(Ct));
    }

    /// <remarks>
    /// <para>
    /// The LRS is aimed at a closed port, so <c>httpClient.GetAsync</c> throws
    /// <c>HttpRequestException</c> - and nothing catches it. A non-success <em>status</em> from the LRS is
    /// logged and skipped (the loop <c>continue</c>s, and a caller sees an empty list), but an LRS that
    /// cannot be reached at all is a 500 carrying the transport error. The two failures a deployment is
    /// most likely to see are handled in opposite ways.
    /// </para>
    /// <para>
    /// Wrapping the <c>GetAsync</c> in the same <c>try</c> as the status check turns this red. Note the
    /// request itself cannot be observed by a test - <c>XApiService.cs:843</c> constructs its own
    /// <c>HttpClient</c> rather than taking the <c>IHttpClientFactory</c> the rest of blueprint's outbound
    /// calls go through - so this and the three early returns are the whole of what is reachable here.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task GetStatements_WhenTheLrsCannotBeReached_Is500()
    {
        var msel = BlueprintAppFactory.Msel();
        await Seed(msel);

        var response = await Client(await Actor().SeedAsync())
            .GetAsync($"{XApiEndpointTests.Statements}?mselId={msel.Id}", Ct);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Contains("127.0.0.1:1", await Title(response));
    }

    // -------------------------------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------------------------------

    /// <summary>
    /// The competency's <c>IdNumber</c>, and therefore the assertion statement's activity id: an
    /// <c>IdNumber</c> that starts with <c>http</c> is used as the IRI verbatim.
    /// </summary>
    private const string CompetencyIri = "https://competencies.test/c/17";

    private async Task<int> QueuedCount()
    {
        await using var context = NewContext();

        return await context.XApiQueuedStatements.CountAsync(Ct);
    }

    /// <summary>The one row the request queued, read back through a cold change tracker.</summary>
    private async Task<XApiQueuedStatementEntity> Queued()
    {
        await using var context = NewContext();

        return Assert.Single(await context.XApiQueuedStatements.ToListAsync(Ct));
    }

    private static JsonElement Statement(XApiQueuedStatementEntity row)
    {
        using var document = JsonDocument.Parse(row.StatementJson);

        return document.RootElement.Clone();
    }

    private static JsonElement At(JsonElement element, string path)
    {
        var current = element;

        foreach (var segment in path.Split('.'))
        {
            Assert.True(
                current.TryGetProperty(segment, out var next),
                $"'{path}' stops at '{segment}': {current}");
            current = next;
        }

        return current;
    }

    private static string Text(JsonElement element, string path) => At(element, path).GetString();

    /// <summary>
    /// <c>ApiError.Title</c>, which in Development carries the exception's own message for a 500.
    /// </summary>
    private async Task<string> Title(HttpResponseMessage response)
    {
        var error = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions, Ct);

        return error.GetProperty("title").GetString();
    }

    /// <summary>The ids of everything an assertion has to name: a MSEL, a competency and a scale.</summary>
    private sealed record AssertionGraph(Guid MselId, Guid CompetencyId, Guid LevelId);

    /// <summary>
    /// Seeds a MSEL, a framework, a competency and a three-point scale, and returns the ids an assertion
    /// naming the middle level needs. A thinner version of <c>XApiServiceTests.SeedAssertionGraph</c>,
    /// which varies the scale; here the only interesting thing about it is that the raw score is 3.
    /// </summary>
    private async Task<AssertionGraph> SeedAssertionGraph()
    {
        var msel = BlueprintAppFactory.Msel();
        var framework = BlueprintAppFactory.CompetencyFramework();
        framework.IdNumber = "https://frameworks.test/f/1";
        var competency = BlueprintAppFactory.Competency(framework.Id);
        competency.IdNumber = CompetencyIri;

        var scale = new ProficiencyScaleEntity
        {
            Id = Guid.NewGuid(),
            Name = $"scale-{Guid.NewGuid()}",
            Description = "Seeded by XApiLiveEndpointTests",
            CreatedBy = Guid.NewGuid(),
        };

        var levels = new[] { 1, 3, 5 }
            .Select((value, index) => new ProficiencyLevelEntity
            {
                Id = Guid.NewGuid(),
                ProficiencyScaleId = scale.Id,
                Name = $"level-{value}",
                Description = "Seeded by XApiLiveEndpointTests",
                Value = value,
                DisplayOrder = index,
                CreatedBy = Guid.NewGuid(),
            })
            .ToArray();

        await Seed(msel, framework, competency, scale);
        await Seed(levels);

        return new AssertionGraph(msel.Id, competency.Id, levels[1].Id);
    }
}
