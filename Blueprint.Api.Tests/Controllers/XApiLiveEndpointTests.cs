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

/// <summary>The same four routes over the real <c>XApiService</c>, with xAPI switched on, so a request can
/// be followed to the row it leaves in the queue.</summary>
public class XApiLiveEndpointTests(DatabaseFixture fixture, XApiEnabledFactory factory)
    : ApiTestBase(fixture, factory), IClassFixture<XApiEnabledFactory>
{
    [Fact]
    public async Task Viewed_QueuesAViewedStatementNamingTheMselAndTheCaller()
    {
        var actor = await Actor().SeedAsync();
        var msel = TestData.Msel();
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
        Assert.Equal(TestConfiguration.Issuer, Text(statement, "actor.account.homePage"));
    }

    /// <summary>Viewing a MSEL that is not there is answered 200 and queues nothing.</summary>
    [Fact]
    public async Task Viewed_ForAMselThatIsNotThere_Is200AndQueuesNothing()
    {
        var response = await Client(await Actor().SeedAsync())
            .PostAsync(XApiEndpointTests.Viewed(Guid.NewGuid()), null, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, await QueuedCount());
    }

    /// <summary>The join-page view queues a statement with no MSEL and no registration.</summary>
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
    public async Task CreateAssertion_WithNoBody_Is400AndQueuesNothing()
    {
        using var content = new StringContent(string.Empty, Encoding.UTF8, "application/json");

        var response = await Client(await Actor().SeedAsync()).PostAsync(XApiEndpointTests.Assertions, content, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, await QueuedCount());
    }

    /// <summary>An empty JSON object binds with all-zero ids and is answered with a 500 naming the all-zeros MSEL.</summary>
    [Fact]
    public async Task CreateAssertion_WithAnEmptyJsonObject_Is500NamingTheAllZerosMsel()
    {
        using var content = new StringContent("{}", Encoding.UTF8, "application/json");

        var response = await Client(await Actor().SeedAsync()).PostAsync(XApiEndpointTests.Assertions, content, Ct);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal($"MSEL {Guid.Empty} not found", await Title(response));
        Assert.Equal(0, await QueuedCount());
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

    /// <summary>An assertion about a MSEL the caller has no role on is queued.</summary>
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

    // Same case as XApiServiceTests.AssertCompetency_AboutSomethingThatIsNotThere_Throws.
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

    // Same case as XApiServiceTests.GetStatements_ForAMselThatIsNotThere_AnswersAnEmptyStatementListToo.
    [Fact]
    public async Task GetStatements_ForAMselThatIsNotThere_IsAnEmptyStatementList()
    {
        var response = await Client(await Actor().SeedAsync())
            .GetAsync($"{XApiEndpointTests.Statements}?mselId={Guid.NewGuid()}", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("{\"statements\":[]}", await response.Content.ReadAsStringAsync(Ct));
    }

    /// <summary>Statements for an integration the MSEL does not use are an empty list, with no LRS
    /// request.</summary>
    [Fact]
    public async Task GetStatements_NamingAnIntegrationTheMselDoesNotUse_NeverReachesTheLrs()
    {
        var msel = TestData.Msel();
        await Seed(msel);

        var response = await Client(await Actor().SeedAsync()).GetAsync(
            $"{XApiEndpointTests.Statements}?mselId={msel.Id}&source=cite", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("{\"statements\":[]}", await response.Content.ReadAsStringAsync(Ct));
    }

    // Same case as XApiServiceTests.GetStatements_WithAnLrsThatCannotBeReached_ThrowsWhereANonSuccessStatusIsSwallowed.
    [Fact]
    public async Task GetStatements_WhenTheLrsCannotBeReached_Is500()
    {
        var msel = TestData.Msel();
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
        var msel = TestData.Msel();
        var framework = TestData.CompetencyFramework();
        framework.IdNumber = "https://frameworks.test/f/1";
        var competency = TestData.Competency(framework.Id);
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
