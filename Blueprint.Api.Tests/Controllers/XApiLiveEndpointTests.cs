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
using Blueprint.Api.Data.Enumerations;
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
        var msel = TestData.Msel();
        await Seed(msel);
        var actor = await Actor().OnMsel(msel, MselRole.Viewer).SeedAsync();

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

    /// <summary><c>ViewMsels</c> lets a caller with no part in the MSEL record that they viewed it.</summary>
    [Fact]
    public async Task Viewed_WithViewMselsAndNoPartInTheMsel_QueuesTheStatement()
    {
        var msel = TestData.Msel();
        await Seed(msel);
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewMsels).SeedAsync();

        var response = await Client(actor).PostAsync(XApiEndpointTests.Viewed(msel.Id), null, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(msel.Id, (await Queued()).MselId);
    }

    [Fact]
    public async Task Viewed_WithEditMselsAndNoPartInTheMsel_Is403AndQueuesNothing()
    {
        var msel = TestData.Msel();
        await Seed(msel);
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();

        var response = await Client(actor).PostAsync(XApiEndpointTests.Viewed(msel.Id), null, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, await QueuedCount());
    }

    [Fact]
    public async Task Viewed_ByTheMselsCreator_QueuesTheStatement()
    {
        var actor = await Actor().SeedAsync();
        var msel = TestData.Msel(createdBy: actor.Id);
        await Seed(msel);

        var response = await Client(actor).PostAsync(XApiEndpointTests.Viewed(msel.Id), null, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(msel.Id, (await Queued()).MselId);
    }

    [Fact]
    public async Task Viewed_ByTheCreatorOfAnotherMsel_Is403AndQueuesNothing()
    {
        var actor = await Actor().SeedAsync();
        var theirs = TestData.Msel(createdBy: actor.Id);
        var msel = TestData.Msel();
        await Seed(theirs, msel);

        var response = await Client(actor).PostAsync(XApiEndpointTests.Viewed(msel.Id), null, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, await QueuedCount());
    }

    [Fact]
    public async Task Viewed_ByAParticipantOnTheMselsTeam_QueuesTheStatement()
    {
        var msel = TestData.Msel();
        await Seed(msel);
        var team = TestData.Team(msel.Id);
        await Seed(team);
        var actor = await Actor().OnTeam(team).SeedAsync();

        var response = await Client(actor).PostAsync(XApiEndpointTests.Viewed(msel.Id), null, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(msel.Id, (await Queued()).MselId);
    }

    [Fact]
    public async Task Viewed_ByAParticipantOnAnotherMselsTeam_Is403AndQueuesNothing()
    {
        var msel = TestData.Msel();
        await Seed(msel);
        var actor = await Actor().OnNewTeam().SeedAsync();

        var response = await Client(actor).PostAsync(XApiEndpointTests.Viewed(msel.Id), null, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, await QueuedCount());
    }

    [Fact]
    public async Task Viewed_ByAMemberOfAUnitOnTheMsel_QueuesTheStatement()
    {
        var msel = TestData.Msel();
        await Seed(msel);
        var actor = await Actor().InUnitOf(msel).SeedAsync();

        var response = await Client(actor).PostAsync(XApiEndpointTests.Viewed(msel.Id), null, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(msel.Id, (await Queued()).MselId);
    }

    [Fact]
    public async Task Viewed_ByAMemberOfAUnitOnAnotherMsel_Is403AndQueuesNothing()
    {
        var other = TestData.Msel();
        var msel = TestData.Msel();
        await Seed(other, msel);
        var actor = await Actor().InUnitOf(other).SeedAsync();

        var response = await Client(actor).PostAsync(XApiEndpointTests.Viewed(msel.Id), null, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
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

    /// <summary>Every member of the body binds and reaches the queued statement: the ids, the comment, the
    /// team, the scenario event, the move and the group.</summary>
    [Fact]
    public async Task CreateAssertion_QueuesAnAssertedStatement()
    {
        var graph = await SeedAssertionGraph();
        var actor = await Actor().OnMsel(graph.Msel, MselRole.Evaluator).SeedAsync();
        var scenarioEvent = TestData.ScenarioEvent(graph.MselId);
        var team = TestData.Team(graph.MselId);
        await Seed(scenarioEvent, team);

        var response = await Client(actor).PostAsJsonAsync(
            XApiEndpointTests.Assertions,
            new
            {
                mselId = graph.MselId,
                competencyId = graph.CompetencyId,
                scenarioEventId = scenarioEvent.Id,
                teamId = team.Id,
                proficiencyLevelId = graph.LevelId,
                comment = "held the line",
                moveNumber = AssertedMove,
                groupNumber = AssertedGroup
            },
            Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var row = await Queued();
        Assert.Equal("asserted", row.Verb);
        Assert.Equal(graph.MselId, row.MselId);
        Assert.Equal(team.Id, row.TeamId);
        Assert.Equal(CompetencyIri, row.ActivityId);

        var statement = Statement(row);
        Assert.Equal("https://w3id.org/xapi/tla/verbs/asserted", Text(statement, "verb.id"));
        Assert.Equal(CompetencyIri, Text(statement, "object.id"));
        Assert.Equal("held the line", Text(statement, "result.response"));
        Assert.Equal(3, At(statement, "result.score.raw").GetDouble());
        Assert.Equal(actor.Id.ToString(), Text(statement, "actor.account.name"));
        Assert.Equal(graph.MselId.ToString(), Text(statement, "context.registration"));
        Assert.Equal(team.Id.ToString(), Text(statement, "context.team.account.name"));

        var groupings = GroupingIds(statement);
        Assert.Contains(XApiEnabledFactory.ApiUrl + "scenarioevents/" + scenarioEvent.Id, groupings);
        Assert.Contains($"{XApiEnabledFactory.ApiUrl}msels/{graph.MselId}/moves/{AssertedMove}", groupings);
        Assert.Contains(
            $"{XApiEnabledFactory.ApiUrl}msels/{graph.MselId}/moves/{AssertedMove}/groups/{AssertedGroup}",
            groupings);
    }

    /// <summary>Only the MSEL's owners and evaluators may assert: a Viewer, an Editor, an Approver and a
    /// Move Editor are refused and nothing is queued.</summary>
    [Theory]
    [InlineData(MselRole.Viewer)]
    [InlineData(MselRole.Editor)]
    [InlineData(MselRole.Approver)]
    [InlineData(MselRole.MoveEditor)]
    public async Task CreateAssertion_ByACallerWhoIsNeitherOwnerNorEvaluator_Is403AndRecordsNothing(MselRole role)
    {
        var graph = await SeedAssertionGraph();
        var actor = await Actor().OnMsel(graph.Msel, role).SeedAsync();

        var response = await Client(actor).PostAsJsonAsync(
            XApiEndpointTests.Assertions,
            new { mselId = graph.MselId, competencyId = graph.CompetencyId, proficiencyLevelId = graph.LevelId },
            Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, await QueuedCount());
    }

    [Fact]
    public async Task CreateAssertion_ByAParticipantOnTheMselsTeam_Is403AndRecordsNothing()
    {
        var graph = await SeedAssertionGraph();
        var team = TestData.Team(graph.MselId);
        await Seed(team);

        var response = await Client(await Actor().OnTeam(team).SeedAsync()).PostAsJsonAsync(
            XApiEndpointTests.Assertions,
            new { mselId = graph.MselId, competencyId = graph.CompetencyId, proficiencyLevelId = graph.LevelId, teamId = team.Id },
            Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, await QueuedCount());
    }

    [Theory]
    [InlineData(MselRole.Owner)]
    [InlineData(MselRole.Evaluator)]
    public async Task CreateAssertion_ByAnOwnerOrEvaluator_IsRecorded(MselRole role)
    {
        var graph = await SeedAssertionGraph();

        var response = await Client(await Actor().OnMsel(graph.Msel, role).SeedAsync()).PostAsJsonAsync(
            XApiEndpointTests.Assertions,
            new { mselId = graph.MselId, competencyId = graph.CompetencyId, proficiencyLevelId = graph.LevelId },
            Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, await QueuedCount());
    }

    [Theory]
    [InlineData(MselRole.Owner)]
    [InlineData(MselRole.Evaluator)]
    public async Task CreateAssertion_ByAnOwnerOrEvaluatorOfAnotherMsel_Is403AndRecordsNothing(MselRole role)
    {
        var graph = await SeedAssertionGraph();
        var actor = await Actor().OnNewMsel(role).SeedAsync();

        var response = await Client(actor).PostAsJsonAsync(
            XApiEndpointTests.Assertions,
            new { mselId = graph.MselId, competencyId = graph.CompetencyId, proficiencyLevelId = graph.LevelId },
            Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, await QueuedCount());
    }

    /// <summary>The creator counts as the MSEL's owner.</summary>
    [Fact]
    public async Task CreateAssertion_ByTheMselsCreator_IsRecorded()
    {
        var actor = await Actor().SeedAsync();
        var graph = await SeedAssertionGraph(createdBy: actor.Id);

        var response = await Client(actor).PostAsJsonAsync(
            XApiEndpointTests.Assertions,
            new { mselId = graph.MselId, competencyId = graph.CompetencyId, proficiencyLevelId = graph.LevelId },
            Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(graph.MselId, (await Queued()).MselId);
    }

    [Fact]
    public async Task CreateAssertion_ByTheCreatorOfAnotherMsel_Is403AndRecordsNothing()
    {
        var actor = await Actor().SeedAsync();
        await Seed(TestData.Msel(createdBy: actor.Id));
        var graph = await SeedAssertionGraph();

        var response = await Client(actor).PostAsJsonAsync(
            XApiEndpointTests.Assertions,
            new { mselId = graph.MselId, competencyId = graph.CompetencyId, proficiencyLevelId = graph.LevelId },
            Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, await QueuedCount());
    }

    [Fact]
    public async Task CreateAssertion_WithViewMselsAndNoRoleOnTheMsel_Is403AndRecordsNothing()
    {
        var graph = await SeedAssertionGraph();
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewMsels).SeedAsync();

        var response = await Client(actor).PostAsJsonAsync(
            XApiEndpointTests.Assertions,
            new { mselId = graph.MselId, competencyId = graph.CompetencyId, proficiencyLevelId = graph.LevelId },
            Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, await QueuedCount());
    }

    [Fact]
    public async Task CreateAssertion_WithEditMselsAndNoRoleOnTheMsel_IsRecorded()
    {
        var graph = await SeedAssertionGraph();
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();

        var response = await Client(actor).PostAsJsonAsync(
            XApiEndpointTests.Assertions,
            new { mselId = graph.MselId, competencyId = graph.CompetencyId, proficiencyLevelId = graph.LevelId },
            Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, await QueuedCount());
    }

    // Same case as XApiServiceTests.AssertCompetency_AboutSomethingThatIsNotThere_Throws.
    [Fact]
    public async Task CreateAssertion_NamingATeamOfAnotherMsel_Is500NamingTheTeamAndRecordsNothing()
    {
        var graph = await SeedAssertionGraph();
        var otherMsel = TestData.Msel();
        await Seed(otherMsel);
        var otherTeam = TestData.Team(otherMsel.Id);
        await Seed(otherTeam);
        var actor = await Actor().OnMsel(graph.Msel, MselRole.Evaluator).SeedAsync();

        var response = await Client(actor).PostAsJsonAsync(
            XApiEndpointTests.Assertions,
            new
            {
                mselId = graph.MselId,
                competencyId = graph.CompetencyId,
                proficiencyLevelId = graph.LevelId,
                teamId = otherTeam.Id,
            },
            Ct);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal($"Team {otherTeam.Id} not found on MSEL {graph.MselId}", await Title(response));
        Assert.Equal(0, await QueuedCount());
    }

    // Same case as XApiServiceTests.AssertCompetency_AboutSomethingThatIsNotThere_Throws.
    [Fact]
    public async Task CreateAssertion_NamingAScenarioEventOfAnotherMsel_Is500NamingTheEventAndRecordsNothing()
    {
        var graph = await SeedAssertionGraph();
        var otherMsel = TestData.Msel();
        await Seed(otherMsel);
        var otherEvent = TestData.ScenarioEvent(otherMsel.Id);
        await Seed(otherEvent);
        var actor = await Actor().OnMsel(graph.Msel, MselRole.Evaluator).SeedAsync();

        var response = await Client(actor).PostAsJsonAsync(
            XApiEndpointTests.Assertions,
            new
            {
                mselId = graph.MselId,
                competencyId = graph.CompetencyId,
                proficiencyLevelId = graph.LevelId,
                scenarioEventId = otherEvent.Id,
            },
            Ct);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal(
            $"Scenario event {otherEvent.Id} not found on MSEL {graph.MselId}", await Title(response));
        Assert.Equal(0, await QueuedCount());
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

        var response = await Client(await Actor().OnMsel(msel, MselRole.Evaluator).SeedAsync()).GetAsync(
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

        var response = await Client(await Actor().OnMsel(msel, MselRole.Evaluator).SeedAsync())
            .GetAsync($"{XApiEndpointTests.Statements}?mselId={msel.Id}", Ct);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Contains("127.0.0.1:1", await Title(response));
    }

    /// <summary>The statements go to the roles that can open the Assessor View and to nobody else on the
    /// MSEL: a Viewer and a Move Editor are refused.</summary>
    [Theory]
    [InlineData(MselRole.Viewer)]
    [InlineData(MselRole.MoveEditor)]
    public async Task GetStatements_ByACallerOutsideTheAssessorRoles_Is403(MselRole role)
    {
        var msel = TestData.Msel();
        await Seed(msel);
        var actor = await Actor().OnMsel(msel, role).SeedAsync();

        var response = await Client(actor)
            .GetAsync($"{XApiEndpointTests.Statements}?mselId={msel.Id}&source=cite", Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task GetStatements_ByAParticipantOnTheMselsTeam_Is403()
    {
        var msel = TestData.Msel();
        await Seed(msel);
        var team = TestData.Team(msel.Id);
        await Seed(team);

        var response = await Client(await Actor().OnTeam(team).SeedAsync())
            .GetAsync($"{XApiEndpointTests.Statements}?mselId={msel.Id}&source=cite", Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary><c>source=cite</c> on a MSEL never pushed to CITE produces no registrations, so a 200 means
    /// the caller passed authorization without the request reaching the LRS.</summary>
    [Theory]
    [InlineData(MselRole.Owner)]
    [InlineData(MselRole.Editor)]
    [InlineData(MselRole.Approver)]
    [InlineData(MselRole.Evaluator)]
    public async Task GetStatements_ByAnAssessorRole_IsAllowed(MselRole role)
    {
        var msel = TestData.Msel();
        await Seed(msel);

        var response = await Client(await Actor().OnMsel(msel, role).SeedAsync())
            .GetAsync($"{XApiEndpointTests.Statements}?mselId={msel.Id}&source=cite", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("{\"statements\":[]}", await response.Content.ReadAsStringAsync(Ct));
    }

    [Theory]
    [InlineData(MselRole.Owner)]
    [InlineData(MselRole.Editor)]
    [InlineData(MselRole.Approver)]
    [InlineData(MselRole.Evaluator)]
    public async Task GetStatements_ByAnAssessorRoleOnAnotherMsel_Is403(MselRole role)
    {
        var msel = TestData.Msel();
        await Seed(msel);
        var actor = await Actor().OnNewMsel(role).SeedAsync();

        var response = await Client(actor)
            .GetAsync($"{XApiEndpointTests.Statements}?mselId={msel.Id}&source=cite", Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>The creator counts as the MSEL's owner.</summary>
    [Fact]
    public async Task GetStatements_ByTheMselsCreator_IsAllowed()
    {
        var actor = await Actor().SeedAsync();
        var msel = TestData.Msel(createdBy: actor.Id);
        await Seed(msel);

        var response = await Client(actor)
            .GetAsync($"{XApiEndpointTests.Statements}?mselId={msel.Id}&source=cite", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("{\"statements\":[]}", await response.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task GetStatements_ByTheCreatorOfAnotherMsel_Is403()
    {
        var actor = await Actor().SeedAsync();
        var theirs = TestData.Msel(createdBy: actor.Id);
        var msel = TestData.Msel();
        await Seed(theirs, msel);

        var response = await Client(actor)
            .GetAsync($"{XApiEndpointTests.Statements}?mselId={msel.Id}&source=cite", Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task GetStatements_WithEditMselsAndNoRoleOnTheMsel_Is403()
    {
        var msel = TestData.Msel();
        await Seed(msel);
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();

        var response = await Client(actor)
            .GetAsync($"{XApiEndpointTests.Statements}?mselId={msel.Id}&source=cite", Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task GetStatements_WithViewMselsAndNoRoleOnTheMsel_IsAllowed()
    {
        var msel = TestData.Msel();
        await Seed(msel);
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewMsels).SeedAsync();

        var response = await Client(actor)
            .GetAsync($"{XApiEndpointTests.Statements}?mselId={msel.Id}&source=cite", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // -------------------------------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------------------------------

    /// <summary>
    /// The competency's <c>IdNumber</c>, and therefore the assertion statement's activity id: an
    /// <c>IdNumber</c> that starts with <c>http</c> is used as the IRI verbatim.
    /// </summary>
    private const string CompetencyIri = "https://competencies.test/c/17";

    /// <summary>A move number other than the default zero, so its grouping is told apart from a missing one.</summary>
    private const int AssertedMove = 2;

    /// <summary>A group number other than the move number, so the two cannot be confused in a grouping id.</summary>
    private const int AssertedGroup = 3;

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

    /// <summary>The ids of the statement's <c>context.contextActivities.grouping</c> activities.</summary>
    private static string[] GroupingIds(JsonElement statement) =>
        At(statement, "context.contextActivities.grouping")
            .EnumerateArray()
            .Select(x => x.GetProperty("id").GetString())
            .ToArray();

    /// <summary>
    /// <c>ApiError.Title</c>, which in Development carries the exception's own message for a 500.
    /// </summary>
    private async Task<string> Title(HttpResponseMessage response)
    {
        var error = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions, Ct);

        return error.GetProperty("title").GetString();
    }

    /// <summary>The ids of everything an assertion has to name: a MSEL, a competency and a scale.</summary>
    private sealed record AssertionGraph(MselEntity Msel, Guid CompetencyId, Guid LevelId)
    {
        public Guid MselId => Msel.Id;
    }

    /// <summary>
    /// Seeds a MSEL, a framework, a competency and a three-point scale, and returns the ids an assertion
    /// naming the middle level needs. A thinner version of <c>XApiServiceTests.SeedAssertionGraph</c>,
    /// which varies the scale; here the only interesting thing about it is that the raw score is 3.
    /// </summary>
    private async Task<AssertionGraph> SeedAssertionGraph(Guid? createdBy = null)
    {
        var msel = TestData.Msel(createdBy: createdBy);
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

        return new AssertionGraph(msel, competency.Id, levels[1].Id);
    }
}
