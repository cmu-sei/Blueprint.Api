// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Security.Claims;
using System.Text.Json;
using System.Threading.Tasks;
using Blueprint.Api.Data.Enumerations;
using Blueprint.Api.Data.Models;
using Blueprint.Api.Infrastructure.Options;
using Blueprint.Api.Services;
using Blueprint.Api.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Sdk;

namespace Blueprint.Api.Tests.Services;

/// <summary><c>XApiService</c> - the eleven methods that turn something that happened in blueprint into a
/// TinCan statement and hand it to <c>XApiQueueService</c>. Everything an installation learns about what
/// its participants did comes out of this file, so what a statement says is the whole contract.</summary>
public class XApiServiceTests(DatabaseFixture fixture) : DatabaseTestBase(fixture)
{
    /// <summary>Starts describing an actor to seed over this test's database.</summary>
    private TestActorBuilder Actor() => new(Db, Ct);

    private const string ApiUrl = "https://blueprint.test/api/";

    /// <summary>The UI's base url, written the way this service wants it: with no trailing slash.</summary>
    private const string UiUrl = "https://blueprint.test";

    /// <summary>
    /// The same url after <c>new Uri(UiUrl)</c>, which is how a team's <c>account.homePage</c> is built -
    /// and <c>Uri</c> supplies the root path the option deliberately does not carry.
    /// </summary>
    private const string UiHome = "https://blueprint.test/";

    private const string Issuer = "https://id.test/realms/crucible";

    private static readonly Guid UserId = Guid.NewGuid();

    private RecordingLogger<XApiService> Log { get; } = new();

    // -------------------------------------------------------------------------------------------------
    // IsConfigured, and what an unconfigured service does
    // -------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(true, "lrs-user", true)]
    [InlineData(false, "lrs-user", false)]
    [InlineData(true, "", false)]
    [InlineData(true, null, false)]
    public void IsConfigured_NeedsBothTheFlagAndAUsername(bool enabled, string username, bool expected) =>
        Assert.Equal(expected, Service(Options(enabled: enabled, username: username)).IsConfigured());

    /// <summary>With x API turned off the statement is not queued and the caller is told it succeeded.</summary>
    [Fact]
    public async Task WithXApiTurnedOff_TheStatementIsNotQueuedAndTheCallerIsToldItSucceeded()
    {
        var msel = TestData.Msel();
        await Seed(msel);

        Assert.True(await Service(Options(enabled: false)).MselViewedAsync(msel, Ct));
        Assert.Equal(0, await QueuedCount());
    }

    [Fact]
    public async Task WithNoLrsUsername_NothingIsQueuedEither()
    {
        var msel = TestData.Msel();
        await Seed(msel);

        Assert.True(await Service(Options(username: null)).MselViewedAsync(msel, Ct));
        Assert.Equal(0, await QueuedCount());
    }

    // -------------------------------------------------------------------------------------------------
    // The statement CreateAsync builds
    // -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task MselViewed_QueuesAViewedStatementNamingTheMsel()
    {
        var msel = TestData.Msel();
        await Seed(msel);

        await Service().MselViewedAsync(msel, Ct);

        var row = await Queued();
        Assert.Equal(XApiQueueStatus.Pending, row.Status);
        Assert.Equal("viewed", row.Verb);
        Assert.Equal($"{ApiUrl}msel/{msel.Id}", row.ActivityId);
        Assert.Equal(msel.Id, row.MselId);
        Assert.Null(row.TeamId);

        var statement = Json(row);
        Assert.Equal("http://id.tincanapi.com/verb/viewed", Text(statement, "verb.id"));
        Assert.Equal("viewed", Text(statement, "verb.display.en-US"));
        Assert.Equal($"{ApiUrl}msel/{msel.Id}", Text(statement, "object.id"));
        Assert.Equal(msel.Name, Text(statement, "object.definition.name.en-US"));
        Assert.Equal(msel.Description, Text(statement, "object.definition.description.en-US"));
        Assert.Equal(
            "http://adlnet.gov/expapi/activities/simulation",
            Text(statement, "object.definition.type"));
        Assert.Equal($"{UiUrl}/msel/{msel.Id}", Text(statement, "object.definition.moreInfo"));
        Assert.Equal(msel.Id.ToString(), Text(statement, "context.registration"));
    }

    [Fact]
    public async Task TheStatementsPlatformAndLanguageComeFromTheOptions()
    {
        var msel = TestData.Msel();
        await Seed(msel);

        await Service(Options(platform: "Crucible")).MselViewedAsync(msel, Ct);

        var statement = await QueuedStatement();
        Assert.Equal("Crucible", Text(statement, "context.platform"));
        Assert.Equal("en-US", Text(statement, "context.language"));
    }

    [Fact]
    public async Task TheActorIsTheCallerNamedFromTheUsersTable()
    {
        var actor = await Actor().WithId(UserId).WithName("Ada Lovelace").SeedAsync();
        var msel = TestData.Msel();
        await Seed(msel);

        await Service(user: Principal(actor.Id)).MselViewedAsync(msel, Ct);

        var statement = await QueuedStatement();
        Assert.Equal("Ada Lovelace", Text(statement, "actor.name"));
        Assert.Equal(actor.Id.ToString(), Text(statement, "actor.account.name"));
        Assert.Equal(Issuer, Text(statement, "actor.account.homePage"));
    }

    /// <summary>A caller with no user row is sent as an account with no name.</summary>
    [Fact]
    public async Task ForACallerWithNoUserRow_TheActorHasAnAccountAndNoName()
    {
        var msel = TestData.Msel();
        await Seed(msel);

        await Service().MselViewedAsync(msel, Ct);

        var statement = await QueuedStatement();
        Assert.False(At(statement, "actor").TryGetProperty("name", out _));
        Assert.Equal(UserId.ToString(), Text(statement, "actor.account.name"));
    }

    [Theory]
    [InlineData(null, Issuer, Issuer)]
    [InlineData("https://configured.test/", Issuer, "https://configured.test/")]
    [InlineData(null, "id.test", "http://id.test/")]
    public async Task TheAccountsHomePageIsTheConfiguredIssuerOrTheTokens(
        string configured, string iss, string expected)
    {
        var msel = TestData.Msel();
        await Seed(msel);

        await Service(Options(issuerUrl: configured), Principal(UserId, iss)).MselViewedAsync(msel, Ct);

        Assert.Equal(expected, Text(await QueuedStatement(), "actor.account.homePage"));
    }

    /// <summary>A principal without a <c>sub</c> claim throws before anything is queued.</summary>
    [Fact]
    public async Task WithNoSubClaim_QueuingAStatementThrows()
    {
        var msel = TestData.Msel();
        await Seed(msel);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Service(user: Principal(UserId, sub: false)).MselViewedAsync(msel, Ct));

        Assert.Equal(0, await QueuedCount());
    }

    /// <summary>With no iss claim queuing a statement throws even when the issuer is configured.</summary>
    [Fact]
    public async Task WithNoIssClaim_QueuingAStatementThrowsEvenWhenTheIssuerIsConfigured()
    {
        var msel = TestData.Msel();
        await Seed(msel);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Service(Options(issuerUrl: "https://configured.test/"), Principal(UserId, iss: null))
                .MselViewedAsync(msel, Ct));

        Assert.Equal(0, await QueuedCount());
    }

    /// <summary>With no UI url the activitys more info points at the local filesystem.</summary>
    [Fact]
    public async Task WithNoUiUrl_TheActivitysMoreInfoPointsAtTheLocalFilesystem()
    {
        var msel = TestData.Msel();
        await Seed(msel);

        await Service(Options(uiUrl: string.Empty)).MselViewedAsync(msel, Ct);

        Assert.Equal(
            $"file:///msel/{msel.Id}",
            Text(await QueuedStatement(), "object.definition.moreInfo"));
    }

    /// <summary>With no <c>UiUrl</c> a statement naming a team throws.</summary>
    [Fact]
    public async Task WithNoUiUrl_AStatementNamingATeamThrows()
    {
        var msel = TestData.Msel();
        var team = TestData.Team(msel.Id);
        await Seed(msel, team);
        var actor = await Actor().WithId(UserId).OnTeam(team).SeedAsync();

        await Assert.ThrowsAsync<UriFormatException>(
            () => Service(Options(uiUrl: string.Empty), Principal(actor.Id)).MselViewedAsync(msel, Ct));

        Assert.Equal(0, await QueuedCount());
    }

    /// <summary>An API url with no trailing slash produces a malformed activity id.</summary>
    [Fact]
    public async Task AnApiUrlWithNoTrailingSlash_ProducesAMalformedActivityId()
    {
        var msel = TestData.Msel();
        await Seed(msel);

        await Service(Options(apiUrl: "https://blueprint.test/api")).MselViewedAsync(msel, Ct);

        Assert.Equal($"https://blueprint.test/apimsel/{msel.Id}", (await Queued()).ActivityId);
    }

    /// <summary>A UI url with a trailing slash produces a double slashed more info url.</summary>
    [Fact]
    public async Task AUiUrlWithATrailingSlash_ProducesADoubleSlashedMoreInfoUrl()
    {
        var msel = TestData.Msel();
        await Seed(msel);

        await Service(Options(uiUrl: "https://blueprint.test/")).MselViewedAsync(msel, Ct);

        Assert.Equal(
            $"https://blueprint.test//msel/{msel.Id}",
            Text(await QueuedStatement(), "object.definition.moreInfo"));
    }

    [Fact]
    public async Task TheStatementIsCategorizedByWhatTheMselIsBeingUsedFor()
    {
        var msel = TestData.Msel();
        await Seed(msel);

        await Service().MselViewedAsync(msel, Ct);

        var category = Assert.Single(Items(await QueuedStatement(), "context.contextActivities.category"));
        Assert.Equal($"{ApiUrl}category/planning", Text(category, "id"));
        Assert.Equal("Planning", Text(category, "definition.name.en-US"));
        Assert.Equal(
            "http://id.tincanapi.com/activitytype/category", Text(category, "definition.type"));
    }

    /// <summary>The statement names no parent and no other activity.</summary>
    [Fact]
    public async Task TheStatementNamesNoParentAndNoOtherActivity()
    {
        var msel = TestData.Msel();
        await Seed(msel);

        await Service().MselViewedAsync(msel, Ct);

        var contextActivities = At(await QueuedStatement(), "context.contextActivities");
        Assert.False(contextActivities.TryGetProperty("parent", out _));
        Assert.False(contextActivities.TryGetProperty("other", out _));
    }

    [Theory]
    [InlineData("a description of the exercise")]
    [InlineData(null)]
    public async Task TheActivitysDescriptionIsTheMselsOrAConstant(string description)
    {
        var msel = TestData.Msel();
        msel.Description = description;
        await Seed(msel);

        await Service().MselViewedAsync(msel, Ct);

        Assert.Equal(
            description ?? "Mission Scenario Event List",
            Text(await QueuedStatement(), "object.definition.description.en-US"));
    }

    // -------------------------------------------------------------------------------------------------
    // The team a statement is attributed to
    // -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ForACallerOnOneOfTheMselsTeams_TheStatementNamesThatTeam()
    {
        var msel = TestData.Msel();
        var team = TestData.Team(msel.Id);
        await Seed(msel, team);
        var actor = await Actor().WithId(UserId).OnTeam(team).SeedAsync();

        await Service(user: Principal(actor.Id)).MselViewedAsync(msel, Ct);

        var row = await Queued();
        Assert.Equal(team.Id, row.TeamId);

        var statement = Json(row);
        Assert.Equal(team.ShortName, Text(statement, "context.team.name"));
        Assert.Equal(UiHome, Text(statement, "context.team.account.homePage"));
        Assert.Equal(team.Id.ToString(), Text(statement, "context.team.account.name"));
        Assert.False(At(statement, "context.team").TryGetProperty("mbox", out _));
    }

    /// <summary>The teams mailbox is built from its short name and then discarded.</summary>
    [Fact]
    public async Task TheTeamsMailboxIsBuiltFromItsShortNameAndThenDiscarded()
    {
        var msel = TestData.Msel();
        var team = TestData.Team(msel.Id);
        team.ShortName = "blue";
        await Seed(msel, team);
        var actor = await Actor().WithId(UserId).OnTeam(team).SeedAsync();

        await Service(Options(emailDomain: "exercise.test"), Principal(actor.Id))
            .MselViewedAsync(msel, Ct);

        var group = At(await QueuedStatement(), "context.team");
        Assert.False(group.TryGetProperty("mbox", out _));
        Assert.Equal(team.Id.ToString(), Text(group, "account.name"));
    }

    /// <summary>The teams only member is the caller.</summary>
    [Fact]
    public async Task TheTeamsOnlyMemberIsTheCaller()
    {
        var msel = TestData.Msel();
        var team = TestData.Team(msel.Id);
        await Seed(msel, team);
        var actor = await Actor().WithId(UserId).WithName("Ada Lovelace").OnTeam(team).SeedAsync();
        await Actor().WithName("Somebody Else").OnTeam(team).SeedAsync();

        await Service(user: Principal(actor.Id)).MselViewedAsync(msel, Ct);

        var member = Assert.Single(Items(await QueuedStatement(), "context.team.member"));
        Assert.Equal("Ada Lovelace", Text(member, "name"));
    }

    [Fact]
    public async Task ForACallerOnAnotherMselsTeam_TheStatementNamesNoTeam()
    {
        var msel = TestData.Msel();
        var other = TestData.Msel();
        var team = TestData.Team(other.Id);
        await Seed(msel, other, team);
        var actor = await Actor().WithId(UserId).OnTeam(team).SeedAsync();

        await Service(user: Principal(actor.Id)).MselViewedAsync(msel, Ct);

        var row = await Queued();
        Assert.Null(row.TeamId);
        Assert.False(At(Json(row), "context").TryGetProperty("team", out _));
    }

    /// <summary>For a caller on two of the MSELs teams one is chosen arbitrarily.</summary>
    [Fact]
    public async Task ForACallerOnTwoOfTheMselsTeams_OneIsChosenArbitrarily()
    {
        var msel = TestData.Msel();
        var first = TestData.Team(msel.Id);
        var second = TestData.Team(msel.Id);
        await Seed(msel, first, second);
        var actor = await Actor().WithId(UserId).OnTeam(first).OnTeam(second).SeedAsync();

        await Service(user: Principal(actor.Id)).MselViewedAsync(msel, Ct);

        Assert.Contains((await Queued()).TeamId, new Guid?[] { first.Id, second.Id });
    }

    /// <summary>A caller on none of the MSEL's teams is sent with no team.</summary>
    [Fact]
    public async Task ForACallerOnNoTeamAtAll_TheStatementNamesNoTeam()
    {
        var msel = TestData.Msel();
        await Seed(msel, TestData.Team(msel.Id));

        await Service().MselViewedAsync(msel, Ct);

        Assert.Null((await Queued()).TeamId);
    }

    // -------------------------------------------------------------------------------------------------
    // The integrations a statement is grouped under
    // -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheStatementIsGroupedUnderEveryIntegrationTheMselIsPushedTo()
    {
        var msel = TestData.Msel();
        msel.PlayerViewId = Guid.NewGuid();
        msel.GalleryExhibitId = Guid.NewGuid();
        msel.CiteEvaluationId = Guid.NewGuid();
        msel.SteamfitterScenarioId = Guid.NewGuid();
        await Seed(msel);

        await Service(clients: Clients(
                player: "https://player.test/",
                gallery: "https://gallery.test/",
                cite: "https://cite.test/",
                steamfitter: "https://steamfitter.test/"))
            .MselViewedAsync(msel, Ct);

        var grouping = Items(await QueuedStatement(), "context.contextActivities.grouping")
            .Select(x => Text(x, "id"))
            .ToList();

        Assert.Equal(
            [
                $"https://player.test/api/views/{msel.PlayerViewId}",
                $"https://gallery.test/api/exhibits/{msel.GalleryExhibitId}",
                $"https://cite.test/api/evaluations/{msel.CiteEvaluationId}",
                $"https://steamfitter.test/api/scenarios/{msel.SteamfitterScenarioId}",
            ],
            grouping);
    }

    /// <summary>An integration's api url gives the same activity id with or without a trailing slash.</summary>
    [Fact]
    public async Task AnIntegrationsApiUrlIsNormalizedWhetherItEndsInASlashOrNot()
    {
        var msel = TestData.Msel();
        msel.PlayerViewId = Guid.NewGuid();
        await Seed(msel);

        await Service(clients: Clients(player: "https://player.test")).MselViewedAsync(msel, Ct);

        var grouping = Assert.Single(Items(await QueuedStatement(), "context.contextActivities.grouping"));
        Assert.Equal($"https://player.test/api/views/{msel.PlayerViewId}", Text(grouping, "id"));
        Assert.Equal("Player View", Text(grouping, "definition.name.en-US"));
        Assert.Equal(
            "http://id.tincanapi.com/activitytype/resource", Text(grouping, "definition.type"));
    }

    /// <summary>An integration with no configured url adds no grouping, and a statement with none has no
    /// <c>grouping</c> property.</summary>
    [Fact]
    public async Task AnIntegrationWhoseUrlIsNotConfigured_IsNotGroupedUnder()
    {
        var msel = TestData.Msel();
        msel.PlayerViewId = Guid.NewGuid();
        await Seed(msel);

        await Service().MselViewedAsync(msel, Ct);

        Assert.False(
            At(await QueuedStatement(), "context.contextActivities")
                .TryGetProperty("grouping", out _));
    }

    [Fact]
    public async Task AMselPushedNowhere_IsGroupedUnderNothing()
    {
        var msel = TestData.Msel();
        await Seed(msel);

        await Service(clients: Clients(player: "https://player.test/")).MselViewedAsync(msel, Ct);

        Assert.False(
            At(await QueuedStatement(), "context.contextActivities")
                .TryGetProperty("grouping", out _));
    }

    // -------------------------------------------------------------------------------------------------
    // The other four verbs
    // -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ExerciseStarted_QueuesALaunchedStatementUnderExecution()
    {
        var msel = TestData.Msel();
        await Seed(msel);

        await Service().ExerciseStartedAsync(msel, Ct);

        var statement = await QueuedStatement();
        Assert.Equal("http://adlnet.gov/expapi/verbs/launched", Text(statement, "verb.id"));
        Assert.Equal(
            $"{ApiUrl}category/execution",
            Text(Assert.Single(Items(statement, "context.contextActivities.category")), "id"));
    }

    [Fact]
    public async Task ExerciseStopped_QueuesATerminatedStatement()
    {
        var msel = TestData.Msel();
        await Seed(msel);

        await Service().ExerciseStoppedAsync(msel, Ct);

        Assert.Equal(
            "http://adlnet.gov/expapi/verbs/terminated", Text(await QueuedStatement(), "verb.id"));
    }

    [Fact]
    public async Task MselJoined_QueuesAJoinStatement()
    {
        var msel = TestData.Msel();
        await Seed(msel);

        await Service().MselJoinedAsync(msel, Ct);

        var row = await Queued();
        Assert.Equal("join", row.Verb);
        Assert.Equal("http://activitystrea.ms/schema/1.0/join", Text(Json(row), "verb.id"));
    }

    /// <summary>The join-page statement has no registration and its queue row no <c>MselId</c>.</summary>
    [Fact]
    public async Task JoinPageViewed_QueuesAStatementBelongingToNoMsel()
    {
        await Service().JoinPageViewedAsync(Ct);

        var row = await Queued();
        Assert.Null(row.MselId);
        Assert.Null(row.TeamId);
        Assert.Equal($"{ApiUrl}page/join-page", row.ActivityId);

        var statement = Json(row);
        Assert.Equal("http://id.tincanapi.com/verb/viewed", Text(statement, "verb.id"));
        Assert.Equal("Join Event Page", Text(statement, "object.definition.name.en-US"));
        Assert.False(At(statement, "context").TryGetProperty("registration", out _));
    }

    /// <summary>The queue row's <c>Verb</c> is the last segment of the verb IRI.</summary>
    [Fact]
    public async Task TheQueuesVerbColumnIsTheLastSegmentOfTheVerbIri()
    {
        var msel = TestData.Msel();
        await Seed(msel);
        var service = Service();

        await service.MselViewedAsync(msel, Ct);
        await service.ExerciseStartedAsync(msel, Ct);
        await service.ExerciseStoppedAsync(msel, Ct);
        await service.MselJoinedAsync(msel, Ct);
        await service.JoinPageViewedAsync(Ct);

        await using var context = NewContext();
        var verbs = await context.XApiQueuedStatements
            .OrderBy(x => x.QueuedAt)
            .Select(x => x.Verb)
            .ToListAsync(Ct);

        Assert.Equal(["viewed", "launched", "terminated", "join", "viewed"], verbs);
    }

    // -------------------------------------------------------------------------------------------------
    // MselViewedAsync(Guid)
    // -------------------------------------------------------------------------------------------------

    /// <summary>Viewed by id queues the same statement as viewed by the entity.</summary>
    [Fact]
    public async Task ViewedById_QueuesTheSameStatementAsViewedByTheEntity()
    {
        var msel = TestData.Msel();
        await Seed(msel);

        Assert.True(await Service().MselViewedAsync(msel.Id, true, Ct));

        var row = await Queued();
        Assert.Equal("viewed", row.Verb);
        Assert.Equal($"{ApiUrl}msel/{msel.Id}", row.ActivityId);
        Assert.Equal(msel.Id, row.MselId);
    }

    /// <summary>Viewing a MSEL that is not there answers false and queues nothing.</summary>
    [Fact]
    public async Task ViewedById_ForAMselThatIsNotThere_IsFalseAndQueuesNothing()
    {
        Assert.False(await Service().MselViewedAsync(Guid.NewGuid(), true, Ct));
        Assert.Equal(0, await QueuedCount());
    }

    /// <summary>The by-id overload reads the MSEL before the overload that checks whether xAPI is
    /// configured.</summary>
    [Fact]
    public async Task ViewedById_WithXApiTurnedOff_StillLooksTheMselUpAndSaysItSucceeded()
    {
        var msel = TestData.Msel();
        await Seed(msel);
        var service = Service(Options(enabled: false));

        Assert.True(await service.MselViewedAsync(msel.Id, true, Ct));
        Assert.False(await service.MselViewedAsync(Guid.NewGuid(), true, Ct));
        Assert.Equal(0, await QueuedCount());
    }

    // -------------------------------------------------------------------------------------------------
    // AssertCompetencyAsync
    // -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AssertCompetency_QueuesAnAssertedStatementScoredAgainstTheScale()
    {
        var graph = await SeedAssertionGraph(values: [1, 3, 5]);

        await Service().AssertCompetencyAsync(
            Assertion(graph, comment: "handled the escalation well"), true, Ct);

        var row = await Queued();
        Assert.Equal("asserted", row.Verb);
        Assert.Equal(graph.Msel.Id, row.MselId);
        Assert.Equal(graph.Competency.IdNumber, row.ActivityId);

        var statement = Json(row);
        Assert.Equal("https://w3id.org/xapi/tla/verbs/asserted", Text(statement, "verb.id"));
        Assert.Equal("asserted", Text(statement, "verb.display.en-US"));
        Assert.Equal(graph.Competency.IdNumber, Text(statement, "object.id"));
        Assert.Equal(
            "https://w3id.org/xapi/tla/activity-types/competency",
            Text(statement, "object.definition.type"));
        Assert.Equal(graph.Competency.ShortName, Text(statement, "object.definition.name.en-US"));
        Assert.Equal(
            graph.Competency.IdNumber,
            Extension(
                statement,
                "object.definition.extensions",
                "https://w3id.org/xapi/tla/extensions/competency-identifier").GetString());

        Assert.Equal(3, At(statement, "result.score.raw").GetDouble());
        Assert.Equal(1, At(statement, "result.score.min").GetDouble());
        Assert.Equal(5, At(statement, "result.score.max").GetDouble());
        Assert.Equal(0.5, At(statement, "result.score.scaled").GetDouble());
        Assert.True(At(statement, "result.completion").GetBoolean());
        Assert.Equal("handled the escalation well", Text(statement, "result.response"));
        Assert.Equal(graph.Msel.Id.ToString(), Text(statement, "context.registration"));
    }

    [Fact]
    public async Task AssertCompetency_WithNoComment_RecordsNoResponse()
    {
        var graph = await SeedAssertionGraph();

        await Service().AssertCompetencyAsync(Assertion(graph), true, Ct);

        Assert.False(At(await QueuedStatement(), "result").TryGetProperty("response", out _));
    }

    /// <summary>A scale with one level records the raw score and no scaled score.</summary>
    [Fact]
    public async Task AssertCompetency_AgainstAScaleWithOneLevel_RecordsNoScaledScore()
    {
        var graph = await SeedAssertionGraph(values: [4]);

        await Service().AssertCompetencyAsync(Assertion(graph), true, Ct);

        var statement = await QueuedStatement();
        Assert.Equal(4, At(statement, "result.score.raw").GetDouble());
        Assert.False(At(statement, "result.score").TryGetProperty("scaled", out _));
    }

    /// <summary>Assert competency always claims total confidence.</summary>
    [Fact]
    public async Task AssertCompetency_AlwaysClaimsTotalConfidence()
    {
        var graph = await SeedAssertionGraph();

        await Service().AssertCompetencyAsync(Assertion(graph), true, Ct);

        Assert.Equal(
            1.0,
            Extension(
                await QueuedStatement(),
                "context.extensions",
                "https://w3id.org/xapi/tla/extensions/confidence").GetDouble());
    }

    /// <summary>Assert competency records the assessor and not who was assessed.</summary>
    [Fact]
    public async Task AssertCompetency_RecordsTheAssessorAndNotWhoWasAssessed()
    {
        var graph = await SeedAssertionGraph();
        var team = TestData.Team(graph.Msel.Id);
        await Seed(team);
        var assessor = await Actor().WithId(UserId).WithName("The Assessor").SeedAsync();
        await Actor().WithName("The Participant").OnTeam(team).SeedAsync();

        await Service(user: Principal(assessor.Id))
            .AssertCompetencyAsync(Assertion(graph, teamId: team.Id), true, Ct);

        var statement = await QueuedStatement();
        Assert.Equal("The Assessor", Text(statement, "actor.name"));
        Assert.Equal(
            "The Assessor",
            Text(Assert.Single(Items(statement, "context.team.member")), "name"));
        Assert.False(At(statement, "context").TryGetProperty("instructor", out _));
    }

    [Fact]
    public async Task AssertCompetency_NamesTheMselAsTheParentAndTheFrameworkAsAGrouping()
    {
        var graph = await SeedAssertionGraph();

        await Service().AssertCompetencyAsync(Assertion(graph), true, Ct);

        var statement = await QueuedStatement();
        var parent = Assert.Single(Items(statement, "context.contextActivities.parent"));
        Assert.Equal($"{ApiUrl}msel/{graph.Msel.Id}", Text(parent, "id"));
        Assert.Equal(graph.Msel.Name, Text(parent, "definition.name.en-US"));

        var framework = Assert.Single(Items(statement, "context.contextActivities.grouping"));
        Assert.Equal(graph.Framework.IdNumber, Text(framework, "id"));
        Assert.Equal(
            "https://w3id.org/xapi/tla/activity-types/competency-framework",
            Text(framework, "definition.type"));
    }

    [Theory]
    [InlineData("https://competencies.test/c/17", "https://competencies.test/c/17")]
    [InlineData("C-17", null)]
    [InlineData(null, null)]
    public async Task AssertCompetency_UsesTheCompetencysIdNumberOnlyWhenItIsAnIri(
        string idNumber, string expected)
    {
        var graph = await SeedAssertionGraph(competencyIdNumber: idNumber);

        await Service().AssertCompetencyAsync(Assertion(graph), true, Ct);

        Assert.Equal(
            expected ?? $"{ApiUrl}competencies/{graph.Competency.Id}",
            (await Queued()).ActivityId);
    }

    [Fact]
    public async Task AssertCompetency_AboutAScenarioEventMoveAndGroup_NamesEachAsAGrouping()
    {
        var graph = await SeedAssertionGraph();
        var scenarioEvent = TestData.ScenarioEvent(graph.Msel.Id);
        await Seed(scenarioEvent);

        await Service().AssertCompetencyAsync(
            Assertion(graph, scenarioEventId: scenarioEvent.Id, moveNumber: 2, groupNumber: 3), true, Ct);

        var grouping = Items(await QueuedStatement(), "context.contextActivities.grouping")
            .Select(x => Text(x, "id"))
            .ToList();

        Assert.Contains($"{ApiUrl}scenarioevents/{scenarioEvent.Id}", grouping);
        Assert.Contains($"{ApiUrl}msels/{graph.Msel.Id}/moves/2", grouping);
        Assert.Contains($"{ApiUrl}msels/{graph.Msel.Id}/moves/2/groups/3", grouping);
    }

    /// <summary>Assert competency about a group with no move numbers it under move zero.</summary>
    [Fact]
    public async Task AssertCompetency_AboutAGroupWithNoMove_NumbersItUnderMoveZero()
    {
        var graph = await SeedAssertionGraph();

        await Service().AssertCompetencyAsync(Assertion(graph, groupNumber: 3), true, Ct);

        Assert.Contains(
            $"{ApiUrl}msels/{graph.Msel.Id}/moves/0/groups/3",
            Items(await QueuedStatement(), "context.contextActivities.grouping")
                .Select(x => Text(x, "id")));
    }

    [Fact]
    public async Task AssertCompetency_IsCategorizedUnderTheCrucibleProfile()
    {
        var graph = await SeedAssertionGraph();

        var category = Assert.Single(
            await Categories(() => Service().AssertCompetencyAsync(Assertion(graph), true, Ct)));

        Assert.Equal("https://crucible.sei.cmu.edu/xapi/profile/v1", Text(category, "id"));
    }

    /// <summary>Assert competency about something that is not there throws.</summary>
    [Theory]
    [InlineData("msel")]
    [InlineData("competency")]
    [InlineData("level")]
    [InlineData("event")]
    public async Task AssertCompetency_AboutSomethingThatIsNotThere_Throws(string missing)
    {
        var graph = await SeedAssertionGraph();
        var scenarioEvent = TestData.ScenarioEvent(graph.Msel.Id);
        await Seed(scenarioEvent);

        var assertion = Assertion(graph, scenarioEventId: scenarioEvent.Id);

        switch (missing)
        {
            case "msel":
                assertion.MselId = Guid.NewGuid();
                break;
            case "competency":
                assertion.CompetencyId = Guid.NewGuid();
                break;
            case "level":
                assertion.ProficiencyLevelId = Guid.NewGuid();
                break;
            default:
                assertion.ScenarioEventId = Guid.NewGuid();
                break;
        }

        await Assert.ThrowsAsync<ArgumentException>(
            () => Service().AssertCompetencyAsync(assertion, true, Ct));

        Assert.Equal(0, await QueuedCount());
    }

    [Fact]
    public async Task AssertCompetency_WithXApiTurnedOff_QueuesNothingAndSaysItSucceeded()
    {
        var graph = await SeedAssertionGraph();

        Assert.True(
            await Service(Options(enabled: false)).AssertCompetencyAsync(Assertion(graph), true, Ct));

        Assert.Equal(0, await QueuedCount());
    }

    // -------------------------------------------------------------------------------------------------
    // RecordCheckboxChangeAsync
    // -------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(true, "https://w3id.org/xapi/dod-isd/verbs/selected", "selected")]
    [InlineData(false, "https://w3id.org/xapi/dod-isd/verbs/reset", "reset")]
    public async Task Checkbox_QueuesSelectedOrReset(bool isChecked, string verb, string display)
    {
        var msel = TestData.Msel();
        var scenarioEvent = TestData.ScenarioEvent(msel.Id);
        await Seed(msel, scenarioEvent);
        var dataFieldId = Guid.NewGuid();

        await Service().RecordCheckboxChangeAsync(
            msel.Id, scenarioEvent.Id, dataFieldId, "Comms restored", isChecked, Ct);

        var row = await Queued();
        Assert.Equal(msel.Id, row.MselId);
        Assert.Null(row.TeamId);
        Assert.Equal(
            $"{ApiUrl}scenarioevents/{scenarioEvent.Id}/datafields/{dataFieldId}", row.ActivityId);

        var statement = Json(row);
        Assert.Equal(verb, Text(statement, "verb.id"));
        Assert.Equal(display, Text(statement, "verb.display.en-US"));
        Assert.Equal("Comms restored", Text(statement, "object.definition.name.en-US"));
        Assert.Equal(
            "http://id.tincanapi.com/activitytype/checklist-item",
            Text(statement, "object.definition.type"));
        Assert.Equal(isChecked, At(statement, "result.completion").GetBoolean());
        Assert.Equal(isChecked, At(statement, "result.success").GetBoolean());
    }

    /// <summary>The queue is told every checkbox change is a completion.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TheQueueIsToldEveryCheckboxChangeIsACompletion(bool isChecked)
    {
        var msel = TestData.Msel();
        var scenarioEvent = TestData.ScenarioEvent(msel.Id);
        await Seed(msel, scenarioEvent);

        await Service().RecordCheckboxChangeAsync(
            msel.Id, scenarioEvent.Id, Guid.NewGuid(), "Comms restored", isChecked, Ct);

        Assert.Equal("completed", (await Queued()).Verb);
    }

    [Fact]
    public async Task Checkbox_NamesTheMselAsTheParentAndTheEventAsAGrouping()
    {
        var msel = TestData.Msel();
        var scenarioEvent = TestData.ScenarioEvent(msel.Id);
        await Seed(msel, scenarioEvent);

        await Service().RecordCheckboxChangeAsync(
            msel.Id, scenarioEvent.Id, Guid.NewGuid(), "Comms restored", true, Ct);

        var statement = await QueuedStatement();
        var parent = Assert.Single(Items(statement, "context.contextActivities.parent"));
        Assert.Equal($"{ApiUrl}msels/{msel.Id}", Text(parent, "id"));

        var grouping = Assert.Single(Items(statement, "context.contextActivities.grouping"));
        Assert.Equal($"{ApiUrl}scenarioevents/{scenarioEvent.Id}", Text(grouping, "id"));
        Assert.Equal("Scenario Event", Text(grouping, "definition.name.en-US"));
    }

    /// <summary>The two hand built paths name the MSEL two different ways.</summary>
    [Fact]
    public async Task TheTwoHandBuiltPathsNameTheMselTwoDifferentWays()
    {
        var graph = await SeedAssertionGraph();
        var scenarioEvent = TestData.ScenarioEvent(graph.Msel.Id);
        await Seed(scenarioEvent);
        var service = Service();

        await service.AssertCompetencyAsync(Assertion(graph), true, Ct);
        await service.RecordCheckboxChangeAsync(
            graph.Msel.Id, scenarioEvent.Id, Guid.NewGuid(), "Comms restored", true, Ct);

        await using var context = NewContext();
        var rows = await context.XApiQueuedStatements.OrderBy(x => x.QueuedAt).ToListAsync(Ct);

        Assert.Equal(
            $"{ApiUrl}msel/{graph.Msel.Id}",
            Text(Items(Json(rows[0]), "context.contextActivities.parent").First(), "id"));
        Assert.Equal(
            $"{ApiUrl}msels/{graph.Msel.Id}",
            Text(Items(Json(rows[1]), "context.contextActivities.parent").First(), "id"));
    }

    [Fact]
    public async Task Checkbox_IsGroupedUnderTheMoveTheEventFallsIn()
    {
        var msel = TestData.Msel();
        var scenarioEvent = TestData.ScenarioEvent(msel.Id, deltaSeconds: 3600);
        var first = TestData.Move(msel.Id, moveNumber: 1, deltaSeconds: 0);
        var second = TestData.Move(msel.Id, moveNumber: 2, deltaSeconds: 1800);
        var later = TestData.Move(msel.Id, moveNumber: 3, deltaSeconds: 7200);
        await Seed(msel, scenarioEvent, first, second, later);

        await Service().RecordCheckboxChangeAsync(
            msel.Id, scenarioEvent.Id, Guid.NewGuid(), "Comms restored", true, Ct);

        var grouping = Items(await QueuedStatement(), "context.contextActivities.grouping")
            .Select(x => Text(x, "id"))
            .ToList();

        Assert.Contains($"{ApiUrl}moves/{second.Id}", grouping);
        Assert.DoesNotContain($"{ApiUrl}moves/{first.Id}", grouping);
        Assert.DoesNotContain($"{ApiUrl}moves/{later.Id}", grouping);
    }

    /// <summary>Checkbox for an event earlier than every move is grouped under no move.</summary>
    [Fact]
    public async Task Checkbox_ForAnEventEarlierThanEveryMove_IsGroupedUnderNoMove()
    {
        var msel = TestData.Msel();
        var scenarioEvent = TestData.ScenarioEvent(msel.Id, deltaSeconds: 60);
        await Seed(msel, scenarioEvent, TestData.Move(msel.Id, deltaSeconds: 1800));

        await Service().RecordCheckboxChangeAsync(
            msel.Id, scenarioEvent.Id, Guid.NewGuid(), "Comms restored", true, Ct);

        Assert.Single(Items(await QueuedStatement(), "context.contextActivities.grouping"));
    }

    [Theory]
    [InlineData("the first hour", "the first hour")]
    [InlineData(null, "Move 4")]
    public async Task Checkbox_NamesTheMoveByItsDescriptionOrItsNumber(
        string description, string expected)
    {
        var msel = TestData.Msel();
        var scenarioEvent = TestData.ScenarioEvent(msel.Id, deltaSeconds: 3600);
        var move = TestData.Move(msel.Id, moveNumber: 4, deltaSeconds: 0);
        move.Description = description;
        await Seed(msel, scenarioEvent, move);

        await Service().RecordCheckboxChangeAsync(
            msel.Id, scenarioEvent.Id, Guid.NewGuid(), "Comms restored", true, Ct);

        var moveActivity = Items(await QueuedStatement(), "context.contextActivities.grouping")
            .Single(x => Text(x, "id") == $"{ApiUrl}moves/{move.Id}");

        Assert.Equal(expected, Text(moveActivity, "definition.name.en-US"));
    }

    [Theory]
    [InlineData("msel")]
    [InlineData("event")]
    public async Task Checkbox_AboutSomethingThatIsNotThere_Throws(string missing)
    {
        var msel = TestData.Msel();
        var scenarioEvent = TestData.ScenarioEvent(msel.Id);
        await Seed(msel, scenarioEvent);

        await Assert.ThrowsAsync<ArgumentException>(
            () => Service().RecordCheckboxChangeAsync(
                missing == "msel" ? Guid.NewGuid() : msel.Id,
                missing == "event" ? Guid.NewGuid() : scenarioEvent.Id,
                Guid.NewGuid(),
                "Comms restored",
                true,
                Ct));

        Assert.Equal(0, await QueuedCount());
    }

    /// <summary>Checkbox about another MSELs event is recorded anyway.</summary>
    [Fact]
    public async Task Checkbox_AboutAnotherMselsEvent_IsRecordedAnyway()
    {
        var msel = TestData.Msel();
        var other = TestData.Msel();
        var scenarioEvent = TestData.ScenarioEvent(other.Id);
        await Seed(msel, other, scenarioEvent);

        await Service().RecordCheckboxChangeAsync(
            msel.Id, scenarioEvent.Id, Guid.NewGuid(), "Comms restored", true, Ct);

        Assert.Equal(msel.Id, (await Queued()).MselId);
    }

    [Fact]
    public async Task Checkbox_IsCategorizedUnderTheCrucibleProfile()
    {
        var msel = TestData.Msel();
        var scenarioEvent = TestData.ScenarioEvent(msel.Id);
        await Seed(msel, scenarioEvent);

        var category = Assert.Single(
            await Categories(() => Service().RecordCheckboxChangeAsync(
                msel.Id, scenarioEvent.Id, Guid.NewGuid(), "Comms restored", true, Ct)));

        Assert.Equal("https://crucible.sei.cmu.edu/xapi/profile/v1", Text(category, "id"));
    }

    // -------------------------------------------------------------------------------------------------
    // GetStatementsAsync
    // -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task GetStatements_WithXApiTurnedOff_AnswersAnEmptyStatementList() =>
        Assert.Equal(
            "{\"statements\":[]}",
            await Service(Options(enabled: false))
                .GetStatementsAsync(Guid.NewGuid(), null, null, 100, null, true, Ct));

    /// <summary>Get statements for a MSEL that is not there answers an empty statement list too.</summary>
    [Fact]
    public async Task GetStatements_ForAMselThatIsNotThere_AnswersAnEmptyStatementListToo() =>
        Assert.Equal(
            "{\"statements\":[]}",
            await Service().GetStatementsAsync(Guid.NewGuid(), null, null, 100, null, true, Ct));

    /// <summary>Get statements for a source nobody recognizes answers an empty statement list.</summary>
    [Fact]
    public async Task GetStatements_ForASourceNobodyRecognizes_AnswersAnEmptyStatementList()
    {
        var msel = TestData.Msel();
        await Seed(msel);

        Assert.Equal(
            "{\"statements\":[]}",
            await Service().GetStatementsAsync(msel.Id, null, null, 100, "citee", true, Ct));
    }

    [Fact]
    public async Task GetStatements_ForAnIntegrationTheMselIsNotPushedTo_AnswersAnEmptyStatementList()
    {
        var msel = TestData.Msel();
        await Seed(msel);

        Assert.Equal(
            "{\"statements\":[]}",
            await Service().GetStatementsAsync(msel.Id, null, null, 100, "cite", true, Ct));
    }

    /// <summary>Get statements with an LRS that cannot be reached throws where a non success status is swallowed.</summary>
    [Fact]
    public async Task GetStatements_WithAnLrsThatCannotBeReached_ThrowsWhereANonSuccessStatusIsSwallowed()
    {
        var msel = TestData.Msel();
        await Seed(msel);

        await Assert.ThrowsAnyAsync<HttpRequestException>(
            () => Service(Options(endpoint: "http://127.0.0.1:1/xapi"))
                .GetStatementsAsync(msel.Id, null, null, 100, null, true, Ct));
    }

    // -------------------------------------------------------------------------------------------------
    // GetStatementsAsync on a MSEL that hides names on the Assessor View
    // -------------------------------------------------------------------------------------------------

    /// <summary>
    /// A rating of one team member, with the member as the actor, the assessor as the instructor and the
    /// team, members and all, as the context; then a statement about a person and one wrapping a
    /// sub-statement.
    /// </summary>
    private const string StatementsWithNames = """
        {"statements":[
          {"id":"s1",
           "actor":{"objectType":"Agent","name":"Pat Learner","mbox":"mailto:pat@example.test",
                    "account":{"homePage":"https://id.test","name":"pat-sub"}},
           "verb":{"id":"https://w3id.org/xapi/tla/verbs/asserted"},
           "object":{"id":"https://blueprint.test/api/competency/1"},
           "context":{
             "instructor":{"name":"Sam Assessor","account":{"homePage":"https://id.test","name":"sam-sub"}},
             "team":{"objectType":"Group","name":"BLU",
                     "account":{"homePage":"https://blueprint.test/","name":"team-1"},
                     "member":[{"name":"Pat Learner","account":{"homePage":"https://id.test","name":"pat-sub"}}]}}},
          {"id":"s2",
           "actor":{"name":"Sam Assessor","openid":"https://id.test/sam","mbox_sha1sum":"abc"},
           "verb":{"id":"http://adlnet.gov/expapi/verbs/mentored"},
           "object":{"objectType":"Agent","name":"Pat Learner","account":{"homePage":"https://id.test","name":"pat-sub"}}},
          {"id":"s3",
           "actor":{"objectType":"Group","name":"BLU","account":{"homePage":"https://blueprint.test/","name":"team-1"},
                    "member":[{"name":"Pat Learner","mbox":"mailto:pat@example.test"}]},
           "verb":{"id":"http://adlnet.gov/expapi/verbs/planned"},
           "object":{"objectType":"SubStatement",
                     "actor":{"name":"Pat Learner","mbox":"mailto:pat@example.test"},
                     "verb":{"id":"http://adlnet.gov/expapi/verbs/attempted"},
                     "object":{"id":"https://blueprint.test/api/inject/1"}}},
          {"id":"s4",
           "actor":{"name":"Pat Learner","account":{"homePage":"https://id.test","name":"pat-sub"}},
           "verb":{"id":"http://adlnet.gov/expapi/verbs/assigned"},
           "object":{"id":"https://cite.test/api/duties/1"},
           "context":{"contextActivities":{
             "parent":[{"id":"https://cite.test/api/evaluations/1",
                        "definition":{"name":{"en-US":"Evaluation"},"type":"http://adlnet.gov/expapi/activities/simulation"}}],
             "other":[{"id":"https://cite.test/api/users/pat-user-id",
                       "definition":{"name":{"en-US":"Pat Learner"},"type":"http://id.tincanapi.com/activitytype/user-profile"}}]}}}
        ]}
        """;

    /// <summary>What <see cref="StatementsWithNames"/> says about who its actors and objects are.</summary>
    private static readonly string[] IdentityOfWhoItIsAbout = ["Pat Learner", "pat@example.test", "pat-sub", "https://id.test/sam", "abc", "pat-user-id"];

    private async Task<JsonElement> StatementsFor(MselEntity msel, Guid userId, bool hasSystemPermission)
    {
        using var lrs = new FakeLrs(StatementsWithNames);

        var json = await Service(Options(endpoint: lrs.Endpoint), Principal(userId))
            .GetStatementsAsync(msel.Id, null, null, 100, null, hasSystemPermission, Ct);

        return JsonDocument.Parse(json).RootElement.GetProperty("statements");
    }

    /// <summary>An assessor rates the team's work, so nothing in the list says which member it is about.</summary>
    [Theory]
    [InlineData(MselRole.Evaluator)]
    [InlineData(MselRole.Editor)]
    [InlineData(MselRole.Approver)]
    public async Task GetStatements_OnAMselThatHidesNames_LeavesOutWhoEachStatementIsAbout(MselRole role)
    {
        var msel = TestData.Msel();
        msel.HideNamesOnAssessorView = true;
        await Seed(msel);
        var actor = await Actor().OnMsel(msel, role).SeedAsync();

        var statements = await StatementsFor(msel, actor.Id, hasSystemPermission: false);

        var text = statements.GetRawText();
        Assert.All(IdentityOfWhoItIsAbout, identity => Assert.DoesNotContain(identity, text));
        Assert.Equal(["s1", "s2", "s3", "s4"], statements.EnumerateArray().Select(s => Text(s, "id")));
    }

    /// <summary>The team is still there, by name and id, which is what the Assessor View groups by.</summary>
    [Fact]
    public async Task GetStatements_OnAMselThatHidesNames_KeepsTheTeam()
    {
        var msel = TestData.Msel();
        msel.HideNamesOnAssessorView = true;
        await Seed(msel);
        var actor = await Actor().OnMsel(msel, MselRole.Evaluator).SeedAsync();

        var statements = await StatementsFor(msel, actor.Id, hasSystemPermission: false);

        var team = statements[0].GetProperty("context").GetProperty("team");
        Assert.Equal("BLU", Text(team, "name"));
        Assert.Equal("team-1", Text(team.GetProperty("account"), "name"));
        Assert.False(team.TryGetProperty("member", out _));
        var groupActor = statements[2].GetProperty("actor");
        Assert.Equal("BLU", Text(groupActor, "name"));
        Assert.False(groupActor.TryGetProperty("member", out _));
    }

    /// <summary>
    /// CITE names the person a statement is about with a user-profile activity; that goes, and the
    /// statement's other context activities stay.
    /// </summary>
    [Fact]
    public async Task GetStatements_OnAMselThatHidesNames_DropsUserProfileActivities()
    {
        var msel = TestData.Msel();
        msel.HideNamesOnAssessorView = true;
        await Seed(msel);
        var actor = await Actor().OnMsel(msel, MselRole.Evaluator).SeedAsync();

        var statements = await StatementsFor(msel, actor.Id, hasSystemPermission: false);

        var activities = statements[3].GetProperty("context").GetProperty("contextActivities");
        Assert.Equal(0, activities.GetProperty("other").GetArrayLength());
        Assert.Equal("https://cite.test/api/evaluations/1", Text(activities.GetProperty("parent")[0], "id"));
    }

    /// <summary>The instructor is the assessor, not someone being assessed, so it keeps its name.</summary>
    [Fact]
    public async Task GetStatements_OnAMselThatHidesNames_KeepsTheAssessor()
    {
        var msel = TestData.Msel();
        msel.HideNamesOnAssessorView = true;
        await Seed(msel);
        var actor = await Actor().OnMsel(msel, MselRole.Evaluator).SeedAsync();

        var statements = await StatementsFor(msel, actor.Id, hasSystemPermission: false);

        Assert.Equal("Sam Assessor", Text(statements[0].GetProperty("context").GetProperty("instructor"), "name"));
    }

    /// <summary>The MSEL's owner runs the event and keeps the names.</summary>
    [Fact]
    public async Task GetStatements_OnAMselThatHidesNames_ShowsTheOwnerTheNames()
    {
        var owner = Guid.NewGuid();
        var msel = TestData.Msel(createdBy: owner);
        msel.HideNamesOnAssessorView = true;
        await Seed(msel);

        var statements = await StatementsFor(msel, owner, hasSystemPermission: false);

        Assert.Equal("Pat Learner", Text(statements[0].GetProperty("actor"), "name"));
    }

    [Fact]
    public async Task GetStatements_OnAMselThatHidesNames_ShowsTheNamesToSomeoneWhoCanViewEveryMsel()
    {
        var msel = TestData.Msel();
        msel.HideNamesOnAssessorView = true;
        await Seed(msel);

        var statements = await StatementsFor(msel, Guid.NewGuid(), hasSystemPermission: true);

        Assert.Equal("Pat Learner", Text(statements[0].GetProperty("actor"), "name"));
    }

    [Fact]
    public async Task GetStatements_OnAMselThatShowsNames_ShowsTheAssessorTheNames()
    {
        var msel = TestData.Msel();
        await Seed(msel);
        var actor = await Actor().OnMsel(msel, MselRole.Evaluator).SeedAsync();

        var statements = await StatementsFor(msel, actor.Id, hasSystemPermission: false);

        Assert.Equal("Pat Learner", Text(statements[0].GetProperty("actor"), "name"));
        Assert.Equal(1, statements[0].GetProperty("context").GetProperty("team").GetProperty("member").GetArrayLength());
    }

    // -------------------------------------------------------------------------------------------------
    // GetStatementsAsync following the LRS's more links
    // -------------------------------------------------------------------------------------------------

    /// <summary>
    /// One page of statements, each about Pat Learner, with <paramref name="more"/> as its <c>more</c> link
    /// (null leaves the property out, which an LRS may do on its last page).
    /// </summary>
    private static string Page(string more, params string[] ids)
    {
        var statements = ids.Select(id => new Dictionary<string, object>
        {
            ["id"] = id,
            ["timestamp"] = $"2026-11-05T10:00:{Array.IndexOf(ids, id):00}.000Z",
            ["actor"] = new Dictionary<string, object>
            {
                ["name"] = "Pat Learner",
                ["mbox"] = "mailto:pat@example.test",
            },
            ["verb"] = new Dictionary<string, object> { ["id"] = "http://adlnet.gov/expapi/verbs/attempted" },
            ["object"] = new Dictionary<string, object> { ["id"] = $"https://blueprint.test/api/inject/{id}" },
        });
        var page = new Dictionary<string, object> { ["statements"] = statements };
        if (more != null)
            page["more"] = more;
        return JsonSerializer.Serialize(page);
    }

    /// <summary>
    /// An LRS that answers a first query by its registration and a follow-up by the <c>more</c> query
    /// parameter of the link it handed out; anything else is a 404.
    /// </summary>
    private static Func<FakeLrs.Request, FakeLrs.Response> Pages(Dictionary<string, string> byRegistrationOrMore) =>
        request =>
        {
            var key = request.Query("more") ?? request.Query("registration");
            return key != null && byRegistrationOrMore.TryGetValue(key, out var body)
                ? new FakeLrs.Response(body)
                : new FakeLrs.Response("{}", 404);
        };

    private static string More(string key) => $"/xapi/statements?more={key}";

    private async Task<(List<string> Ids, string Json)> PagedStatements(
        FakeLrs lrs,
        MselEntity msel,
        int limit = 2,
        int? max = null,
        Guid? userId = null,
        bool hasSystemPermission = true)
    {
        var options = Options(endpoint: lrs.Endpoint);
        if (max.HasValue)
            options.MaxStatementsPerRegistration = max.Value;

        var json = await Service(options, Principal(userId ?? UserId))
            .GetStatementsAsync(msel.Id, null, null, limit, null, hasSystemPermission, Ct);

        var ids = JsonDocument.Parse(json).RootElement.GetProperty("statements")
            .EnumerateArray().Select(s => Text(s, "id")).ToList();
        return (ids, json);
    }

    /// <summary>
    /// One registration with more statements than the page size: every page is read, the request's limit is
    /// the page size, and the pages are merged into one list, newest first, with no <c>more</c> left in it.
    /// </summary>
    [Fact]
    public async Task GetStatements_FollowsMoreUntilTheLastPage_AndMergesEveryPage()
    {
        var msel = TestData.Msel();
        await Seed(msel);
        var reg = msel.Id.ToString();
        using var lrs = new FakeLrs(Pages(new()
        {
            [reg] = Page(More("p2"), "a1", "a2"),
            ["p2"] = Page(More("p3"), "a3", "a4"),
            ["p3"] = Page("", "a5"),
        }));

        var (ids, json) = await PagedStatements(lrs, msel);

        Assert.Equal(["a1", "a2", "a3", "a4", "a5"], ids.Order());
        Assert.Equal(3, lrs.Requests.Count);
        Assert.Equal(reg, lrs.Requests[0].Query("registration"));
        Assert.Equal("2", lrs.Requests[0].Query("limit"));
        Assert.Equal(["/xapi/statements?more=p2", "/xapi/statements?more=p3"], lrs.Requests.Skip(1).Select(r => r.PathAndQuery));
        Assert.False(JsonDocument.Parse(json).RootElement.TryGetProperty("more", out _));
        var timestamps = JsonDocument.Parse(json).RootElement.GetProperty("statements")
            .EnumerateArray().Select(s => Text(s, "timestamp")).ToList();
        Assert.Equal(timestamps.OrderDescending(StringComparer.Ordinal), timestamps);
    }

    /// <summary>A last page with no <c>more</c> property at all ends the paging as an empty one does.</summary>
    [Fact]
    public async Task GetStatements_StopsAtAPageWithNoMoreProperty()
    {
        var msel = TestData.Msel();
        await Seed(msel);
        using var lrs = new FakeLrs(Pages(new()
        {
            [msel.Id.ToString()] = Page(More("p2"), "a1", "a2"),
            ["p2"] = Page(null, "a3"),
        }));

        var (ids, _) = await PagedStatements(lrs, msel);

        Assert.Equal(["a1", "a2", "a3"], ids.Order());
        Assert.Equal(2, lrs.Requests.Count);
    }

    /// <summary>An absolute <c>more</c> link is followed as it is.</summary>
    [Fact]
    public async Task GetStatements_FollowsAnAbsoluteMoreLink()
    {
        var msel = TestData.Msel();
        await Seed(msel);
        FakeLrs lrs = null;
        var pages = new Dictionary<string, string>();
        lrs = new FakeLrs(Pages(pages));
        using var _ = lrs;
        pages[msel.Id.ToString()] = Page($"{lrs.Origin}/xapi/statements?more=p2", "a1", "a2");
        pages["p2"] = Page("", "a3");

        var (ids, _) = await PagedStatements(lrs, msel);

        Assert.Equal(["a1", "a2", "a3"], ids.Order());
        Assert.Equal("/xapi/statements?more=p2", lrs.Requests[1].PathAndQuery);
    }

    /// <summary>
    /// Every page is sent the LRS credentials, so an absolute <c>more</c> on another host is not followed:
    /// the first page is kept and a warning names the link.
    /// </summary>
    [Fact]
    public async Task GetStatements_DoesNotFollowAMoreLinkToAnotherHost()
    {
        var msel = TestData.Msel();
        await Seed(msel);
        FakeLrs lrs = null;
        var pages = new Dictionary<string, string>();
        lrs = new FakeLrs(Pages(pages));
        using var _ = lrs;
        pages[msel.Id.ToString()] = Page("https://elsewhere.test/xapi/statements?more=p2", "a1", "a2");
        pages["p2"] = Page("", "a3");

        var (ids, _) = await PagedStatements(lrs, msel);

        Assert.Equal(["a1", "a2"], ids.Order());
        Assert.Single(lrs.Requests);
        Assert.Contains(Log.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("elsewhere.test"));
    }

    /// <summary>
    /// A relative <c>more</c> is relative to the LRS's host, not to <c>Endpoint</c>'s path, so a link that
    /// does not repeat the endpoint's path is not read under it.
    /// </summary>
    [Fact]
    public async Task GetStatements_ResolvesARelativeMoreAgainstTheLrsHost()
    {
        var msel = TestData.Msel();
        await Seed(msel);
        using var lrs = new FakeLrs(request =>
            request.PathAndQuery.StartsWith("/xapi/statements?registration=")
                ? new FakeLrs.Response(Page("/other/statements?more=p2", "a1", "a2"))
                : request.PathAndQuery == "/other/statements?more=p2"
                    ? new FakeLrs.Response(Page("", "a3"))
                    : new FakeLrs.Response("{}", 404));

        var (ids, _) = await PagedStatements(lrs, msel);

        Assert.Equal(["a1", "a2", "a3"], ids.Order());
    }

    /// <summary>
    /// <c>MaxStatementsPerRegistration</c> stops the paging once a registration has that many, keeps what
    /// was read, and says so in a warning naming the registration and the count.
    /// </summary>
    [Fact]
    public async Task GetStatements_StopsAtMaxStatementsPerRegistration_AndLogsAWarning()
    {
        var msel = TestData.Msel();
        await Seed(msel);
        var reg = msel.Id.ToString();
        using var lrs = new FakeLrs(Pages(new()
        {
            [reg] = Page(More("p2"), "a1", "a2"),
            ["p2"] = Page(More("p3"), "a3", "a4"),
            ["p3"] = Page(More("p4"), "a5", "a6"),
            ["p4"] = Page("", "a7"),
        }));

        var (ids, _) = await PagedStatements(lrs, msel, max: 3);

        Assert.Equal(["a1", "a2", "a3", "a4"], ids.Order());
        Assert.Equal(2, lrs.Requests.Count);
        var warning = Assert.Single(Log.At(LogLevel.Warning));
        Assert.Contains(reg, warning.Message);
        Assert.Contains("4 statements", warning.Message);
    }

    /// <summary>The cap's default is the one appsettings.json documents.</summary>
    [Fact]
    public void MaxStatementsPerRegistration_DefaultsTo5000() =>
        Assert.Equal(5000, new XApiOptions().MaxStatementsPerRegistration);

    /// <summary>A <c>more</c> link the LRS already handed out ends the paging, so a bad LRS cannot loop it.</summary>
    [Fact]
    public async Task GetStatements_StopsWhenTheLrsRepeatsAMoreLink()
    {
        var msel = TestData.Msel();
        await Seed(msel);
        using var lrs = new FakeLrs(Pages(new()
        {
            [msel.Id.ToString()] = Page(More("p2"), "a1", "a2"),
            ["p2"] = Page(More("p2"), "a3", "a4"),
        }));

        var (ids, _) = await PagedStatements(lrs, msel);

        Assert.Equal(["a1", "a2", "a3", "a4"], ids.Order());
        Assert.Equal(2, lrs.Requests.Count);
        Assert.Contains(Log.At(LogLevel.Warning), w => w.Message.Contains("repeated"));
    }

    /// <summary>A later page the LRS refuses is logged, and the pages before it are still answered.</summary>
    [Fact]
    public async Task GetStatements_WhenALaterPageFails_KeepsTheEarlierPages()
    {
        var msel = TestData.Msel();
        await Seed(msel);
        using var lrs = new FakeLrs(Pages(new()
        {
            [msel.Id.ToString()] = Page(More("p2"), "a1", "a2"),
            ["p2"] = Page(More("p3"), "a3", "a4"),
        }));

        var (ids, _) = await PagedStatements(lrs, msel);

        Assert.Equal(["a1", "a2", "a3", "a4"], ids.Order());
        Assert.Equal(3, lrs.Requests.Count);
        Assert.Contains(Log.At(LogLevel.Warning), w => w.Message.Contains("NotFound"));
    }

    /// <summary>
    /// A later page the LRS drops the connection on is logged too, rather than failing the whole list;
    /// only the first query still throws (see the unreachable LRS test above).
    /// </summary>
    [Fact]
    public async Task GetStatements_WhenALaterPageCannotBeReached_KeepsTheEarlierPages()
    {
        var msel = TestData.Msel();
        await Seed(msel);
        using var lrs = new FakeLrs(request =>
            request.PathAndQuery.Contains("more=p2")
                ? new FakeLrs.Response("", Drop: true)
                : new FakeLrs.Response(Page("/xapi/statements?more=p2", "a1", "a2")));

        var (ids, _) = await PagedStatements(lrs, msel);

        Assert.Equal(["a1", "a2"], ids.Order());
        // A dropped connection surfaces as a failed request or, on HttpListener under Linux, an empty answer.
        Assert.Contains(Log.At(LogLevel.Warning), w =>
            w.Exception is HttpRequestException or System.IO.IOException || w.Message.Contains("not a statement list"));
    }

    /// <summary>An answer that is not a statement list is left out with a warning instead of failing the whole list.</summary>
    [Fact]
    public async Task GetStatements_LeavesOutAnAnswerThatIsNotAStatementList()
    {
        var msel = TestData.Msel();
        await Seed(msel);
        using var lrs = new FakeLrs(_ => new FakeLrs.Response("<html>proxy error</html>"));

        var (ids, _) = await PagedStatements(lrs, msel);

        Assert.Empty(ids);
        Assert.Contains(Log.At(LogLevel.Warning), w => w.Message.Contains("not a statement list"));
    }

    /// <summary>Each registration - the MSEL and every integration it is pushed to - is paged on its own.</summary>
    [Fact]
    public async Task GetStatements_PagesEachRegistration()
    {
        var msel = TestData.Msel();
        msel.CiteEvaluationId = Guid.NewGuid();
        msel.GalleryExhibitId = Guid.NewGuid();
        await Seed(msel);
        using var lrs = new FakeLrs(Pages(new()
        {
            [msel.Id.ToString()] = Page(More("m2"), "m1", "m2"),
            ["m2"] = Page("", "m3"),
            [msel.CiteEvaluationId.ToString()] = Page(More("c2"), "c1", "c2"),
            ["c2"] = Page(More("c3"), "c3", "c4"),
            ["c3"] = Page("", "c5"),
            [msel.GalleryExhibitId.ToString()] = Page(More("g2"), "g1", "g2"),
            ["g2"] = Page("", "g3"),
        }));

        var (ids, _) = await PagedStatements(lrs, msel);

        Assert.Equal(["c1", "c2", "c3", "c4", "c5", "g1", "g2", "g3", "m1", "m2", "m3"], ids.Order());
        Assert.Equal(7, lrs.Requests.Count);
    }

    /// <summary>The cap is per registration, so one busy source does not starve the others.</summary>
    [Fact]
    public async Task GetStatements_AppliesTheCapToEachRegistration()
    {
        var msel = TestData.Msel();
        msel.CiteEvaluationId = Guid.NewGuid();
        await Seed(msel);
        using var lrs = new FakeLrs(Pages(new()
        {
            [msel.Id.ToString()] = Page(More("m2"), "m1", "m2"),
            ["m2"] = Page(More("m3"), "m3", "m4"),
            [msel.CiteEvaluationId.ToString()] = Page(More("c2"), "c1", "c2"),
            ["c2"] = Page("", "c3"),
        }));

        var (ids, _) = await PagedStatements(lrs, msel, max: 2);

        Assert.Equal(["c1", "c2", "m1", "m2"], ids.Order());
        Assert.Equal(2, Log.At(LogLevel.Warning).Count);
    }

    /// <summary>The names are left out of every page, not only the first.</summary>
    [Fact]
    public async Task GetStatements_OnAMselThatHidesNames_LeavesTheNamesOutOfLaterPages()
    {
        var msel = TestData.Msel();
        msel.HideNamesOnAssessorView = true;
        await Seed(msel);
        var actor = await Actor().OnMsel(msel, MselRole.Evaluator).SeedAsync();
        using var lrs = new FakeLrs(Pages(new()
        {
            [msel.Id.ToString()] = Page(More("p2"), "a1", "a2"),
            ["p2"] = Page("", "a3"),
        }));

        var (ids, json) = await PagedStatements(lrs, msel, userId: actor.Id, hasSystemPermission: false);

        Assert.Equal(["a1", "a2", "a3"], ids.Order());
        Assert.DoesNotContain("Pat Learner", json);
        Assert.DoesNotContain("pat@example.test", json);
    }

    /// <summary>Every page carries the LRS credentials and the xAPI version, not only the first query.</summary>
    [Fact]
    public async Task GetStatements_SendsTheAuthAndVersionHeadersOnEveryPage()
    {
        var msel = TestData.Msel();
        await Seed(msel);
        using var lrs = new FakeLrs(Pages(new()
        {
            [msel.Id.ToString()] = Page(More("p2"), "a1", "a2"),
            ["p2"] = Page(More("p3"), "a3", "a4"),
            ["p3"] = Page("", "a5"),
        }));

        await PagedStatements(lrs, msel);

        var basic = "Basic " + Convert.ToBase64String(System.Text.Encoding.ASCII.GetBytes("lrs-user:lrs-secret"));
        Assert.Equal(3, lrs.Requests.Count);
        Assert.All(lrs.Requests, request =>
        {
            Assert.Equal(basic, request.Authorization);
            Assert.Equal("1.0.3", request.Version);
        });
    }

    // -------------------------------------------------------------------------------------------------
    // Harness
    // -------------------------------------------------------------------------------------------------

    private static XApiOptions Options(
        bool enabled = true,
        string username = "lrs-user",
        string apiUrl = ApiUrl,
        string uiUrl = UiUrl,
        string issuerUrl = null,
        string emailDomain = null,
        string platform = "Blueprint",
        string endpoint = "https://lrs.test/xapi") => new()
        {
            Enabled = enabled,
            Endpoint = endpoint,
            Username = username,
            Password = "lrs-secret",
            IssuerUrl = issuerUrl,
            ApiUrl = apiUrl,
            UiUrl = uiUrl,
            Platform = platform,
            EmailDomain = emailDomain,
        };

    /// <summary>
    /// The sibling urls, which decide which integrations a statement is grouped under. All four are null
    /// by default, because that is what makes a test about anything else produce no groupings at all.
    /// </summary>
    private static ClientOptions Clients(
        string player = null,
        string gallery = null,
        string cite = null,
        string steamfitter = null) => new()
        {
            PlayerApiUrl = player,
            GalleryApiUrl = gallery,
            CiteApiUrl = cite,
            SteamfitterApiUrl = steamfitter,
        };

    /// <summary>
    /// A caller's token. <c>sub</c> is the user id and <c>iss</c> becomes the account's home page, and the
    /// service reads both with <c>First</c> - so both are present unless a test is about their absence.
    /// </summary>
    private static ClaimsPrincipal Principal(Guid userId, string iss = Issuer, bool sub = true)
    {
        var claims = new List<Claim>();

        if (sub)
        {
            claims.Add(new Claim("sub", userId.ToString()));
        }

        if (iss is not null)
        {
            claims.Add(new Claim("iss", iss));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Bearer"));
    }

    /// <summary>
    /// The service over the test's own database, with the <em>real</em> queue behind it.
    /// </summary>
    /// <remarks>
    /// A substituted <c>IXApiQueueService</c> would let a test assert the entity handed over, which is
    /// nearly the same thing - but the row as PostgreSQL stores it is what the background service reads,
    /// and one of the columns is <c>StatementJson</c>, a <c>text</c> column holding the whole contract.
    /// </remarks>
    private XApiService Service(
        XApiOptions options = null,
        ClaimsPrincipal user = null,
        ClientOptions clients = null) =>
        new(Db,
            user ?? Principal(UserId),
            options ?? Options(),
            new XApiQueueService(Db, NullLogger<XApiQueueService>.Instance),
            clients ?? Clients(),
            Log);

    private async Task<int> QueuedCount()
    {
        await using var context = NewContext();

        return await context.XApiQueuedStatements.CountAsync(Ct);
    }

    /// <summary>The one row the call under test queued, read back through a cold change tracker.</summary>
    private async Task<XApiQueuedStatementEntity> Queued()
    {
        await using var context = NewContext();

        return Assert.Single(await context.XApiQueuedStatements.ToListAsync(Ct));
    }

    private async Task<JsonElement> QueuedStatement() => Json(await Queued());

    /// <summary>
    /// The categories of the statement <paramref name="act"/> queues. Hoisted out of the two tests that
    /// want them because <c>CS4007</c> forbids a <c>JsonElement</c> span crossing an <c>await</c>.
    /// </summary>
    private async Task<List<JsonElement>> Categories(Func<Task<bool>> act)
    {
        await act();

        return Items(await QueuedStatement(), "context.contextActivities.category").ToList();
    }

    /// <summary>
    /// The statement as the LRS will receive it. Cloned, because the document owning the element is
    /// disposed on the way out.
    /// </summary>
    private static JsonElement Json(XApiQueuedStatementEntity row)
    {
        using var document = JsonDocument.Parse(row.StatementJson);

        return document.RootElement.Clone();
    }

    /// <summary>
    /// A dotted path through a statement - <c>"context.team.account.name"</c>. Reports the element it gave
    /// up in, because a statement is nested deeply enough that "missing property" on its own says nothing.
    /// </summary>
    /// <remarks>
    /// This cannot reach an extension: an extension's key is an IRI and therefore full of dots. Use
    /// <see cref="Extension"/>.
    /// </remarks>
    private static JsonElement At(JsonElement element, string path)
    {
        var current = element;

        foreach (var name in path.Split('.'))
        {
            if (!current.TryGetProperty(name, out var next))
            {
                throw new XunitException($"No '{name}' on the way to '{path}' in {element}");
            }

            current = next;
        }

        return current;
    }

    private static string Text(JsonElement element, string path) => At(element, path).GetString();

    /// <summary>
    /// The array at <paramref name="path"/>. Named <c>Items</c> rather than <c>Array</c> so it does not
    /// shadow <c>System.Array</c> inside this class.
    /// </summary>
    private static IEnumerable<JsonElement> Items(JsonElement element, string path) =>
        At(element, path).EnumerateArray();

    /// <summary>
    /// One extension of the map at <paramref name="path"/>, keyed by its IRI - which
    /// <see cref="At"/> cannot reach, because an IRI contains dots.
    /// </summary>
    private static JsonElement Extension(JsonElement element, string path, string iri)
    {
        var extensions = At(element, path);

        if (!extensions.TryGetProperty(iri, out var value))
        {
            throw new XunitException($"No '{iri}' among the extensions at '{path}' in {element}");
        }

        return value;
    }

    /// <summary>
    /// Everything an assertion needs to exist: a MSEL, a framework, a competency and a scale.
    /// </summary>
    private sealed record Graph(
        MselEntity Msel,
        CompetencyFrameworkEntity Framework,
        CompetencyEntity Competency,
        ProficiencyScaleEntity Scale,
        ProficiencyLevelEntity[] Levels)
    {
        /// <summary>The middle level, so a scaled score is neither 0 nor 1 by accident.</summary>
        public ProficiencyLevelEntity Level => Levels[Levels.Length / 2];
    }

    /// <remarks>
    /// <c>IdNumber</c> is assigned after construction on both the framework and the competency, because
    /// the seed helpers default it to a fresh value rather than to null - and one theory case here is
    /// about a competency that has none.
    /// </remarks>
    private async Task<Graph> SeedAssertionGraph(
        string competencyIdNumber = "https://competencies.test/c/17",
        int[] values = null)
    {
        var msel = TestData.Msel();
        var framework = TestData.CompetencyFramework();
        framework.IdNumber = "https://frameworks.test/f/1";
        var competency = TestData.Competency(framework.Id);
        competency.IdNumber = competencyIdNumber;

        var scale = new ProficiencyScaleEntity
        {
            Id = Guid.NewGuid(),
            Name = $"scale-{Guid.NewGuid()}",
            Description = "Seeded by XApiServiceTests",
            CreatedBy = Guid.NewGuid(),
        };

        var levels = (values ?? [1, 3, 5])
            .Select((value, index) => new ProficiencyLevelEntity
            {
                Id = Guid.NewGuid(),
                ProficiencyScaleId = scale.Id,
                Name = $"level-{value}",
                Description = "Seeded by XApiServiceTests",
                Value = value,
                DisplayOrder = index,
                CreatedBy = Guid.NewGuid(),
            })
            .ToArray();

        await Seed(msel, framework, competency, scale);
        await Seed(levels);

        return new Graph(msel, framework, competency, scale, levels);
    }

    /// <summary>
    /// An assertion naming <paramref name="graph"/>'s middle proficiency level.
    /// </summary>
    /// <remarks>
    /// <c>CompetencyAssertion</c> is a class with settable properties, so a test that wants a different
    /// id mutates the object this returns. That is safe here where it would not be over the wire: the
    /// object is handed straight to the service, so "absent" and "null" are the same thing.
    /// </remarks>
    private static ViewModels.CompetencyAssertion Assertion(
        Graph graph,
        string comment = null,
        Guid? teamId = null,
        Guid? scenarioEventId = null,
        int? moveNumber = null,
        int? groupNumber = null) =>
        new()
        {
            MselId = graph.Msel.Id,
            CompetencyId = graph.Competency.Id,
            ProficiencyLevelId = graph.Level.Id,
            Comment = comment,
            TeamId = teamId,
            ScenarioEventId = scenarioEventId,
            MoveNumber = moveNumber,
            GroupNumber = groupNumber,
        };
}
