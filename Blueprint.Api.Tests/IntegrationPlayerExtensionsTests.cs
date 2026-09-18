// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Blueprint.Api.Data.Models;
using Blueprint.Api.Infrastructure.Extensions;
using Blueprint.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Player.Api.Client;
using Xunit;

// Blueprint and IdentityModel both declare a ClientOptions, so the name is ambiguous here.
using ClientOptions = Blueprint.Api.Infrastructure.Options.ClientOptions;

namespace Blueprint.Api.Tests;

/// <summary>
/// <c>IntegrationPlayerExtensions</c> - the five calls blueprint makes to player.api when a MSEL is pushed,
/// pulled, or joined.
/// </summary>
/// <remarks>
/// <para>
/// These are static methods taking their API client as an argument, so they need no host: the database is
/// here for the two that read it, and <see cref="TestHttpHandler"/> stands in for the socket while the
/// generated <c>PlayerApiClient</c>, the <c>HttpClient</c> pipeline and every route it builds run for real.
/// What each test asserts is the request that went out and what was made of what came back, which is the
/// only thing a caller of these methods can observe - none of them returns anything.
/// </para>
/// <para>
/// The wire contract, since nothing else in the repository records it: <c>DeleteView</c> is
/// <c>DELETE api/views/{id}</c> answering 204, <c>CreateView</c> is <c>POST api/views</c> answering 201,
/// <c>CreateTeam</c> is <c>POST api/views/{viewId}/teams</c> answering 201, <c>CreateUser</c> is
/// <c>POST api/users</c> answering 201, <c>CreateApplication</c> is
/// <c>POST api/views/{viewId}/applications</c> answering 201, <c>CreateApplicationInstance</c> is
/// <c>POST api/teams/{teamId}/application-instances</c> answering 201 - and <c>AddUserToTeam</c> is
/// <c>POST api/teams/{teamId}/users/{userId}</c> answering <strong>200</strong>, the one of the seven that
/// does not answer 201. A generated client rejects the wrong status as a problem response, so this matters
/// to anyone stubbing player.api.
/// </para>
/// <para>
/// <strong><c>CreateViewAsync</c> reads the id it was told to use after it has already required a different
/// one.</strong> Line 43 casts <c>msel.PlayerViewId</c> unguarded into the form, and line 49 only then
/// overwrites it with the <c>playerViewId</c> argument - the parameter that exists to supply an id. So a
/// MSEL with no <c>PlayerViewId</c> is <c>InvalidOperationException: Nullable object must have a value.</c>
/// before any request is sent, even when the caller passed one. Swapping the two lines is the fix. See
/// <see cref="CreateView_WithNoViewIdOnTheMsel_ThrowsBeforeSendingEvenWhenGivenOne"/>.
/// </para>
/// <para>
/// <strong>The <c>batchSize</c> argument does nothing.</strong> <c>CreateApplicationsAsync</c> builds its
/// work with <c>msel.PlayerApplications.Select(async application =&gt; ...).ToList()</c>, and an
/// <c>async</c> lambda starts running when it is created - so materializing the list starts every
/// application at once, and the loop that walks it in slices of <c>batchSize</c> is only awaiting tasks
/// already in flight. <c>ClientOptions.PlayerMaxConcurrentRequests</c> is therefore ignored, and a MSEL with
/// two hundred applications opens two hundred concurrent requests to player.api. The same shape appears
/// three more times in <c>IntegrationCiteExtensions</c> and <c>IntegrationGalleryExtensions</c>. See
/// <see cref="CreateApplications_IgnoresTheBatchSizeAndStartsEveryApplicationAtOnce"/>.
/// </para>
/// <para>
/// <strong>A url that does not resolve is sent as null rather than reported.</strong> After placeholder
/// substitution <c>CreateApplicationsAsync</c> runs <c>Uri.TryCreate</c> and, on failure, assigns
/// <c>null</c> - so a typo in an application's url reaches Player as an application with no url, and the
/// operator sees an empty tile rather than an error. The substitution feeding it has the same quiet
/// failure: an integration the MSEL does not have gives <c>Guid?.ToString()</c>, which is the
/// <em>empty string</em>, so <c>{citeEvaluationId}</c> becomes nothing at all rather than a zero guid or a
/// complaint.
/// </para>
/// <para>
/// <strong><c>AddUserToTeamAsync</c> is half idempotent.</strong> Creating the user is wrapped in a
/// <c>try</c>/<c>catch</c> commented "User might already exist, continue"; adding them to the team, one line
/// below, is not - so joining a team a user is already on throws out of the method and into
/// <c>JoinService</c>'s own handler. See <see cref="AddUserToTeam_SwallowsAFailureCreatingTheUser"/> and
/// <see cref="AddUserToTeam_DoesNotSwallowAFailureAddingToTheTeam"/>.
/// </para>
/// <para>
/// <strong><c>PullFromPlayerAsync</c> swallows everything</strong>, so a view that could not be deleted is
/// indistinguishable from one that was - and <c>IntegrationService</c> calls it <em>twice in a row</em>
/// (lines 175-176 and 534-535), so every pull sends two DELETEs and the second is a 404 nobody sees. The
/// duplication lives in <c>IntegrationService</c> and is covered with it; what is pinned here is that one
/// call is one request.
/// </para>
/// <para>
/// Two dead parameters: <c>CreateViewAsync</c> and <c>CreateTeamsAsync</c> both take a
/// <c>BlueprintContext</c> and never touch it. The tests pass <c>null</c>, which is the cheapest possible
/// proof and turns red the moment either starts reading.
/// </para>
/// <para>
/// Per this branch's rule, every test above characterizes rather than fixes, and says what fixing it will do
/// to the test.
/// </para>
/// </remarks>
public class IntegrationPlayerExtensionsTests(DatabaseFixture fixture) : DatabaseTestBase(fixture)
{
    // ---------------------------------------------------------------------------------------------
    // GetPlayerApiClient
    // ---------------------------------------------------------------------------------------------

    /// <remarks>
    /// The composition, in one test: the configured url becomes the base address and the token becomes the
    /// authorization header, both by way of <c>ApiClientsExtensions.GetHttpClient</c>, which
    /// <see cref="ApiClientsExtensionsTests"/> covers on its own.
    /// </remarks>
    [Fact]
    public async Task GetPlayerApiClient_BuildsAClientCarryingTheTokenAndTheApiUrl()
    {
        var handler = Handler().Answers("api/views/*", HttpStatusCode.NoContent);
        var client = IntegrationPlayerExtensions.GetPlayerApiClient(
            handler.AsFactory(), "http://player.example/", await Tokens.Bearer());

        await IntegrationPlayerExtensions.PullFromPlayerAsync(ViewId, client, Ct);

        var sent = Assert.Single(handler.Sent);

        Assert.Equal($"api/views/{ViewId}", sent.Path);
        Assert.Equal(Tokens.Header, sent.Authorization);
    }

    // ---------------------------------------------------------------------------------------------
    // PullFromPlayerAsync
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task PullFromPlayer_DeletesTheView()
    {
        var handler = Handler().Answers($"api/views/{ViewId}", HttpStatusCode.NoContent);

        await IntegrationPlayerExtensions.PullFromPlayerAsync(ViewId, Client(handler), Ct);

        var sent = Assert.Single(handler.Sent);

        Assert.Equal(HttpMethod.Delete, sent.Method);
        Assert.Equal($"api/views/{ViewId}", sent.Path);
    }

    /// <remarks>
    /// <para>
    /// The house style of this file, and of the whole integration layer: <c>catch (System.Exception) { }</c>
    /// with an empty body. A pull that deleted nothing reports exactly what a pull that deleted everything
    /// reports, and <c>IntegrationService</c> goes on to clear the MSEL's <c>PlayerViewId</c> either way -
    /// so the view is orphaned in Player with nothing left pointing at it.
    /// </para>
    /// <para>
    /// Removing the swallow turns all three of these cases red at once, which is the evidence they are one
    /// decision rather than three.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task PullFromPlayer_SwallowsARefusal(HttpStatusCode status)
    {
        var handler = Handler().Answers($"api/views/{ViewId}", status);

        await IntegrationPlayerExtensions.PullFromPlayerAsync(ViewId, Client(handler), Ct);

        Assert.Single(handler.Sent);
    }

    [Fact]
    public async Task PullFromPlayer_SwallowsAnUnreachablePlayer()
    {
        var handler = Handler().Throws($"api/views/{ViewId}");

        await IntegrationPlayerExtensions.PullFromPlayerAsync(ViewId, Client(handler), Ct);

        Assert.Single(handler.Sent);
    }

    // ---------------------------------------------------------------------------------------------
    // CreateViewAsync
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task CreateView_PostsTheMselsNameDescriptionAndId()
    {
        var msel = BlueprintAppFactory.Msel();
        msel.PlayerViewId = ViewId;

        var handler = Handler().AnswersJson("api/views", ViewJson, HttpStatusCode.Created);

        await IntegrationPlayerExtensions.CreateViewAsync(msel, null, Client(handler), null, Ct);

        var sent = Assert.Single(handler.Sent);

        Assert.Equal(HttpMethod.Post, sent.Method);
        Assert.Equal("api/views", sent.Path);

        var form = Body(sent.Body);

        Assert.Equal(ViewId.ToString(), form["id"].GetString());
        Assert.Equal(msel.Name, form["name"].GetString());
        Assert.Equal(msel.Description, form["description"].GetString());
    }

    /// <remarks>
    /// Both are hard-coded rather than taken from the MSEL: a pushed view is Active whatever the MSEL's own
    /// status is, and Player is always asked to build an admin team.
    /// </remarks>
    [Fact]
    public async Task CreateView_AlwaysAsksForAnActiveViewWithAnAdminTeam()
    {
        var msel = BlueprintAppFactory.Msel();
        msel.PlayerViewId = ViewId;

        var handler = Handler().AnswersJson("api/views", ViewJson, HttpStatusCode.Created);

        await IntegrationPlayerExtensions.CreateViewAsync(msel, null, Client(handler), null, Ct);

        var form = Body(Assert.Single(handler.Sent).Body);

        Assert.Equal("Active", form["status"].GetString());
        Assert.True(form["createAdminTeam"].GetBoolean());
    }

    /// <remarks>
    /// The argument wins when there is one, which is what it is for - <c>IntegrationService</c> passes the id
    /// of a view it is re-creating.
    /// </remarks>
    [Fact]
    public async Task CreateView_PrefersTheSuppliedViewIdOverTheMsels()
    {
        var msel = BlueprintAppFactory.Msel();
        msel.PlayerViewId = Guid.Parse("22222222-2222-2222-2222-222222222222");

        var handler = Handler().AnswersJson("api/views", ViewJson, HttpStatusCode.Created);

        await IntegrationPlayerExtensions.CreateViewAsync(msel, ViewId, Client(handler), null, Ct);

        Assert.Equal(ViewId.ToString(), Body(Assert.Single(handler.Sent).Body)["id"].GetString());
    }

    /// <remarks>
    /// <para>
    /// The defect in this class's remarks. <c>Id = (Guid)msel.PlayerViewId</c> runs while the form is being
    /// built; <c>if (playerViewId != null) viewForm.Id = (Guid)playerViewId</c> runs six lines later. So the
    /// argument cannot rescue a MSEL that has no view id of its own, which is the one case it would be
    /// needed for, and the failure is an <c>InvalidOperationException</c> from a cast rather than anything
    /// naming the MSEL.
    /// </para>
    /// <para>
    /// The assertion that nothing was sent is the half that matters: a fix that reordered the two lines would
    /// make this a 201, and a fix that guarded the cast would still send nothing but would say something
    /// useful. Either way this test goes red.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task CreateView_WithNoViewIdOnTheMsel_ThrowsBeforeSendingEvenWhenGivenOne()
    {
        var msel = BlueprintAppFactory.Msel();

        Assert.Null(msel.PlayerViewId);

        var handler = Handler().AnswersJson("api/views", ViewJson, HttpStatusCode.Created);

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            IntegrationPlayerExtensions.CreateViewAsync(msel, ViewId, Client(handler), null, Ct));

        Assert.Equal("Nullable object must have a value.", thrown.Message);
        Assert.Empty(handler.Sent);
    }

    /// <remarks>
    /// Nothing here is swallowed, unlike the pull - so a refused create fails the push, which is right.
    /// </remarks>
    [Fact]
    public async Task CreateView_DoesNotSwallowARefusal()
    {
        var msel = BlueprintAppFactory.Msel();
        msel.PlayerViewId = ViewId;

        var handler = Handler().Answers("api/views", HttpStatusCode.Conflict);

        await Assert.ThrowsAnyAsync<ApiException>(() =>
            IntegrationPlayerExtensions.CreateViewAsync(msel, null, Client(handler), null, Ct));
    }

    // ---------------------------------------------------------------------------------------------
    // CreateTeamsAsync
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task CreateTeams_CreatesEachTeamUnderTheViewWithItsBlueprintId()
    {
        var msel = await SeedMsel();
        var first = BlueprintAppFactory.Team(msel.Id);
        var second = BlueprintAppFactory.Team(msel.Id);
        await Seed(first, second);

        var handler = Handler().AnswersJson($"api/views/{ViewId}/teams", TeamJson, HttpStatusCode.Created);

        await IntegrationPlayerExtensions.CreateTeamsAsync(
            await Reload(msel.Id), Client(handler), null, [], Ct);

        var ids = handler.Sent.Select(x => Body(x.Body)["id"].GetString()).ToList();

        Assert.Equal(2, ids.Count);
        Assert.Contains(first.Id.ToString(), ids);
        Assert.Contains(second.Id.ToString(), ids);
    }

    [Fact]
    public async Task CreateTeams_CreatesEachTeamsUsersAndAddsThemToIt()
    {
        var msel = await SeedMsel();
        var team = BlueprintAppFactory.Team(msel.Id);
        await Seed(team);

        var actor = await Actor().WithName("Ada").OnTeam(team).SeedAsync();

        var handler = Handler()
            .AnswersJson($"api/views/{ViewId}/teams", TeamJson, HttpStatusCode.Created)
            .AnswersJson("api/users", UserJson, HttpStatusCode.Created)
            .AnswersJson($"api/teams/{team.Id}/users/{actor.Id}", UserJson);

        await IntegrationPlayerExtensions.CreateTeamsAsync(
            await Reload(msel.Id), Client(handler), null, [], Ct);

        Assert.Equal(
            [$"api/views/{ViewId}/teams", "api/users", $"api/teams/{team.Id}/users/{actor.Id}"],
            handler.Paths);

        var user = Body(handler.Sent[1].Body);

        Assert.Equal(actor.Id.ToString(), user["id"].GetString());
        Assert.Equal("Ada", user["name"].GetString());
    }

    /// <remarks>
    /// The set is the push's memory of which users Player already knows about; <c>IntegrationService</c>
    /// fills it from Player before the teams are walked. A user already in it is added to the team without
    /// being created again.
    /// </remarks>
    [Fact]
    public async Task CreateTeams_SkipsCreatingAUserPlayerAlreadyHas()
    {
        var msel = await SeedMsel();
        var team = BlueprintAppFactory.Team(msel.Id);
        await Seed(team);

        var actor = await Actor().OnTeam(team).SeedAsync();

        var handler = Handler()
            .AnswersJson($"api/views/{ViewId}/teams", TeamJson, HttpStatusCode.Created)
            .AnswersJson($"api/teams/{team.Id}/users/{actor.Id}", UserJson);

        await IntegrationPlayerExtensions.CreateTeamsAsync(
            await Reload(msel.Id), Client(handler), null, [actor.Id], Ct);

        Assert.DoesNotContain("api/users", handler.Paths);
    }

    [Fact]
    public async Task CreateTeams_RecordsACreatedUserSoTheNextTeamDoesNotCreateThemAgain()
    {
        var msel = await SeedMsel();
        var first = BlueprintAppFactory.Team(msel.Id);
        var second = BlueprintAppFactory.Team(msel.Id);
        await Seed(first, second);

        var actor = await Actor().OnTeam(first).OnTeam(second).SeedAsync();

        var handler = Handler()
            .AnswersJson($"api/views/{ViewId}/teams", TeamJson, HttpStatusCode.Created)
            .AnswersJson("api/users", UserJson, HttpStatusCode.Created)
            .AnswersJson($"api/teams/{first.Id}/users/{actor.Id}", UserJson)
            .AnswersJson($"api/teams/{second.Id}/users/{actor.Id}", UserJson);

        var seen = new HashSet<Guid>();

        await IntegrationPlayerExtensions.CreateTeamsAsync(
            await Reload(msel.Id), Client(handler), null, seen, Ct);

        Assert.Single(handler.Paths.Where(x => x == "api/users").ToList());
        Assert.Contains(actor.Id, seen);
    }

    /// <remarks>
    /// The <c>BlueprintContext</c> parameter is never read - the method's own comment says it uses the
    /// eager-loaded teams - so <c>null</c> is a sufficient argument. This test turns red the moment it starts
    /// reading, which is the point of passing null rather than a context.
    /// </remarks>
    [Fact]
    public async Task CreateTeams_NeverReadsTheDatabase()
    {
        var msel = await SeedMsel();
        await Seed(BlueprintAppFactory.Team(msel.Id));

        var handler = Handler().AnswersJson($"api/views/{ViewId}/teams", TeamJson, HttpStatusCode.Created);

        await IntegrationPlayerExtensions.CreateTeamsAsync(
            await Reload(msel.Id), Client(handler), blueprintContext: null, [], Ct);

        Assert.Single(handler.Sent);
    }

    [Fact]
    public async Task CreateTeams_DoesNotSwallowARefusal()
    {
        var msel = await SeedMsel();
        await Seed(BlueprintAppFactory.Team(msel.Id));

        var handler = Handler().Answers($"api/views/{ViewId}/teams", HttpStatusCode.Conflict);
        var loaded = await Reload(msel.Id);

        await Assert.ThrowsAnyAsync<ApiException>(() =>
            IntegrationPlayerExtensions.CreateTeamsAsync(loaded, Client(handler), null, [], Ct));
    }

    // ---------------------------------------------------------------------------------------------
    // CreateApplicationsAsync
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task CreateApplications_PostsEachApplicationUnderTheView()
    {
        var msel = await SeedMsel();
        var application = BlueprintAppFactory.PlayerApplication(msel.Id, name: "Console", icon: "star.png");
        await Seed(application);

        var handler = Handler().AnswersJson($"api/views/{ViewId}/applications", ApplicationJson, HttpStatusCode.Created);

        await CreateApplications(msel.Id, handler);

        var sent = Assert.Single(handler.Sent);

        Assert.Equal(HttpMethod.Post, sent.Method);
        Assert.Equal($"api/views/{ViewId}/applications", sent.Path);

        var body = Body(sent.Body);

        Assert.Equal("Console", body["name"].GetString());
        Assert.Equal("http://application.example/", body["url"].GetString());
        Assert.Equal("star.png", body["icon"].GetString());
        Assert.Equal(ViewId.ToString(), body["viewId"].GetString());
    }

    /// <remarks>
    /// Both are <c>bool?</c> on the blueprint row and on Player's <c>Application</c>, and both are copied
    /// straight across, so an application row that has never had either set arrives with them null and Player
    /// decides what that means.
    /// </remarks>
    [Fact]
    public async Task CreateApplications_CopiesTheEmbeddableAndBackgroundFlags()
    {
        var msel = await SeedMsel();
        await Seed(BlueprintAppFactory.PlayerApplication(msel.Id));

        var handler = Handler().AnswersJson($"api/views/{ViewId}/applications", ApplicationJson, HttpStatusCode.Created);

        await CreateApplications(msel.Id, handler);

        var body = Body(Assert.Single(handler.Sent).Body);

        Assert.True(body["embeddable"].GetBoolean());
        Assert.False(body["loadInBackground"].GetBoolean());
    }

    /// <remarks>
    /// Ten placeholders, five of them the MSEL's own integration ids and five from
    /// <c>ClientSettings</c>. This is how an application lands in Player already pointing at the CITE
    /// evaluation or Gallery exhibit the same push created.
    /// </remarks>
    [Fact]
    public async Task CreateApplications_SubstitutesEveryPlaceholderInTheUrl()
    {
        var msel = await SeedMsel(withIntegrationIds: true);
        await Seed(BlueprintAppFactory.PlayerApplication(msel.Id, url: AllPlaceholders));

        var handler = Handler().AnswersJson($"api/views/{ViewId}/applications", ApplicationJson, HttpStatusCode.Created);

        await CreateApplications(msel.Id, handler);

        var url = Body(Assert.Single(handler.Sent).Body)["url"].GetString();

        Assert.Equal(
            "http://application.example/" +
            $"?msel={msel.Id}" +
            $"&cite={CiteEvaluationId}" +
            $"&exhibit={GalleryExhibitId}" +
            $"&scenario={SteamfitterScenarioId}" +
            $"&view={ViewId}" +
            "&playerUrl=http://player.ui" +
            "&citeUrl=http://cite.ui" +
            "&galleryUrl=http://gallery.ui" +
            "&steamfitterUrl=http://steamfitter.ui" +
            "&blueprintUrl=http://blueprint.ui",
            url);
    }

    [Fact]
    public async Task CreateApplications_SubstitutesTheSamePlaceholdersInTheIcon()
    {
        var msel = await SeedMsel(withIntegrationIds: true);
        await Seed(BlueprintAppFactory.PlayerApplication(msel.Id, icon: "icons/{playerViewId}/{blueprintMselId}.png"));

        var handler = Handler().AnswersJson($"api/views/{ViewId}/applications", ApplicationJson, HttpStatusCode.Created);

        await CreateApplications(msel.Id, handler);

        Assert.Equal(
            $"icons/{ViewId}/{msel.Id}.png",
            Body(Assert.Single(handler.Sent).Body)["icon"].GetString());
    }

    /// <remarks>
    /// <c>Nullable&lt;Guid&gt;.ToString()</c> is the empty string, not a zero guid and not <c>"null"</c>, so
    /// an application url naming an integration the MSEL does not have arrives with the parameter present and
    /// empty. Player stores it; whatever reads it gets nothing. Nothing warns, at push time or after.
    /// </remarks>
    [Fact]
    public async Task CreateApplications_ForAnIntegrationTheMselDoesNotHave_SubstitutesAnEmptyString()
    {
        var msel = await SeedMsel();

        Assert.Null(msel.CiteEvaluationId);

        await Seed(BlueprintAppFactory.PlayerApplication(
            msel.Id, url: "http://application.example/?cite={citeEvaluationId}"));

        var handler = Handler().AnswersJson($"api/views/{ViewId}/applications", ApplicationJson, HttpStatusCode.Created);

        await CreateApplications(msel.Id, handler);

        Assert.Equal(
            "http://application.example/?cite=",
            Body(Assert.Single(handler.Sent).Body)["url"].GetString());
    }

    /// <remarks>
    /// <c>Uri.TryCreate</c> with <c>UriKind.Absolute</c>, and then a scheme check, and then <c>null</c> on
    /// failure with nothing logged. A relative url, a typo in the scheme and a <c>javascript:</c> url are all
    /// the same answer: an application in Player with no url at all. Reporting the bad url - or refusing the
    /// push - turns all three of these cases red.
    /// </remarks>
    [Theory]
    [InlineData("not a url")]
    [InlineData("/relative/path")]
    [InlineData("htp://typo.example/")]
    [InlineData("javascript:alert(1)")]
    public async Task CreateApplications_ForAUrlThatDoesNotResolve_SendsNoUrlAtAll(string url)
    {
        var msel = await SeedMsel();
        await Seed(BlueprintAppFactory.PlayerApplication(msel.Id, url: url));

        var handler = Handler().AnswersJson($"api/views/{ViewId}/applications", ApplicationJson, HttpStatusCode.Created);

        await CreateApplications(msel.Id, handler);

        Assert.Equal(JsonValueKind.Null, Body(Assert.Single(handler.Sent).Body)["url"].ValueKind);
    }

    /// <remarks>
    /// The icon is read with <c>?.Replace(...)</c> and the url with a bare <c>.Replace(...)</c>, so an
    /// application row with no url is a <c>NullReferenceException</c> where one with no icon is fine. Nothing
    /// constrains the column: <c>PlayerApplicationEntity.Url</c> is a nullable string with no
    /// <c>[Required]</c> and no configuration.
    /// </remarks>
    [Fact]
    public async Task CreateApplications_ForAnApplicationWithNoUrl_ThrowsANullReference()
    {
        var msel = await SeedMsel();
        await Seed(BlueprintAppFactory.PlayerApplication(msel.Id, url: null));

        var handler = Handler().AnswersJson($"api/views/{ViewId}/applications", ApplicationJson, HttpStatusCode.Created);

        await Assert.ThrowsAsync<NullReferenceException>(() => CreateApplications(msel.Id, handler));
    }

    [Fact]
    public async Task CreateApplications_ForAnApplicationWithNoIcon_SendsNoIcon()
    {
        var msel = await SeedMsel();
        await Seed(BlueprintAppFactory.PlayerApplication(msel.Id, icon: null));

        var handler = Handler().AnswersJson($"api/views/{ViewId}/applications", ApplicationJson, HttpStatusCode.Created);

        await CreateApplications(msel.Id, handler);

        Assert.Equal(JsonValueKind.Null, Body(Assert.Single(handler.Sent).Body)["icon"].ValueKind);
    }

    [Fact]
    public async Task CreateApplications_CreatesAnInstancePerTeamCarryingItsDisplayOrder()
    {
        var msel = await SeedMsel();
        var first = BlueprintAppFactory.Team(msel.Id);
        var second = BlueprintAppFactory.Team(msel.Id);
        await Seed(first, second);

        var application = BlueprintAppFactory.PlayerApplication(msel.Id);
        await Seed(application);
        await Seed(
            BlueprintAppFactory.PlayerApplicationTeam(application.Id, first.Id, displayOrder: 3),
            BlueprintAppFactory.PlayerApplicationTeam(application.Id, second.Id, displayOrder: 7));

        var handler = Handler()
            .AnswersJson($"api/views/{ViewId}/applications", ApplicationJson, HttpStatusCode.Created)
            .AnswersJson($"api/teams/{first.Id}/application-instances", InstanceJson, HttpStatusCode.Created)
            .AnswersJson($"api/teams/{second.Id}/application-instances", InstanceJson, HttpStatusCode.Created);

        await CreateApplications(msel.Id, handler);

        var instances = handler.Sent
            .Where(x => x.Path.EndsWith("application-instances"))
            .ToDictionary(x => Body(x.Body)["teamId"].GetString(), x => Body(x.Body)["displayOrder"].GetInt32());

        Assert.Equal(3, instances[first.Id.ToString()]);
        Assert.Equal(7, instances[second.Id.ToString()]);
    }

    /// <remarks>
    /// The application id in the instance form is <c>(Guid)playerApplication.Id</c> - the id Player answered
    /// with, not the blueprint row's - so an instance is created against whatever Player named. Player
    /// answering without an id is an <c>InvalidOperationException</c> from that cast.
    /// </remarks>
    [Fact]
    public async Task CreateApplications_UsesTheApplicationIdPlayerAnsweredWith()
    {
        var msel = await SeedMsel();
        var team = BlueprintAppFactory.Team(msel.Id);
        await Seed(team);

        var application = BlueprintAppFactory.PlayerApplication(msel.Id);
        await Seed(application);
        await Seed(BlueprintAppFactory.PlayerApplicationTeam(application.Id, team.Id));

        var playerSaid = Guid.Parse("33333333-3333-3333-3333-333333333333");
        var handler = Handler()
            .AnswersJson($"api/views/{ViewId}/applications",
                $$"""{"id":"{{playerSaid}}","name":"a","viewId":"{{ViewId}}"}""", HttpStatusCode.Created)
            .AnswersJson($"api/teams/{team.Id}/application-instances", InstanceJson, HttpStatusCode.Created);

        await CreateApplications(msel.Id, handler);

        Assert.Equal(
            playerSaid.ToString(),
            Body(handler.Sent[^1].Body)["applicationId"].GetString());
        Assert.NotEqual(application.Id.ToString(), playerSaid.ToString());
    }

    /// <remarks>
    /// <para>
    /// The defect in this class's remarks, and the reason <see cref="TestHttpHandler.HoldsUntil"/> exists.
    /// Three applications, one team each, <c>batchSize: 1</c> - which should mean one application finished
    /// before the next begins. What happens instead is that all three start at once, because
    /// <c>Select(async ...)</c> starts each task as the list is materialized and the batching loop only
    /// awaits what is already running.
    /// </para>
    /// <para>
    /// The handler holds every response until three requests have arrived, so the call can only complete if
    /// three were in flight together. Code that honoured <c>batchSize: 1</c> would have one in flight, the
    /// gate would never open, and this test would fail by cancellation after ten seconds - which is why the
    /// token below is a bounded one rather than the test's own. Asserting the order the requests were
    /// recorded in would be simpler and wrong: it passed five times in isolation and failed once under a
    /// full-suite run, because whether the three creates are recorded before the first instance is up to the
    /// thread pool.
    /// </para>
    /// <para>
    /// Deferring the work - <c>Select(application =&gt; async () =&gt; ...)</c> and invoking a slice at a
    /// time - turns this test red. Without it, <c>PlayerMaxConcurrentRequests</c> is decoration.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task CreateApplications_IgnoresTheBatchSizeAndStartsEveryApplicationAtOnce()
    {
        var msel = await SeedMsel();
        var team = BlueprintAppFactory.Team(msel.Id);
        await Seed(team);

        for (var i = 0; i < 3; i++)
        {
            var application = BlueprintAppFactory.PlayerApplication(msel.Id, name: $"application-{i}");
            await Seed(application);
            await Seed(BlueprintAppFactory.PlayerApplicationTeam(application.Id, team.Id, displayOrder: i));
        }

        var handler = new TestHttpHandler().HoldsUntil(3)
            .AnswersJson($"api/views/{ViewId}/applications", ApplicationJson, HttpStatusCode.Created)
            .AnswersJson($"api/teams/{team.Id}/application-instances", InstanceJson, HttpStatusCode.Created);

        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        bounded.CancelAfter(TimeSpan.FromSeconds(10));

        await IntegrationPlayerExtensions.CreateApplicationsAsync(
            await Reload(msel.Id), Client(handler), Db, batchSize: 1, Options(), bounded.Token);

        Assert.Equal(3, handler.MaxInFlight);
        Assert.Equal(6, handler.Sent.Count);
    }

    [Fact]
    public async Task CreateApplications_WithNoApplications_SendsNothing()
    {
        var msel = await SeedMsel();
        var handler = Handler();

        await CreateApplications(msel.Id, handler);

        Assert.Empty(handler.Sent);
    }

    // ---------------------------------------------------------------------------------------------
    // AddUserToTeamAsync
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task AddUserToTeam_CreatesTheUserThenAddsThem()
    {
        var msel = await SeedMsel();
        var team = BlueprintAppFactory.Team(msel.Id);
        await Seed(team);

        var actor = await Actor().WithName("Grace").SeedAsync();

        var handler = Handler()
            .AnswersJson("api/users", UserJson, HttpStatusCode.Created)
            .AnswersJson($"api/teams/{team.Id}/users/{actor.Id}", UserJson);

        await IntegrationPlayerExtensions.AddUserToTeamAsync(actor.Id, team.Id, Client(handler), Db, Ct);

        Assert.Equal(["api/users", $"api/teams/{team.Id}/users/{actor.Id}"], handler.Paths);
        Assert.Equal("Grace", Body(handler.Sent[0].Body)["name"].GetString());
    }

    /// <remarks>
    /// <para>
    /// The user is looked up in blueprint's own table first, so an id blueprint has never seen is added to
    /// the Player team without a Player user being created for it. Whether that succeeds is Player's
    /// business; nothing here checks.
    /// </para>
    /// <para>
    /// Two mechanisms produce this and either would do on its own, which is worth knowing before touching
    /// the method: the <c>if (user != null)</c> guard skips the create, and the <c>catch</c> around it would
    /// absorb the <c>NullReferenceException</c> if the guard were removed. Replacing the guard with
    /// <c>if (true)</c> leaves this test green - the redundancy is real, and the guard can be deleted
    /// without changing what any caller sees.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AddUserToTeam_ForSomeoneBlueprintDoesNotKnow_AddsThemWithoutCreatingThem()
    {
        var msel = await SeedMsel();
        var team = BlueprintAppFactory.Team(msel.Id);
        await Seed(team);

        var stranger = Guid.NewGuid();
        var handler = Handler().AnswersJson($"api/teams/{team.Id}/users/{stranger}", UserJson);

        await IntegrationPlayerExtensions.AddUserToTeamAsync(stranger, team.Id, Client(handler), Db, Ct);

        Assert.Equal($"api/teams/{team.Id}/users/{stranger}", Assert.Single(handler.Paths));
    }

    /// <remarks>
    /// The commented half of the asymmetry: <c>// User might already exist, continue</c>. A conflict from
    /// <c>CreateUser</c> is the ordinary case for anybody who has used Player before, so swallowing it is
    /// deliberate and right.
    /// </remarks>
    [Fact]
    public async Task AddUserToTeam_SwallowsAFailureCreatingTheUser()
    {
        var msel = await SeedMsel();
        var team = BlueprintAppFactory.Team(msel.Id);
        await Seed(team);

        var actor = await Actor().SeedAsync();

        var handler = Handler()
            .Answers("api/users", HttpStatusCode.Conflict)
            .AnswersJson($"api/teams/{team.Id}/users/{actor.Id}", UserJson);

        await IntegrationPlayerExtensions.AddUserToTeamAsync(actor.Id, team.Id, Client(handler), Db, Ct);

        Assert.Equal(["api/users", $"api/teams/{team.Id}/users/{actor.Id}"], handler.Paths);
    }

    /// <remarks>
    /// <para>
    /// The uncommented half. Adding a user to a team they are already on is exactly as ordinary as creating a
    /// user who already exists, and it is one line below the <c>catch</c> that would have absorbed it - so
    /// <c>JoinService</c>'s queue item fails, and its own handler logs a join failure for a user who is
    /// already where they needed to be.
    /// </para>
    /// <para>
    /// Widening the <c>try</c> to cover both calls turns this test red and leaves
    /// <see cref="AddUserToTeam_SwallowsAFailureCreatingTheUser"/> green.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AddUserToTeam_DoesNotSwallowAFailureAddingToTheTeam()
    {
        var msel = await SeedMsel();
        var team = BlueprintAppFactory.Team(msel.Id);
        await Seed(team);

        var actor = await Actor().SeedAsync();

        var handler = Handler()
            .AnswersJson("api/users", UserJson, HttpStatusCode.Created)
            .Answers($"api/teams/{team.Id}/users/{actor.Id}", HttpStatusCode.Conflict);

        await Assert.ThrowsAnyAsync<ApiException>(() =>
            IntegrationPlayerExtensions.AddUserToTeamAsync(actor.Id, team.Id, Client(handler), Db, Ct));
    }

    // ---------------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------------

    private static readonly Guid ViewId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid CiteEvaluationId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid GalleryExhibitId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000002");
    private static readonly Guid SteamfitterScenarioId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000003");

    private const string AllPlaceholders =
        "http://application.example/" +
        "?msel={blueprintMselId}" +
        "&cite={citeEvaluationId}" +
        "&exhibit={galleryExhibitId}" +
        "&scenario={steamfitterScenarioId}" +
        "&view={playerViewId}" +
        "&playerUrl={playerUrl}" +
        "&citeUrl={citeUrl}" +
        "&galleryUrl={galleryUrl}" +
        "&steamfitterUrl={steamfitterUrl}" +
        "&blueprintUrl={blueprintUrl}";

    // Bodies the generated client will accept for each call. Hand-written rather than serialized from the
    // client's own DTOs because what is being pinned is the wire format, not a round trip through it.
    private static readonly string ViewJson = $$"""{"id":"{{ViewId}}","name":"view","status":"Active"}""";
    private static readonly string TeamJson = $$"""{"id":"{{ViewId}}","name":"team"}""";
    private static readonly string UserJson = $$"""{"id":"{{ViewId}}","name":"user"}""";
    private static readonly string ApplicationJson =
        $$"""{"id":"{{ViewId}}","name":"application","viewId":"{{ViewId}}"}""";
    private static readonly string InstanceJson =
        $$"""{"id":"{{ViewId}}","teamId":"{{ViewId}}","applicationId":"{{ViewId}}"}""";

    private static TestHttpHandler Handler() => new();

    private static PlayerApiClient Client(TestHttpHandler handler) =>
        new(ApiClientsExtensions.GetHttpClient(handler.AsFactory(), "http://player.example/", null));

    /// <summary>The three <c>ClientSettings</c> urls the placeholder substitution reads, and the two more.</summary>
    private static ClientOptions Options() => new()
    {
        PlayerUiUrl = "http://player.ui",
        CiteUiUrl = "http://cite.ui",
        GalleryUiUrl = "http://gallery.ui",
        SteamfitterUiUrl = "http://steamfitter.ui",
        BlueprintUiUrl = "http://blueprint.ui"
    };

    /// <summary>
    /// A MSEL already pushed as far as having a Player view, since every method here but the pull needs one.
    /// </summary>
    private async Task<MselEntity> SeedMsel(bool withIntegrationIds = false)
    {
        var msel = BlueprintAppFactory.Msel();
        msel.PlayerViewId = ViewId;

        if (withIntegrationIds)
        {
            msel.CiteEvaluationId = CiteEvaluationId;
            msel.GalleryExhibitId = GalleryExhibitId;
            msel.SteamfitterScenarioId = SteamfitterScenarioId;
        }

        await Seed(msel);

        return msel;
    }

    /// <summary>
    /// The MSEL as the push loads it: teams with their users, and the applications. Read through a fresh
    /// context so the eager loads are what the methods actually see rather than whatever the test's own
    /// change tracker happens to hold.
    /// </summary>
    /// <remarks>
    /// <c>AsSplitQuery</c> is not an optimization here, it is required. Blueprint configures
    /// <c>MultipleCollectionIncludeWarning</c> to <em>throw</em>
    /// (<c>DatabaseExtensions.UseConfiguredDatabase</c>, reproduced by <see cref="BlueprintContextFactory"/>),
    /// so any query including two collections fails without it. <c>IntegrationService.cs:286</c> does the
    /// same thing for the same reason, over eleven includes.
    /// </remarks>
    private async Task<MselEntity> Reload(Guid mselId)
    {
        await using var context = NewContext();

        return await context.Msels
            .Include(m => m.Teams)
                .ThenInclude(t => t.TeamUsers)
                    .ThenInclude(tu => tu.User)
            .Include(m => m.PlayerApplications)
            .AsSplitQuery()
            .FirstAsync(m => m.Id == mselId, Ct);
    }

    private async Task CreateApplications(Guid mselId, TestHttpHandler handler, int batchSize = 10) =>
        await IntegrationPlayerExtensions.CreateApplicationsAsync(
            await Reload(mselId), Client(handler), Db, batchSize, Options(), Ct);

    /// <summary>The request body as a property bag, which is how these tests read what went out.</summary>
    private static Dictionary<string, JsonElement> Body(string body) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(body);
}
