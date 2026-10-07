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
using Blueprint.Api.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Player.Api.Client;
using Xunit;

// Blueprint and IdentityModel both declare a ClientOptions, so the name is ambiguous here.
using ClientOptions = Blueprint.Api.Infrastructure.Options.ClientOptions;

namespace Blueprint.Api.Tests.Infrastructure.Extensions;

/// <summary><c>IntegrationPlayerExtensions</c> - the five calls blueprint makes to player.api when a MSEL
/// is pushed, pulled, or joined.</summary>
public class IntegrationPlayerExtensionsTests(DatabaseFixture fixture) : DatabaseTestBase(fixture)
{
    /// <summary>Starts describing an actor to seed over this test's database.</summary>
    private TestActorBuilder Actor() => new(Db, Ct);

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

    /// <summary>Pull from player swallows a refusal.</summary>
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
        var msel = TestData.Msel();
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

    /// <summary>A pushed view is always Active and always asks for an admin team.</summary>
    [Fact]
    public async Task CreateView_AlwaysAsksForAnActiveViewWithAnAdminTeam()
    {
        var msel = TestData.Msel();
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
        var msel = TestData.Msel();
        msel.PlayerViewId = Guid.Parse("22222222-2222-2222-2222-222222222222");

        var handler = Handler().AnswersJson("api/views", ViewJson, HttpStatusCode.Created);

        await IntegrationPlayerExtensions.CreateViewAsync(msel, ViewId, Client(handler), null, Ct);

        Assert.Equal(ViewId.ToString(), Body(Assert.Single(handler.Sent).Body)["id"].GetString());
    }

    /// <summary>Create view with no view id on the MSEL throws before sending even when given one.</summary>
    [Fact]
    public async Task CreateView_WithNoViewIdOnTheMsel_ThrowsBeforeSendingEvenWhenGivenOne()
    {
        var msel = TestData.Msel();

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
        var msel = TestData.Msel();
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
        var first = TestData.Team(msel.Id);
        var second = TestData.Team(msel.Id);
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
        var team = TestData.Team(msel.Id);
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
        var team = TestData.Team(msel.Id);
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
        var first = TestData.Team(msel.Id);
        var second = TestData.Team(msel.Id);
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

    /// <summary>The method uses the eager-loaded teams and never reads its <c>BlueprintContext</c>.</summary>
    [Fact]
    public async Task CreateTeams_NeverReadsTheDatabase()
    {
        var msel = await SeedMsel();
        await Seed(TestData.Team(msel.Id));

        var handler = Handler().AnswersJson($"api/views/{ViewId}/teams", TeamJson, HttpStatusCode.Created);

        await IntegrationPlayerExtensions.CreateTeamsAsync(
            await Reload(msel.Id), Client(handler), blueprintContext: null, [], Ct);

        Assert.Single(handler.Sent);
    }

    [Fact]
    public async Task CreateTeams_DoesNotSwallowARefusal()
    {
        var msel = await SeedMsel();
        await Seed(TestData.Team(msel.Id));

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
        var application = TestData.PlayerApplication(msel.Id, name: "Console", icon: "star.png");
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
        await Seed(TestData.PlayerApplication(msel.Id));

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
        await Seed(TestData.PlayerApplication(msel.Id, url: AllPlaceholders));

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
        await Seed(TestData.PlayerApplication(msel.Id, icon: "icons/{playerViewId}/{blueprintMselId}.png"));

        var handler = Handler().AnswersJson($"api/views/{ViewId}/applications", ApplicationJson, HttpStatusCode.Created);

        await CreateApplications(msel.Id, handler);

        Assert.Equal(
            $"icons/{ViewId}/{msel.Id}.png",
            Body(Assert.Single(handler.Sent).Body)["icon"].GetString());
    }

    /// <summary>A placeholder for an integration the MSEL does not have is replaced by the empty
    /// string.</summary>
    [Fact]
    public async Task CreateApplications_ForAnIntegrationTheMselDoesNotHave_SubstitutesAnEmptyString()
    {
        var msel = await SeedMsel();

        Assert.Null(msel.CiteEvaluationId);

        await Seed(TestData.PlayerApplication(
            msel.Id, url: "http://application.example/?cite={citeEvaluationId}"));

        var handler = Handler().AnswersJson($"api/views/{ViewId}/applications", ApplicationJson, HttpStatusCode.Created);

        await CreateApplications(msel.Id, handler);

        Assert.Equal(
            "http://application.example/?cite=",
            Body(Assert.Single(handler.Sent).Body)["url"].GetString());
    }

    /// <summary>Create applications for a url that does not resolve sends no url at all.</summary>
    [Theory]
    [InlineData("not a url")]
    [InlineData("/relative/path")]
    [InlineData("htp://typo.example/")]
    [InlineData("javascript:alert(1)")]
    public async Task CreateApplications_ForAUrlThatDoesNotResolve_SendsNoUrlAtAll(string url)
    {
        var msel = await SeedMsel();
        await Seed(TestData.PlayerApplication(msel.Id, url: url));

        var handler = Handler().AnswersJson($"api/views/{ViewId}/applications", ApplicationJson, HttpStatusCode.Created);

        await CreateApplications(msel.Id, handler);

        Assert.Equal(JsonValueKind.Null, Body(Assert.Single(handler.Sent).Body)["url"].ValueKind);
    }

    /// <summary>Create applications for an application with no url throws a null reference.</summary>
    [Fact]
    public async Task CreateApplications_ForAnApplicationWithNoUrl_ThrowsANullReference()
    {
        var msel = await SeedMsel();
        await Seed(TestData.PlayerApplication(msel.Id, url: null));

        var handler = Handler().AnswersJson($"api/views/{ViewId}/applications", ApplicationJson, HttpStatusCode.Created);

        await Assert.ThrowsAsync<NullReferenceException>(() => CreateApplications(msel.Id, handler));
    }

    [Fact]
    public async Task CreateApplications_ForAnApplicationWithNoIcon_SendsNoIcon()
    {
        var msel = await SeedMsel();
        await Seed(TestData.PlayerApplication(msel.Id, icon: null));

        var handler = Handler().AnswersJson($"api/views/{ViewId}/applications", ApplicationJson, HttpStatusCode.Created);

        await CreateApplications(msel.Id, handler);

        Assert.Equal(JsonValueKind.Null, Body(Assert.Single(handler.Sent).Body)["icon"].ValueKind);
    }

    [Fact]
    public async Task CreateApplications_CreatesAnInstancePerTeamCarryingItsDisplayOrder()
    {
        var msel = await SeedMsel();
        var first = TestData.Team(msel.Id);
        var second = TestData.Team(msel.Id);
        await Seed(first, second);

        var application = TestData.PlayerApplication(msel.Id);
        await Seed(application);
        await Seed(
            TestData.PlayerApplicationTeam(application.Id, first.Id, displayOrder: 3),
            TestData.PlayerApplicationTeam(application.Id, second.Id, displayOrder: 7));

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

    /// <summary>The instance names the application id Player answered with.</summary>
    [Fact]
    public async Task CreateApplications_UsesTheApplicationIdPlayerAnsweredWith()
    {
        var msel = await SeedMsel();
        var team = TestData.Team(msel.Id);
        await Seed(team);

        var application = TestData.PlayerApplication(msel.Id);
        await Seed(application);
        await Seed(TestData.PlayerApplicationTeam(application.Id, team.Id));

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

    /// <summary>Create applications ignores the batch size and starts every application at once.</summary>
    [Fact]
    public async Task CreateApplications_IgnoresTheBatchSizeAndStartsEveryApplicationAtOnce()
    {
        var msel = await SeedMsel();
        var team = TestData.Team(msel.Id);
        await Seed(team);

        for (var i = 0; i < 3; i++)
        {
            var application = TestData.PlayerApplication(msel.Id, name: $"application-{i}");
            await Seed(application);
            await Seed(TestData.PlayerApplicationTeam(application.Id, team.Id, displayOrder: i));
        }

        var handler = new SiblingApiHandler().HoldsUntil(3)
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
        var team = TestData.Team(msel.Id);
        await Seed(team);

        var actor = await Actor().WithName("Grace").SeedAsync();

        var handler = Handler()
            .AnswersJson("api/users", UserJson, HttpStatusCode.Created)
            .AnswersJson($"api/teams/{team.Id}/users/{actor.Id}", UserJson);

        await IntegrationPlayerExtensions.AddUserToTeamAsync(actor.Id, team.Id, Client(handler), Db, Ct);

        Assert.Equal(["api/users", $"api/teams/{team.Id}/users/{actor.Id}"], handler.Paths);
        Assert.Equal("Grace", Body(handler.Sent[0].Body)["name"].GetString());
    }

    /// <summary>A user blueprint does not know is added to the team without being created.</summary>
    [Fact]
    public async Task AddUserToTeam_ForSomeoneBlueprintDoesNotKnow_AddsThemWithoutCreatingThem()
    {
        var msel = await SeedMsel();
        var team = TestData.Team(msel.Id);
        await Seed(team);

        var stranger = Guid.NewGuid();
        var handler = Handler().AnswersJson($"api/teams/{team.Id}/users/{stranger}", UserJson);

        await IntegrationPlayerExtensions.AddUserToTeamAsync(stranger, team.Id, Client(handler), Db, Ct);

        Assert.Equal($"api/teams/{team.Id}/users/{stranger}", Assert.Single(handler.Paths));
    }

    /// <summary>A failure creating the user is swallowed.</summary>
    [Fact]
    public async Task AddUserToTeam_SwallowsAFailureCreatingTheUser()
    {
        var msel = await SeedMsel();
        var team = TestData.Team(msel.Id);
        await Seed(team);

        var actor = await Actor().SeedAsync();

        var handler = Handler()
            .Answers("api/users", HttpStatusCode.Conflict)
            .AnswersJson($"api/teams/{team.Id}/users/{actor.Id}", UserJson);

        await IntegrationPlayerExtensions.AddUserToTeamAsync(actor.Id, team.Id, Client(handler), Db, Ct);

        Assert.Equal(["api/users", $"api/teams/{team.Id}/users/{actor.Id}"], handler.Paths);
    }

    /// <summary>Add user to team does not swallow a failure adding to the team.</summary>
    [Fact]
    public async Task AddUserToTeam_DoesNotSwallowAFailureAddingToTheTeam()
    {
        var msel = await SeedMsel();
        var team = TestData.Team(msel.Id);
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

    private static SiblingApiHandler Handler() => new();

    private static PlayerApiClient Client(SiblingApiHandler handler) =>
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
        var msel = TestData.Msel();
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

    private async Task CreateApplications(Guid mselId, SiblingApiHandler handler, int batchSize = 10) =>
        await IntegrationPlayerExtensions.CreateApplicationsAsync(
            await Reload(mselId), Client(handler), Db, batchSize, Options(), Ct);

    /// <summary>The request body as a property bag, which is how these tests read what went out.</summary>
    private static Dictionary<string, JsonElement> Body(string body) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(body);
}
