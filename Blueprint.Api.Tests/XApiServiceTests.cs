// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Security.Claims;
using System.Text.Json;
using System.Threading.Tasks;
using Blueprint.Api.Data.Models;
using Blueprint.Api.Infrastructure.Options;
using Blueprint.Api.Services;
using Blueprint.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Sdk;

namespace Blueprint.Api.Tests;

/// <summary>
/// <c>XApiService</c> - the eleven methods that turn something that happened in blueprint into a TinCan
/// statement and hand it to <c>XApiQueueService</c>. Everything an installation learns about what its
/// participants did comes out of this file, so what a statement says is the whole contract.
/// </summary>
/// <remarks>
/// <para>
/// The service is constructed directly over the test's own database: it takes a <c>BlueprintContext</c>,
/// an <c>IPrincipal</c>, the options, the queue, the sibling urls and a logger, and asks nobody for
/// permission - so it needs no host. The queue is the <em>real</em> <c>XApiQueueService</c> rather than a
/// substitute, because a statement's whole observable effect is the row it leaves behind, and that row is
/// what <c>XApiBackgroundServiceTests</c> then sends.
/// </para>
/// <para>
/// <strong>Nothing here is authorized.</strong> No method consults <c>IBlueprintAuthorizationService</c>
/// or any of the eight <c>Msel*Requirement</c> helpers, and neither does <c>XApiController</c> - whose
/// four routes carry only <c>BaseController</c>'s bare <c>[Authorize]</c>. So any authenticated caller
/// may assert any competency about any MSEL and read every statement the LRS holds for one. That is
/// characterized at the surface in <c>XApiEndpointTests</c>, where it is visible as a status code.
/// </para>
/// <para>
/// <strong><c>IsConfigured()</c> requires both the flag and the username; the background service reads
/// the username alone.</strong> So an installation that sets <c>Enabled: false</c> while leaving
/// credentials in place stops producing statements here but goes on flushing whatever is already queued
/// (<c>XApiBackgroundServiceTests.Start_WithXApiDisabledButStillCredentialed_SendsAnyway</c>). The two
/// halves of the feature read different switches.
/// </para>
/// <para>
/// <strong>An unconfigured service reports success.</strong> Every method returns <c>true</c> having
/// queued nothing, and <c>GetStatementsAsync</c> answers an empty statement list, so a caller cannot
/// tell "xAPI is off" from "this worked" or from "there is nothing to show". See
/// <see cref="WithXApiTurnedOff_TheStatementIsNotQueuedAndTheCallerIsToldItSucceeded"/>.
/// </para>
/// <para>
/// <strong><c>GetStatementsAsync</c> cannot be stubbed.</strong> <c>XApiService.cs:843</c> does
/// <c>using var httpClient = new HttpClient()</c> rather than taking the <c>IHttpClientFactory</c> the
/// rest of blueprint's outbound calls go through, so no test can see the request it builds or answer it.
/// Only its three early returns are reachable, plus the transport failure in
/// <see cref="GetStatements_WithAnLrsThatCannotBeReached_ThrowsWhereANonSuccessStatusIsSwallowed"/>,
/// which is aimed at a closed port on loopback. Per this branch's rule the defect is characterized, not
/// refactored - taking the factory as a dependency would turn that test into an ordinary one.
/// </para>
/// <para>
/// <strong><c>ApiUrl</c> and <c>UiUrl</c> take opposite trailing-slash conventions</strong>, and nothing
/// documents or validates either: <c>ApiUrl</c> is concatenated bare (<c>ApiUrl + "msel/" + id</c>) so it
/// must end in a slash, and <c>UiUrl</c> is concatenated with an already-rooted path
/// (<c>UiUrl + "/msel/" + id</c>) so it must not. <c>appsettings.json</c> ships both empty. The two
/// conventions are pinned by <see cref="AnApiUrlWithNoTrailingSlash_ProducesAMalformedActivityId"/> and
/// <see cref="AUiUrlWithATrailingSlash_ProducesADoubleSlashedMoreInfoUrl"/>.
/// </para>
/// <para>
/// Per this branch's rule, every test characterizes rather than fixes, and says what fixing it will do
/// to the test.
/// </para>
/// </remarks>
public class XApiServiceTests(DatabaseFixture fixture) : DatabaseTestBase(fixture)
{
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

    /// <remarks>
    /// The caller is told <c>true</c>, which every caller in the codebase reads as "recorded". Returning
    /// <c>false</c>, or anything distinguishing "not configured" from "queued", turns this red.
    /// </remarks>
    [Fact]
    public async Task WithXApiTurnedOff_TheStatementIsNotQueuedAndTheCallerIsToldItSucceeded()
    {
        var msel = BlueprintAppFactory.Msel();
        await Seed(msel);

        Assert.True(await Service(Options(enabled: false)).MselViewedAsync(msel, Ct));
        Assert.Equal(0, await QueuedCount());
    }

    [Fact]
    public async Task WithNoLrsUsername_NothingIsQueuedEither()
    {
        var msel = BlueprintAppFactory.Msel();
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
        var msel = BlueprintAppFactory.Msel();
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
        var msel = BlueprintAppFactory.Msel();
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
        var msel = BlueprintAppFactory.Msel();
        await Seed(msel);

        await Service(user: Principal(actor.Id)).MselViewedAsync(msel, Ct);

        var statement = await QueuedStatement();
        Assert.Equal("Ada Lovelace", Text(statement, "actor.name"));
        Assert.Equal(actor.Id.ToString(), Text(statement, "actor.account.name"));
        Assert.Equal(Issuer, Text(statement, "actor.account.homePage"));
    }

    /// <remarks>
    /// <c>FirstOrDefault</c> over the users table, so a statement from a caller blueprint has never
    /// provisioned carries an account but no name - and the LRS has no way to resolve one.
    /// Auto-provisioning makes this rare rather than impossible: <c>UserClaimsService</c> only provisions
    /// on a request that reaches the claims transformer. Throwing, or defaulting the name, turns this red.
    /// </remarks>
    [Fact]
    public async Task ForACallerWithNoUserRow_TheActorHasAnAccountAndNoName()
    {
        var msel = BlueprintAppFactory.Msel();
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
        var msel = BlueprintAppFactory.Msel();
        await Seed(msel);

        await Service(Options(issuerUrl: configured), Principal(UserId, iss)).MselViewedAsync(msel, Ct);

        Assert.Equal(expected, Text(await QueuedStatement(), "actor.account.homePage"));
    }

    /// <remarks>
    /// <c>Claims.First(c => c.Type == "sub")</c>, so a token without one throws before anything is queued -
    /// and the <c>?.Value</c> written after it is dead, because <c>First</c> cannot return null. Every
    /// blueprint token carries <c>sub</c>, so this is a shape defect rather than a live one; using
    /// <c>FirstOrDefault</c>, or <c>ClaimsPrincipalExtensions.GetId</c>, turns this red.
    /// </remarks>
    [Fact]
    public async Task WithNoSubClaim_QueuingAStatementThrows()
    {
        var msel = BlueprintAppFactory.Msel();
        await Seed(msel);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Service(user: Principal(UserId, sub: false)).MselViewedAsync(msel, Ct));

        Assert.Equal(0, await QueuedCount());
    }

    /// <remarks>
    /// The same <c>First</c>, and this one is reachable: <c>iss</c> is read even when
    /// <c>XApiOptions.IssuerUrl</c> is configured, so a token minted without it - which is every token the
    /// harness mints, and any in a deployment that does not set the claim - cannot produce a statement.
    /// Reading the claim only in the branch that needs it turns this red.
    /// </remarks>
    [Fact]
    public async Task WithNoIssClaim_QueuingAStatementThrowsEvenWhenTheIssuerIsConfigured()
    {
        var msel = BlueprintAppFactory.Msel();
        await Seed(msel);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Service(Options(issuerUrl: "https://configured.test/"), Principal(UserId, iss: null))
                .MselViewedAsync(msel, Ct));

        Assert.Equal(0, await QueuedCount());
    }

    /// <remarks>
    /// <para>
    /// <c>appsettings.json</c> ships <c>UiUrl</c> empty, so an installation that turns xAPI on without
    /// also setting it records <c>new Uri("" + "/msel/…")</c> — and on Unix a rooted path is a valid
    /// absolute URI, resolved against the local filesystem. So the statement is queued and sent with a
    /// <c>file://</c> link in it, rather than failing. Nothing validates the options at startup.
    /// </para>
    /// <para>
    /// The same misconfiguration is a <c>UriFormatException</c> on Windows, where a rooted path is not
    /// an absolute URI — so this pins the answer on Linux, which is what CI and every deployed
    /// container run. Either way nothing usable reaches the LRS; validating the option turns both
    /// behaviours into one.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task WithNoUiUrl_TheActivitysMoreInfoPointsAtTheLocalFilesystem()
    {
        var msel = BlueprintAppFactory.Msel();
        await Seed(msel);

        await Service(Options(uiUrl: string.Empty)).MselViewedAsync(msel, Ct);

        Assert.Equal(
            $"file:///msel/{msel.Id}",
            Text(await QueuedStatement(), "object.definition.moreInfo"));
    }

    /// <remarks>
    /// The team block is the one place the empty option is not survivable: a team's
    /// <c>account.homePage</c> is <c>new Uri(UiUrl)</c> with nothing appended, and an empty string is
    /// not a URI on any platform. So an installation with xAPI on and <c>UiUrl</c> unset records
    /// statements for a caller on no team and throws for a caller on one.
    /// </remarks>
    [Fact]
    public async Task WithNoUiUrl_AStatementNamingATeamThrows()
    {
        var msel = BlueprintAppFactory.Msel();
        var team = BlueprintAppFactory.Team(msel.Id);
        await Seed(msel, team);
        var actor = await Actor().WithId(UserId).OnTeam(team).SeedAsync();

        await Assert.ThrowsAsync<UriFormatException>(
            () => Service(Options(uiUrl: string.Empty), Principal(actor.Id)).MselViewedAsync(msel, Ct));

        Assert.Equal(0, await QueuedCount());
    }

    /// <remarks>
    /// Concatenation with no separator, where <c>BuildIntegrationGroupings</c> two hundred lines below
    /// checks for the trailing slash before appending. So an <c>ApiUrl</c> written without one produces
    /// <c>…/apimsel/{id}</c>, an activity id the LRS will store and nothing will ever match again.
    /// Normalizing the url turns this red.
    /// </remarks>
    [Fact]
    public async Task AnApiUrlWithNoTrailingSlash_ProducesAMalformedActivityId()
    {
        var msel = BlueprintAppFactory.Msel();
        await Seed(msel);

        await Service(Options(apiUrl: "https://blueprint.test/api")).MselViewedAsync(msel, Ct);

        Assert.Equal($"https://blueprint.test/apimsel/{msel.Id}", (await Queued()).ActivityId);
    }

    /// <remarks>
    /// And the opposite convention one option along: <c>UiUrl</c> is concatenated with a path that already
    /// begins with a slash, so the value that makes <c>ApiUrl</c> work is the value that breaks this one.
    /// Neither is documented, neither is validated, and <c>appsettings.json</c> ships both empty - so the
    /// only way to configure xAPI correctly is to read this method. Normalizing either url turns this red.
    /// </remarks>
    [Fact]
    public async Task AUiUrlWithATrailingSlash_ProducesADoubleSlashedMoreInfoUrl()
    {
        var msel = BlueprintAppFactory.Msel();
        await Seed(msel);

        await Service(Options(uiUrl: "https://blueprint.test/")).MselViewedAsync(msel, Ct);

        Assert.Equal(
            $"https://blueprint.test//msel/{msel.Id}",
            Text(await QueuedStatement(), "object.definition.moreInfo"));
    }

    [Fact]
    public async Task TheStatementIsCategorizedByWhatTheMselIsBeingUsedFor()
    {
        var msel = BlueprintAppFactory.Msel();
        await Seed(msel);

        await Service().MselViewedAsync(msel, Ct);

        var category = Assert.Single(Items(await QueuedStatement(), "context.contextActivities.category"));
        Assert.Equal($"{ApiUrl}category/planning", Text(category, "id"));
        Assert.Equal("Planning", Text(category, "definition.name.en-US"));
        Assert.Equal(
            "http://id.tincanapi.com/activitytype/category", Text(category, "definition.type"));
    }

    /// <remarks>
    /// All six callers of <c>CreateAsync</c> pass an empty dictionary for both, so the two blocks that
    /// would build them (<c>XApiService.cs:183</c> and <c>:201</c>) never run and no statement this service
    /// produces through <c>CreateAsync</c> names what it happened inside. The two paths that do name a
    /// parent - <c>AssertCompetencyAsync</c> and <c>RecordCheckboxChangeAsync</c> - build their statements
    /// by hand instead. Passing the MSEL as the parent turns this red.
    /// </remarks>
    [Fact]
    public async Task TheStatementNamesNoParentAndNoOtherActivity()
    {
        var msel = BlueprintAppFactory.Msel();
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
        var msel = BlueprintAppFactory.Msel();
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
        var msel = BlueprintAppFactory.Msel();
        var team = BlueprintAppFactory.Team(msel.Id);
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

    /// <remarks>
    /// <para>
    /// <strong><c>XApiOptions.EmailDomain</c> is dead.</strong> xAPI allows an agent exactly one
    /// inverse-functional identifier, and TinCan enforces it on the way out: <c>Agent.ToJObject</c>
    /// writes <c>account</c> when there is one and only then falls back to <c>mbox</c>. Both team blocks
    /// (<c>XApiService.cs:168</c> and <c>:559</c>) build the mailbox first and assign the account four
    /// lines later, so the address is computed, stored on the object, and dropped by the serializer.
    /// Those two lines are the option's only readers in the whole codebase, so configuring it does
    /// nothing at all.
    /// </para>
    /// <para>
    /// This is the good outcome by accident: <c>ShortName</c> is free text with no unique index, so two
    /// teams called <c>blue</c> would have been one mailbox to the LRS, where the account name is the
    /// team's id. Deleting the <c>account</c> assignment turns this red and reintroduces that collision;
    /// deleting the <c>mbox</c> assignment instead changes nothing on the wire.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task TheTeamsMailboxIsBuiltFromItsShortNameAndThenDiscarded()
    {
        var msel = BlueprintAppFactory.Msel();
        var team = BlueprintAppFactory.Team(msel.Id);
        team.ShortName = "blue";
        await Seed(msel, team);
        var actor = await Actor().WithId(UserId).OnTeam(team).SeedAsync();

        await Service(Options(emailDomain: "exercise.test"), Principal(actor.Id))
            .MselViewedAsync(msel, Ct);

        var group = At(await QueuedStatement(), "context.team");
        Assert.False(group.TryGetProperty("mbox", out _));
        Assert.Equal(team.Id.ToString(), Text(group, "account.name"));
    }

    /// <remarks>
    /// The group's only member is the caller, so the statement says "this team, of which one person is a
    /// member" whatever the team's real membership is. Listing the team's users turns this red.
    /// </remarks>
    [Fact]
    public async Task TheTeamsOnlyMemberIsTheCaller()
    {
        var msel = BlueprintAppFactory.Msel();
        var team = BlueprintAppFactory.Team(msel.Id);
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
        var msel = BlueprintAppFactory.Msel();
        var other = BlueprintAppFactory.Msel();
        var team = BlueprintAppFactory.Team(other.Id);
        await Seed(msel, other, team);
        var actor = await Actor().WithId(UserId).OnTeam(team).SeedAsync();

        await Service(user: Principal(actor.Id)).MselViewedAsync(msel, Ct);

        var row = await Queued();
        Assert.Null(row.TeamId);
        Assert.False(At(Json(row), "context").TryGetProperty("team", out _));
    }

    /// <remarks>
    /// <c>FirstOrDefaultAsync</c> with no ordering over a MSEL's teams, so a caller on two of them - which
    /// nothing forbids - is attributed to whichever the database returns first, and the same caller's next
    /// statement may be attributed to the other. Ordering, or refusing to guess, turns this red.
    /// </remarks>
    [Fact]
    public async Task ForACallerOnTwoOfTheMselsTeams_OneIsChosenArbitrarily()
    {
        var msel = BlueprintAppFactory.Msel();
        var first = BlueprintAppFactory.Team(msel.Id);
        var second = BlueprintAppFactory.Team(msel.Id);
        await Seed(msel, first, second);
        var actor = await Actor().WithId(UserId).OnTeam(first).OnTeam(second).SeedAsync();

        await Service(user: Principal(actor.Id)).MselViewedAsync(msel, Ct);

        Assert.Contains((await Queued()).TeamId, new Guid?[] { first.Id, second.Id });
    }

    /// <remarks>
    /// A team the MSEL owns but nothing points at is still not this caller's team, because the lookup is
    /// through <c>TeamUsers</c>. This is the ordinary case for an author who is not a participant.
    /// </remarks>
    [Fact]
    public async Task ForACallerOnNoTeamAtAll_TheStatementNamesNoTeam()
    {
        var msel = BlueprintAppFactory.Msel();
        await Seed(msel, BlueprintAppFactory.Team(msel.Id));

        await Service().MselViewedAsync(msel, Ct);

        Assert.Null((await Queued()).TeamId);
    }

    // -------------------------------------------------------------------------------------------------
    // The integrations a statement is grouped under
    // -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheStatementIsGroupedUnderEveryIntegrationTheMselIsPushedTo()
    {
        var msel = BlueprintAppFactory.Msel();
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

    /// <remarks>
    /// The one url in blueprint's <c>ClientSettings</c> shipped without a trailing slash is
    /// <c>PlayerApiUrl</c>, and this is the code that is careful about it - which is what makes the bare
    /// concatenation building the activity id itself (see
    /// <see cref="AnApiUrlWithNoTrailingSlash_ProducesAMalformedActivityId"/>) a defect rather than a
    /// house style.
    /// </remarks>
    [Fact]
    public async Task AnIntegrationsApiUrlIsNormalizedWhetherItEndsInASlashOrNot()
    {
        var msel = BlueprintAppFactory.Msel();
        msel.PlayerViewId = Guid.NewGuid();
        await Seed(msel);

        await Service(clients: Clients(player: "https://player.test")).MselViewedAsync(msel, Ct);

        var grouping = Assert.Single(Items(await QueuedStatement(), "context.contextActivities.grouping"));
        Assert.Equal($"https://player.test/api/views/{msel.PlayerViewId}", Text(grouping, "id"));
        Assert.Equal("Player View", Text(grouping, "definition.name.en-US"));
        Assert.Equal(
            "http://id.tincanapi.com/activitytype/resource", Text(grouping, "definition.type"));
    }

    /// <remarks>
    /// Not an empty <c>grouping</c> array but no <c>grouping</c> property at all: <c>CreateAsync</c> only
    /// assigns the list when the caller handed it a non-empty one, so "grouped under nothing" and "never
    /// asked" are the same statement on the wire.
    /// </remarks>
    [Fact]
    public async Task AnIntegrationWhoseUrlIsNotConfigured_IsNotGroupedUnder()
    {
        var msel = BlueprintAppFactory.Msel();
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
        var msel = BlueprintAppFactory.Msel();
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
        var msel = BlueprintAppFactory.Msel();
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
        var msel = BlueprintAppFactory.Msel();
        await Seed(msel);

        await Service().ExerciseStoppedAsync(msel, Ct);

        Assert.Equal(
            "http://adlnet.gov/expapi/verbs/terminated", Text(await QueuedStatement(), "verb.id"));
    }

    [Fact]
    public async Task MselJoined_QueuesAJoinStatement()
    {
        var msel = BlueprintAppFactory.Msel();
        await Seed(msel);

        await Service().MselJoinedAsync(msel, Ct);

        var row = await Queued();
        Assert.Equal("join", row.Verb);
        Assert.Equal("http://activitystrea.ms/schema/1.0/join", Text(Json(row), "verb.id"));
    }

    /// <remarks>
    /// The join page belongs to no MSEL, so the statement has no registration and the queue row has no
    /// <c>MselId</c> - which is also what makes it invisible to <c>GetStatementsAsync</c>, whose every
    /// query is by registration. A statement nothing can read back.
    /// </remarks>
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

    /// <remarks>
    /// <c>Verb</c> is the last path segment of the verb IRI, which is what the queue's own diagnostics
    /// read. It is right for these five and wrong for a checkbox change - see
    /// <see cref="TheQueueIsToldEveryCheckboxChangeIsACompletion"/>.
    /// </remarks>
    [Fact]
    public async Task TheQueuesVerbColumnIsTheLastSegmentOfTheVerbIri()
    {
        var msel = BlueprintAppFactory.Msel();
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

    /// <remarks>
    /// <c>_context.Msels.FindAsync(id, ct)</c> binds to <c>FindAsync(params object[] keyValues)</c> — the
    /// overload taking the token is <c>FindAsync(object[], CancellationToken)</c> and a bare <c>Guid</c>
    /// does not convert to <c>object[]</c> — so the token is passed as a *second key value*. EF Core 10
    /// strips a trailing <c>CancellationToken</c> from the params array rather than counting it, so the
    /// lookup works; the same call with two real key values is an <c>ArgumentException</c> naming the
    /// single-key entity. Written as it is, this depends on that accommodation, which is why the xUnit
    /// analyzer flags the identical call in a test. <c>XApiQueueService</c>'s two <c>Mark*Async</c>
    /// methods use <c>FindAsync(new object[] { id }, ct)</c>, which is the form that states the intent.
    /// </remarks>
    [Fact]
    public async Task ViewedById_QueuesTheSameStatementAsViewedByTheEntity()
    {
        var msel = BlueprintAppFactory.Msel();
        await Seed(msel);

        Assert.True(await Service().MselViewedAsync(msel.Id, Ct));

        var row = await Queued();
        Assert.Equal("viewed", row.Verb);
        Assert.Equal($"{ApiUrl}msel/{msel.Id}", row.ActivityId);
        Assert.Equal(msel.Id, row.MselId);
    }

    /// <remarks>
    /// The only method on the service that reports failure, and its only caller
    /// (<c>XApiController.cs:75</c>) discards the answer and returns a bare <c>Ok()</c> — so the route is
    /// an empty 200 whether the MSEL exists or not, and the one distinction this method draws is thrown
    /// away one line above the response.
    /// </remarks>
    [Fact]
    public async Task ViewedById_ForAMselThatIsNotThere_IsFalseAndQueuesNothing()
    {
        Assert.False(await Service().MselViewedAsync(Guid.NewGuid(), Ct));
        Assert.Equal(0, await QueuedCount());
    }

    /// <remarks>
    /// This overload has no <c>IsConfigured()</c> check of its own, so it reads the database before
    /// delegating to the overload that does. Harmless — one query — but it means the route is the one
    /// xAPI route that touches the database with the feature turned off.
    /// </remarks>
    [Fact]
    public async Task ViewedById_WithXApiTurnedOff_StillLooksTheMselUpAndSaysItSucceeded()
    {
        var msel = BlueprintAppFactory.Msel();
        await Seed(msel);
        var service = Service(Options(enabled: false));

        Assert.True(await service.MselViewedAsync(msel.Id, Ct));
        Assert.False(await service.MselViewedAsync(Guid.NewGuid(), Ct));
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
            Assertion(graph, comment: "handled the escalation well"), Ct);

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

        await Service().AssertCompetencyAsync(Assertion(graph), Ct);

        Assert.False(At(await QueuedStatement(), "result").TryGetProperty("response", out _));
    }

    /// <remarks>
    /// One level, so <c>maxValue == minValue</c> and the guard at <c>XApiService.cs:535</c> leaves
    /// <c>scaled</c> unset rather than dividing by zero. The raw score is still recorded, so the LRS has
    /// the value and no way to place it - which is the honest answer for a scale with one point on it.
    /// </remarks>
    [Fact]
    public async Task AssertCompetency_AgainstAScaleWithOneLevel_RecordsNoScaledScore()
    {
        var graph = await SeedAssertionGraph(values: [4]);

        await Service().AssertCompetencyAsync(Assertion(graph), Ct);

        var statement = await QueuedStatement();
        Assert.Equal(4, At(statement, "result.score.raw").GetDouble());
        Assert.False(At(statement, "result.score").TryGetProperty("scaled", out _));
    }

    /// <remarks>
    /// The confidence extension is the constant <c>1.0</c> - nothing in the request can express how sure
    /// the assessor was, and every assertion blueprint has ever sent claims total certainty. Reading it
    /// from the assertion turns this red.
    /// </remarks>
    [Fact]
    public async Task AssertCompetency_AlwaysClaimsTotalConfidence()
    {
        var graph = await SeedAssertionGraph();

        await Service().AssertCompetencyAsync(Assertion(graph), Ct);

        Assert.Equal(
            1.0,
            Extension(
                await QueuedStatement(),
                "context.extensions",
                "https://w3id.org/xapi/tla/extensions/confidence").GetDouble());
    }

    /// <remarks>
    /// <strong>The statement does not say who was assessed.</strong> <c>CompetencyAssertion</c> has no
    /// participant field, the actor is the caller, and the team - when one is named - lists the caller as
    /// its only member. So an instructor rating six participants writes six statements about themselves,
    /// and the LRS cannot attribute any of them. Naming the participant, as actor or in
    /// <c>context.instructor</c>, turns this red.
    /// </remarks>
    [Fact]
    public async Task AssertCompetency_RecordsTheAssessorAndNotWhoWasAssessed()
    {
        var graph = await SeedAssertionGraph();
        var team = BlueprintAppFactory.Team(graph.Msel.Id);
        await Seed(team);
        var assessor = await Actor().WithId(UserId).WithName("The Assessor").SeedAsync();
        await Actor().WithName("The Participant").OnTeam(team).SeedAsync();

        await Service(user: Principal(assessor.Id))
            .AssertCompetencyAsync(Assertion(graph, teamId: team.Id), Ct);

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

        await Service().AssertCompetencyAsync(Assertion(graph), Ct);

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

        await Service().AssertCompetencyAsync(Assertion(graph), Ct);

        Assert.Equal(
            expected ?? $"{ApiUrl}competencies/{graph.Competency.Id}",
            (await Queued()).ActivityId);
    }

    [Fact]
    public async Task AssertCompetency_AboutAScenarioEventMoveAndGroup_NamesEachAsAGrouping()
    {
        var graph = await SeedAssertionGraph();
        var scenarioEvent = BlueprintAppFactory.ScenarioEvent(graph.Msel.Id);
        await Seed(scenarioEvent);

        await Service().AssertCompetencyAsync(
            Assertion(graph, scenarioEventId: scenarioEvent.Id, moveNumber: 2, groupNumber: 3), Ct);

        var grouping = Items(await QueuedStatement(), "context.contextActivities.grouping")
            .Select(x => Text(x, "id"))
            .ToList();

        Assert.Contains($"{ApiUrl}scenarioevents/{scenarioEvent.Id}", grouping);
        Assert.Contains($"{ApiUrl}msels/{graph.Msel.Id}/moves/2", grouping);
        Assert.Contains($"{ApiUrl}msels/{graph.Msel.Id}/moves/2/groups/3", grouping);
    }

    /// <remarks>
    /// A group number with no move is numbered under move zero, so a group of move three and the same
    /// group number in move zero are one activity to the LRS. Requiring the move, or leaving the group
    /// out, turns this red.
    /// </remarks>
    [Fact]
    public async Task AssertCompetency_AboutAGroupWithNoMove_NumbersItUnderMoveZero()
    {
        var graph = await SeedAssertionGraph();

        await Service().AssertCompetencyAsync(Assertion(graph, groupNumber: 3), Ct);

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
            await Categories(() => Service().AssertCompetencyAsync(Assertion(graph), Ct)));

        Assert.Equal("https://crucible.sei.cmu.edu/xapi/profile/v1", Text(category, "id"));
    }

    /// <remarks>
    /// A bare <c>ArgumentException</c> for all four, which <c>JsonExceptionFilter</c> answers as a 500 -
    /// see <c>XApiEndpointTests</c>. An <c>EntityNotFoundException</c> would be a 404, which is what the
    /// rest of the API does for an id that is not there.
    /// </remarks>
    [Theory]
    [InlineData("msel")]
    [InlineData("competency")]
    [InlineData("level")]
    [InlineData("event")]
    public async Task AssertCompetency_AboutSomethingThatIsNotThere_Throws(string missing)
    {
        var graph = await SeedAssertionGraph();
        var scenarioEvent = BlueprintAppFactory.ScenarioEvent(graph.Msel.Id);
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
            () => Service().AssertCompetencyAsync(assertion, Ct));

        Assert.Equal(0, await QueuedCount());
    }

    [Fact]
    public async Task AssertCompetency_WithXApiTurnedOff_QueuesNothingAndSaysItSucceeded()
    {
        var graph = await SeedAssertionGraph();

        Assert.True(
            await Service(Options(enabled: false)).AssertCompetencyAsync(Assertion(graph), Ct));

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
        var msel = BlueprintAppFactory.Msel();
        var scenarioEvent = BlueprintAppFactory.ScenarioEvent(msel.Id);
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

    /// <remarks>
    /// <c>Verb = "completed"</c> whatever the statement says, so the column the queue's diagnostics and
    /// anybody querying the table read disagrees with the statement they would read it to find - and a
    /// box being cleared is recorded as a completion. Writing the verb's last segment, as the other five
    /// paths do, turns this red.
    /// </remarks>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TheQueueIsToldEveryCheckboxChangeIsACompletion(bool isChecked)
    {
        var msel = BlueprintAppFactory.Msel();
        var scenarioEvent = BlueprintAppFactory.ScenarioEvent(msel.Id);
        await Seed(msel, scenarioEvent);

        await Service().RecordCheckboxChangeAsync(
            msel.Id, scenarioEvent.Id, Guid.NewGuid(), "Comms restored", isChecked, Ct);

        Assert.Equal("completed", (await Queued()).Verb);
    }

    [Fact]
    public async Task Checkbox_NamesTheMselAsTheParentAndTheEventAsAGrouping()
    {
        var msel = BlueprintAppFactory.Msel();
        var scenarioEvent = BlueprintAppFactory.ScenarioEvent(msel.Id);
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

    /// <remarks>
    /// <c>AssertCompetencyAsync</c> one method above writes <c>msel/{id}</c> for the same MSEL, so the two
    /// statements a single checklist tick can produce name two different activities for the exercise they
    /// happened in. Neither is wrong on its own; they cannot both be right.
    /// </remarks>
    [Fact]
    public async Task TheTwoHandBuiltPathsNameTheMselTwoDifferentWays()
    {
        var graph = await SeedAssertionGraph();
        var scenarioEvent = BlueprintAppFactory.ScenarioEvent(graph.Msel.Id);
        await Seed(scenarioEvent);
        var service = Service();

        await service.AssertCompetencyAsync(Assertion(graph), Ct);
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
        var msel = BlueprintAppFactory.Msel();
        var scenarioEvent = BlueprintAppFactory.ScenarioEvent(msel.Id, deltaSeconds: 3600);
        var first = BlueprintAppFactory.Move(msel.Id, moveNumber: 1, deltaSeconds: 0);
        var second = BlueprintAppFactory.Move(msel.Id, moveNumber: 2, deltaSeconds: 1800);
        var later = BlueprintAppFactory.Move(msel.Id, moveNumber: 3, deltaSeconds: 7200);
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

    /// <remarks>
    /// An event earlier than every move is grouped under none, where <c>GetMovesAndInjects</c> - the method
    /// Gallery's articles are built by - reports the same event as being in the first move. Two answers to
    /// one question, in one service's worth of code.
    /// </remarks>
    [Fact]
    public async Task Checkbox_ForAnEventEarlierThanEveryMove_IsGroupedUnderNoMove()
    {
        var msel = BlueprintAppFactory.Msel();
        var scenarioEvent = BlueprintAppFactory.ScenarioEvent(msel.Id, deltaSeconds: 60);
        await Seed(msel, scenarioEvent, BlueprintAppFactory.Move(msel.Id, deltaSeconds: 1800));

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
        var msel = BlueprintAppFactory.Msel();
        var scenarioEvent = BlueprintAppFactory.ScenarioEvent(msel.Id, deltaSeconds: 3600);
        var move = BlueprintAppFactory.Move(msel.Id, moveNumber: 4, deltaSeconds: 0);
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
        var msel = BlueprintAppFactory.Msel();
        var scenarioEvent = BlueprintAppFactory.ScenarioEvent(msel.Id);
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

    /// <remarks>
    /// Nothing checks that the event belongs to the MSEL, so a tick recorded against one MSEL's event is
    /// filed under another MSEL's registration - and the move lookup, which <em>is</em> scoped by MSEL,
    /// then finds nothing. Checking turns this red.
    /// </remarks>
    [Fact]
    public async Task Checkbox_AboutAnotherMselsEvent_IsRecordedAnyway()
    {
        var msel = BlueprintAppFactory.Msel();
        var other = BlueprintAppFactory.Msel();
        var scenarioEvent = BlueprintAppFactory.ScenarioEvent(other.Id);
        await Seed(msel, other, scenarioEvent);

        await Service().RecordCheckboxChangeAsync(
            msel.Id, scenarioEvent.Id, Guid.NewGuid(), "Comms restored", true, Ct);

        Assert.Equal(msel.Id, (await Queued()).MselId);
    }

    [Fact]
    public async Task Checkbox_IsCategorizedUnderTheCrucibleProfile()
    {
        var msel = BlueprintAppFactory.Msel();
        var scenarioEvent = BlueprintAppFactory.ScenarioEvent(msel.Id);
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
                .GetStatementsAsync(Guid.NewGuid(), null, null, 100, null, Ct));

    /// <remarks>
    /// The same answer as "xAPI is off" and as "this MSEL has produced nothing", so the UI cannot tell a
    /// deleted MSEL from an idle one. A 404, or any distinguishable answer, turns this red.
    /// </remarks>
    [Fact]
    public async Task GetStatements_ForAMselThatIsNotThere_AnswersAnEmptyStatementListToo() =>
        Assert.Equal(
            "{\"statements\":[]}",
            await Service().GetStatementsAsync(Guid.NewGuid(), null, null, 100, null, Ct));

    /// <remarks>
    /// The source names are matched one by one and an unrecognized one matches nothing, so
    /// <c>?source=citee</c> is an empty list and a 200. Rejecting an unknown source turns this red.
    /// </remarks>
    [Fact]
    public async Task GetStatements_ForASourceNobodyRecognizes_AnswersAnEmptyStatementList()
    {
        var msel = BlueprintAppFactory.Msel();
        await Seed(msel);

        Assert.Equal(
            "{\"statements\":[]}",
            await Service().GetStatementsAsync(msel.Id, null, null, 100, "citee", Ct));
    }

    [Fact]
    public async Task GetStatements_ForAnIntegrationTheMselIsNotPushedTo_AnswersAnEmptyStatementList()
    {
        var msel = BlueprintAppFactory.Msel();
        await Seed(msel);

        Assert.Equal(
            "{\"statements\":[]}",
            await Service().GetStatementsAsync(msel.Id, null, null, 100, "cite", Ct));
    }

    /// <remarks>
    /// <para>
    /// The one reachable line past <c>BuildRegistrationIds</c>. <c>XApiService.cs:843</c> constructs its
    /// own <c>HttpClient</c>, so there is no seam to answer on: the port is closed on purpose and what the
    /// test pins is that the refusal escapes as a 500, where an LRS answering 401 or 500 is logged and
    /// skipped. So an LRS that is down is an error the user sees and an LRS that refuses blueprint's
    /// credentials is an empty list. Taking <c>IHttpClientFactory</c> as a dependency makes this an
    /// ordinary test and turns it red.
    /// </para>
    /// <para>
    /// Loopback rather than a hostname, because a name that does not resolve costs a DNS timeout.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task GetStatements_WithAnLrsThatCannotBeReached_ThrowsWhereANonSuccessStatusIsSwallowed()
    {
        var msel = BlueprintAppFactory.Msel();
        await Seed(msel);

        await Assert.ThrowsAnyAsync<HttpRequestException>(
            () => Service(Options(endpoint: "http://127.0.0.1:1/xapi"))
                .GetStatementsAsync(msel.Id, null, null, 100, null, Ct));
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
        var msel = BlueprintAppFactory.Msel();
        var framework = BlueprintAppFactory.CompetencyFramework();
        framework.IdNumber = "https://frameworks.test/f/1";
        var competency = BlueprintAppFactory.Competency(framework.Id);
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
