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
using Blueprint.Api.Tests.Support;
using Blueprint.Api.ViewModels;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Xunit;

namespace Blueprint.Api.Tests.Controllers;

/// <summary><c>XApiController</c> - the four routes blueprint's UI calls to record what a participant did
/// and to read back what the LRS holds.</summary>
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

        var response = await Client().SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>Every route for a caller with no permissions is answered with a 200.</summary>
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

    /// <summary>The statements route passes the request's cancellation token on.</summary>
    [Fact]
    public async Task GetStatements_PassesTheRequestsCancellationToken()
    {
        StatementsAre("{}");

        var client = Client(await Actor().SeedAsync());

        await client.GetAsync(Statements, Ct);

        await Factory.XApi.Received(1).GetStatementsAsync(
            Arg.Any<Guid>(),
            Arg.Any<DateTime?>(),
            Arg.Any<DateTime?>(),
            Arg.Any<int>(),
            Arg.Any<string>(),
            Cancellable);
    }

    // -------------------------------------------------------------------------------------------------
    // GET xapi/statements
    // -------------------------------------------------------------------------------------------------

    /// <summary>The statements route answers the LRS document verbatim.</summary>
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

    /// <summary>With only a MSEL id the route asks for the hundred most recent statements from every
    /// source.</summary>
    [Fact]
    public async Task GetStatements_WithOnlyAMselId_AsksForAHundredStatementsFromEverySource()
    {
        StatementsAre("{}");

        var mselId = Guid.NewGuid();

        await Client(await Actor().SeedAsync()).GetAsync($"{Statements}?mselId={mselId}", Ct);

        await Factory.XApi.Received(1).GetStatementsAsync(
            Arg.Is(mselId), Missing, Missing, Arg.Is(100), NoSource, Cancellable);
    }

    /// <summary>A request naming no MSEL asks about the all-zeros MSEL.</summary>
    [Fact]
    public async Task GetStatements_NamingNoMselAtAll_AsksAboutTheAllZerosMsel()
    {
        StatementsAre("{\"statements\":[]}");

        var response = await Client(await Actor().SeedAsync()).GetAsync(Statements, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await Factory.XApi.Received(1).GetStatementsAsync(
            Arg.Is(Guid.Empty), Missing, Missing, Arg.Is(100), NoSource, Cancellable);
    }

    /// <summary>A null answer from the service is a 200 with an empty body.</summary>
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

    /// <summary>The assertions route passes the request's cancellation token on.</summary>
    [Fact]
    public async Task CreateAssertion_PassesTheRequestsCancellationToken()
    {
        var client = Client(await Actor().SeedAsync());

        await client.PostAsJsonAsync(Assertions, new { mselId = Guid.NewGuid() }, Ct);

        await Factory.XApi.Received(1)
            .AssertCompetencyAsync(Arg.Any<CompetencyAssertion>(), Cancellable);
    }

    // Same case as XApiLiveEndpointTests.CreateAssertion_WithAnEmptyJsonObject_Is500NamingTheAllZerosMsel.
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

    /// <summary>The MSEL-viewed route passes the route id and the request's cancellation token on.</summary>
    [Fact]
    public async Task Viewed_PassesTheRequestsCancellationToken()
    {
        var client = Client(await Actor().SeedAsync());
        var mselId = Guid.NewGuid();

        await client.PostAsync(Viewed(mselId), null, Ct);

        await Factory.XApi.Received(1).MselViewedAsync(mselId, Cancellable);
    }

    /// <summary>The join-page route passes the request's cancellation token on.</summary>
    [Fact]
    public async Task ViewedJoinPage_PassesTheRequestsCancellationToken()
    {
        var client = Client(await Actor().SeedAsync());

        await client.PostAsync(ViewedJoinPage, null, Ct);

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
    /// A token the request could actually have been aborted with: <c>Arg.Any</c> also accepts
    /// <c>CancellationToken.None</c>, so it cannot tell a forwarded token from a dropped one.
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
