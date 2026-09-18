// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using Blueprint.Api.Data.Models;
using Blueprint.Api.Hubs;
using Blueprint.Api.Services;
using Blueprint.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Player.Api.Client;
using Xunit;
using Xunit.Sdk;

// Blueprint's own view model and Player's generated client both declare an application type, and this
// file reads one and writes the other.
using SystemPermission = Blueprint.Api.Data.Enumerations.SystemPermission;

namespace Blueprint.Api.Tests;

/// <summary>
/// <c>PlayerService</c> - the request-path half of blueprint's Player integration, and
/// <c>POST api/playerApplications/push</c>, the endpoint that hands one application to
/// <c>AddApplicationService</c>.
/// </summary>
/// <remarks>
/// <para>
/// Three methods. Two are reference-data reads of the same shape as <c>CiteService</c>'s - call the
/// client inside a <c>try</c>, cast to <c>List&lt;T&gt;</c>, return an empty list from an empty
/// <c>catch</c> - and only <c>GetApplicationTemplatesAsync</c> is covered here;
/// <c>GetMyViewsAsync</c> is already pinned by <c>MselServiceInvitationTests</c>, which drives it
/// through <c>JoinMselByInvitationAsync</c>, its one caller. The third, <c>PushApplication</c>, is the
/// only producer of an <c>AddApplicationInformation</c> anywhere, so what it puts on the queue is the
/// whole of what <c>AddApplicationServiceTests</c> then assumes.
/// </para>
/// <para>
/// <strong>The display order is always 2.</strong> It is computed as
/// <c>msel.PlayerApplications.Count + 1</c> on a MSEL loaded by
/// <c>SingleOrDefaultAsync(m =&gt; m.Id == ...)</c> with no <c>Include</c> for that navigation - so the
/// only thing in the collection is whatever EF's change tracker has already fixed up into it, which on
/// this path is exactly one row: the application <c>CreateAsync</c> inserted and re-read a moment
/// earlier on the same request-scoped context. The MSEL's other applications are in the database and
/// not in the tracker, so they are not counted. Every application a MSEL pushes is therefore offered to
/// Player as the second one, whether the MSEL holds none or thirty.
/// See <see cref="Push_AlwaysAsksForDisplayOrderTwo"/>.
/// </para>
/// <para>
/// <strong>And it would be the wrong count even if it were loaded.</strong> The order being computed
/// is the application's position on <em>a team's</em> dashboard, which is what
/// <c>PlayerApplicationTeamEntity.DisplayOrder</c> records; counting the MSEL's applications instead
/// answers a different question, and the column that holds the real answer is never read. See
/// <see cref="Push_IgnoresTheDisplayOrdersTheTeamAlreadyHas"/>.
/// </para>
/// <para>
/// <strong>The push goes to whichever team the caller is on, and no row records it.</strong> The team
/// is found by intersecting the caller's <c>TeamUser</c> rows with the MSEL's teams, so a MSEL author
/// who is on none of its teams pushes the application to the all-zeros team id, and one who is on two
/// gets a 500 from <c>SingleOrDefaultAsync</c>. Meanwhile no <c>PlayerApplicationTeam</c> row is
/// written, although that table exists precisely to say which teams see an application - so
/// blueprint's own answer to "who can see this" and Player's disagree from the moment of the push. See
/// <see cref="Push_ForACallerOnNoTeamOfTheMsel_QueuesTheAllZerosTeamId"/>,
/// <see cref="Push_ForACallerOnTwoTeamsOfTheMsel_Is500"/> and
/// <see cref="Push_WritesNoTeamRowForTheApplication"/>.
/// </para>
/// <para>
/// <strong>A push that cannot be built leaves the row behind and tells the clients it was created.</strong>
/// <c>CreateAndPushAsync</c> calls <c>CreateAsync</c>, which saves - and a save with no ambient
/// transaction publishes its entity event there and then - before <c>PushApplication</c> runs. So the
/// three ways this method throws (an undeployed MSEL, an unusable url, a caller on two teams) each
/// answer 500 with the application already stored and already broadcast as created. See
/// <see cref="Push_ForAMselThatWasNeverDeployed_Is500_AndKeepsTheRow"/>.
/// </para>
/// <para>
/// Per this branch's rule, every test characterizes rather than fixes, and says what fixing it will do
/// to the test.
/// </para>
/// </remarks>
public class PlayerServiceTests(DatabaseFixture fixture, BlueprintAppFactory factory)
    : ApiTestBase(fixture, factory), IClassFixture<BlueprintAppFactory>
{
    private IAddApplicationQueue Queue =>
        Factory.Services.GetRequiredService<IAddApplicationQueue>();

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();

        // The queue is a host singleton and the host serves the whole class.
        while (TryTake(TimeSpan.FromMilliseconds(25), out _))
        {
        }
    }

    // ---------------------------------------------------------------------------------------------
    // GET applicationTemplates
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task ApplicationTemplates_ReturnsWhatPlayerAnswered()
    {
        Factory.PlayerApi.GetApplicationTemplatesAsync(Arg.Any<CancellationToken>()).Returns(
        [
            new ApplicationTemplate { Name = "Console" },
            new ApplicationTemplate { Name = "Wiki" }
        ]);

        var response = await (await AnyCaller()).GetAsync(Templates, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var templates = await response.Content
            .ReadFromJsonAsync<List<ApplicationTemplate>>(JsonOptions, Ct);

        Assert.Equal(["Console", "Wiki"], templates.ConvertAll(x => x.Name));
    }

    /// <remarks>
    /// Player's application templates are the list a MSEL author picks from, and this route hands them
    /// to any caller holding a token. Both <c>PlayerController</c> and <c>PlayerService</c> take the
    /// framework's <c>IAuthorizationService</c> - not blueprint's <c>IBlueprintAuthorizationService</c>,
    /// so neither could ask for a <c>SystemPermission</c> as written - and neither reads it. The same
    /// finding as all three <c>CiteController</c> routes. Requiring any permission turns this red.
    /// </remarks>
    [Fact]
    public async Task ApplicationTemplates_ForACallerWithNoPermissions_Is200()
    {
        Factory.PlayerApi.GetApplicationTemplatesAsync(Arg.Any<CancellationToken>()).Returns([]);

        var response = await Client(await Actor().SeedAsync()).GetAsync(Templates, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task ApplicationTemplates_Anonymously_Is401()
    {
        var response = await AnonymousClient.GetAsync(Templates, Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <remarks>
    /// An unreachable Player is a 200 and an empty list, exactly as an unreachable CITE is: the
    /// <c>catch</c> is empty and the service holds no logger to write to even if it wanted one.
    /// Answering <c>502</c> is what turns this red.
    /// </remarks>
    [Fact]
    public async Task ApplicationTemplates_WhenPlayerCannotBeReached_IsAnEmptyList()
    {
        Factory.PlayerApi.GetApplicationTemplatesAsync(Arg.Any<CancellationToken>())
            .ThrowsAsync(new PlayerIsDown());

        var response = await (await AnyCaller()).GetAsync(Templates, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("[]", await response.Content.ReadAsStringAsync(Ct));
    }

    /// <remarks>
    /// The fourth instance of the same cast - three in <c>CiteService</c> and this one.
    /// <c>GetApplicationTemplatesAsync</c> is declared to return <c>ICollection&lt;T&gt;</c>, so an
    /// answer that is an array rather than a <c>List</c> is an <c>InvalidCastException</c> into the
    /// empty <c>catch</c> and the caller is told Player has no templates. Latent, because today's NSwag
    /// output builds a <c>List</c>. Assigning to an <c>IEnumerable</c> local turns this red.
    /// </remarks>
    [Fact]
    public async Task ApplicationTemplates_WhenPlayerAnswersWithAnArrayRatherThanAList_IsAnEmptyList()
    {
        Factory.PlayerApi.GetApplicationTemplatesAsync(Arg.Any<CancellationToken>())
            .Returns(new[] { new ApplicationTemplate { Name = "unreachable" } });

        var response = await (await AnyCaller()).GetAsync(Templates, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("[]", await response.Content.ReadAsStringAsync(Ct));
    }

    /// <remarks>
    /// Passed here, and dropped by <c>CiteService.GetScoringModelsAsync</c> - which is how you can tell
    /// that one is an oversight rather than a decision. The token is matched on
    /// <c>CanBeCanceled</c> rather than with <c>Arg.Any</c>, because <c>CancellationToken.None</c> is a
    /// legal <c>CancellationToken</c> and an assertion that accepts it cannot tell a dropped token from
    /// a forwarded one.
    /// </remarks>
    [Fact]
    public async Task ApplicationTemplates_PassesTheRequestsCancellationToken()
    {
        Factory.PlayerApi.GetApplicationTemplatesAsync(Arg.Any<CancellationToken>()).Returns([]);

        await (await AnyCaller()).GetAsync(Templates, Ct);

        await Factory.PlayerApi.Received(1)
            .GetApplicationTemplatesAsync(Arg.Is<CancellationToken>(x => x.CanBeCanceled));
    }

    // ---------------------------------------------------------------------------------------------
    // POST playerApplications/push
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Push_QueuesTheApplicationTheCallerDescribed()
    {
        var msel = await Deployed();
        var team = BlueprintAppFactory.Team(msel.Id);
        await Seed(team);
        var actor = await Actor().OnMsel(msel, Data.Enumerations.MselRole.Owner).OnTeam(team).SeedAsync();

        var body = Application(msel.Id);
        var response = await Client(actor).PostAsJsonAsync(Push, body, JsonOptions, Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var queued = Queued(body.Name);

        Assert.Equal(body.Name, queued.Application.Name);
        Assert.Equal(new Uri(body.Url), queued.Application.Url);
        Assert.Equal(body.Icon, queued.Application.Icon);
        Assert.True(queued.Application.Embeddable);
        Assert.False(queued.Application.LoadInBackground);
        Assert.Equal(msel.PlayerViewId, queued.Application.ViewId);
        Assert.Equal(team.Id, queued.TeamId);
    }

    /// <remarks>
    /// Blueprint does not tell Player which id it used for its own row, and it is right not to: the two
    /// applications are different rows in different databases, and <c>AddApplicationService</c> reads
    /// the id out of Player's answer rather than off the queue item. The consequence is that nothing
    /// afterwards can match Player's application back to blueprint's - there is no column for it - so
    /// the only record that a push happened is that it was answered 201.
    /// </remarks>
    [Fact]
    public async Task Push_QueuesAnApplicationWithNoIdOfItsOwn()
    {
        var msel = await Deployed();
        var actor = await Actor().OnMsel(msel, Data.Enumerations.MselRole.Owner).SeedAsync();

        var body = Application(msel.Id);
        await Client(actor).PostAsJsonAsync(Push, body, JsonOptions, Ct);

        Assert.Equal(Guid.Empty, Queued(body.Name).Application.Id);
    }

    /// <remarks>
    /// <para>
    /// Three applications already on the MSEL and a fourth just created, so four rows the query could
    /// have counted - and the answer is 2. <c>PushApplication</c> loads the MSEL without
    /// <c>Include(m =&gt; m.PlayerApplications)</c>, but the navigation is not empty either: EF fixes up
    /// the one <c>PlayerApplicationEntity</c> the request-scoped context is already tracking, which is
    /// the row <c>CreateAsync</c> inserted and then re-read through <c>GetAsync</c> two lines earlier. So
    /// the count is 1, the order is 2, and the three rows that are genuinely on the MSEL - seeded here
    /// through a different context, and never tracked by this one - are invisible.
    /// </para>
    /// <para>
    /// This is worse than a stale navigation, because it is not stable: what gets counted is whatever
    /// the request happens to have loaded, so the number depends on the code path rather than on the
    /// data. Adding the <c>Include</c> turns this red and turns the answer into 5.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Push_AlwaysAsksForDisplayOrderTwo()
    {
        var msel = await Deployed();
        await Seed(
            BlueprintAppFactory.PlayerApplication(msel.Id),
            BlueprintAppFactory.PlayerApplication(msel.Id),
            BlueprintAppFactory.PlayerApplication(msel.Id));
        var actor = await Actor().OnMsel(msel, Data.Enumerations.MselRole.Owner).SeedAsync();

        var body = Application(msel.Id);
        await Client(actor).PostAsJsonAsync(Push, body, JsonOptions, Ct);

        Assert.Equal(2, Queued(body.Name).DisplayOrder);
    }

    /// <remarks>
    /// The number being sent is a position on the team's dashboard, and the team already has two
    /// applications at positions 1 and 2 - recorded where Player records them, on
    /// <c>PlayerApplicationTeamEntity.DisplayOrder</c>. Nothing reads that column, so the third
    /// application is pushed as position 2 and lands on top of one of them. Counting the team's rows
    /// rather than the MSEL's turns this red.
    /// </remarks>
    [Fact]
    public async Task Push_IgnoresTheDisplayOrdersTheTeamAlreadyHas()
    {
        var msel = await Deployed();
        var team = BlueprintAppFactory.Team(msel.Id);
        await Seed(team);

        var first = BlueprintAppFactory.PlayerApplication(msel.Id);
        var second = BlueprintAppFactory.PlayerApplication(msel.Id);
        await Seed(first, second);
        await Seed(
            BlueprintAppFactory.PlayerApplicationTeam(first.Id, team.Id, 1),
            BlueprintAppFactory.PlayerApplicationTeam(second.Id, team.Id, 2));

        var actor = await Actor().OnMsel(msel, Data.Enumerations.MselRole.Owner).OnTeam(team).SeedAsync();

        var body = Application(msel.Id);
        await Client(actor).PostAsJsonAsync(Push, body, JsonOptions, Ct);

        Assert.Equal(2, Queued(body.Name).DisplayOrder);
    }

    /// <remarks>
    /// <c>PlayerApplicationTeamEntity</c> is the table that says which teams see an application, and
    /// the push writes none - so blueprint shows the application against no team while Player shows it
    /// on the pusher's, and the ordinary way to put an application on a second team
    /// (<c>POST playerApplicationTeams</c>) has no effect on a view that has already been pushed to.
    /// Creating the join row turns this red.
    /// </remarks>
    [Fact]
    public async Task Push_WritesNoTeamRowForTheApplication()
    {
        var msel = await Deployed();
        var team = BlueprintAppFactory.Team(msel.Id);
        await Seed(team);
        var actor = await Actor().OnMsel(msel, Data.Enumerations.MselRole.Owner).OnTeam(team).SeedAsync();

        var body = Application(msel.Id);
        var response = await Client(actor).PostAsJsonAsync(Push, body, JsonOptions, Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        await using var context = NewContext();

        Assert.Empty(await context.PlayerApplicationTeams.ToListAsync(Ct));
    }

    /// <remarks>
    /// A MSEL author who is not on any of its teams - the ordinary case for a content developer - pushes
    /// the application to the all-zeros team, which <c>AddApplicationService</c> then posts as
    /// <c>player/api/teams/00000000-0000-0000-0000-000000000000/application-instances</c>. The instance
    /// fails, the failure is logged against the step that cannot fail, and the caller was told 201.
    /// Refusing the push, or falling back to every team on the MSEL, turns this red.
    /// </remarks>
    [Fact]
    public async Task Push_ForACallerOnNoTeamOfTheMsel_QueuesTheAllZerosTeamId()
    {
        var msel = await Deployed();
        await Seed(BlueprintAppFactory.Team(msel.Id));
        var actor = await Actor().OnMsel(msel, Data.Enumerations.MselRole.Owner).SeedAsync();

        var body = Application(msel.Id);
        var response = await Client(actor).PostAsJsonAsync(Push, body, JsonOptions, Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(Guid.Empty, Queued(body.Name).TeamId);
    }

    /// <remarks>
    /// Being on two of a MSEL's teams is legal - nothing anywhere forbids it, and an exercise designer
    /// sitting on both a blue team and the control team is the obvious case - and it makes this endpoint
    /// a 500 from <c>SingleOrDefaultAsync</c>, with the application row already stored. The other team
    /// membership is not the caller's mistake and there is no way for them to work around it.
    /// <c>FirstOrDefaultAsync</c> turns this red, and pushing to every team the caller is on is
    /// probably the real fix.
    /// </remarks>
    [Fact]
    public async Task Push_ForACallerOnTwoTeamsOfTheMsel_Is500()
    {
        var msel = await Deployed();
        var first = BlueprintAppFactory.Team(msel.Id);
        var second = BlueprintAppFactory.Team(msel.Id);
        await Seed(first, second);
        var actor = await Actor()
            .OnMsel(msel, Data.Enumerations.MselRole.Owner)
            .OnTeam(first)
            .OnTeam(second)
            .SeedAsync();

        var body = Application(msel.Id);
        var response = await Client(actor).PostAsJsonAsync(Push, body, JsonOptions, Ct);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        await AssertStoredButNotQueued(msel.Id, body.Name);
    }

    /// <remarks>
    /// <c>ViewId = (Guid)msel.PlayerViewId</c> with no guard, so pushing an application before the MSEL
    /// has been pushed is <c>"Nullable object must have a value."</c> as a 500 - and the row is already
    /// stored and already broadcast, because <c>CreateAsync</c> saved before <c>PushApplication</c> ran.
    /// The author is left with an application blueprint lists and Player has never heard of, and the
    /// only way to fix it is to notice and delete it. Guarding the cast, or wrapping the create and the
    /// push in one transaction, turns this red.
    /// </remarks>
    [Fact]
    public async Task Push_ForAMselThatWasNeverDeployed_Is500_AndKeepsTheRow()
    {
        var msel = BlueprintAppFactory.Msel();
        await Seed(msel);
        var actor = await Actor().OnMsel(msel, Data.Enumerations.MselRole.Owner).SeedAsync();

        var body = Application(msel.Id);
        var response = await Client(actor).PostAsJsonAsync(Push, body, JsonOptions, Ct);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        await AssertStoredButNotQueued(msel.Id, body.Name);
    }

    /// <remarks>
    /// The <c>Url</c> column is a free-text string on both the entity and the view model, and nothing
    /// validates it until <c>new Uri(application.Url)</c> - so a relative url, or a typo, is a 500 with
    /// the row kept. <c>POST playerApplications</c>, the create that does not push, stores the same
    /// value happily, which is how one gets into the database in the first place. A
    /// <c>[Url]</c> attribute on the view model turns this red and turns it into a 400.
    /// </remarks>
    [Fact]
    public async Task Push_WithAUrlThatIsNotAbsolute_Is500_AndKeepsTheRow()
    {
        var msel = await Deployed();
        var actor = await Actor().OnMsel(msel, Data.Enumerations.MselRole.Owner).SeedAsync();

        var body = Application(msel.Id) with { Url = "console.example/index.html" };
        var response = await Client(actor).PostAsJsonAsync(Push, body, JsonOptions, Ct);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        await AssertStoredButNotQueued(msel.Id, body.Name);
    }

    /// <remarks>
    /// The same line, one step earlier: no url at all is an <c>ArgumentNullException</c> rather than a
    /// <c>UriFormatException</c>, and the route has no required-field validation that would have caught
    /// it. Note the contrast with <c>IntegrationPlayerExtensions</c>, which runs the MSEL's applications
    /// through <c>Uri.TryCreate</c> and sends a null url rather than throwing - so the two ways an
    /// application reaches Player disagree about a url they cannot parse.
    /// </remarks>
    [Fact]
    public async Task Push_WithNoUrlAtAll_Is500()
    {
        var msel = await Deployed();
        var actor = await Actor().OnMsel(msel, Data.Enumerations.MselRole.Owner).SeedAsync();

        var body = Application(msel.Id) with { Url = null };
        var response = await Client(actor).PostAsJsonAsync(Push, body, JsonOptions, Ct);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        await AssertStoredButNotQueued(msel.Id, body.Name);
    }

    /// <remarks>
    /// One broadcast, from the create, carrying the stored row to the MSEL's group and to the admin
    /// group. Nothing says the application was pushed, is being pushed, or has arrived - the queue item
    /// is taken on another thread and <c>AddApplicationService</c> sends nothing either - so blueprint's
    /// UI shows a pushed application identically to an unpushed one.
    /// </remarks>
    [Fact]
    public async Task Push_BroadcastsThatTheApplicationWasCreated_AndNothingAboutThePush()
    {
        var msel = await Deployed();
        var actor = await Actor().OnMsel(msel, Data.Enumerations.MselRole.Owner).SeedAsync();

        var body = Application(msel.Id);
        await Client(actor).PostAsJsonAsync(Push, body, JsonOptions, Ct);

        Queued(body.Name);

        Assert.Equal(
            [MainHubMethods.PlayerApplicationCreated, MainHubMethods.PlayerApplicationCreated],
            Hub.Sends.Select(x => x.Method).ToList());
        Assert.Equal(
            [msel.Id.ToString(), MainHub.ADMIN_DATA_GROUP],
            Hub.Recipients(MainHubMethods.PlayerApplicationCreated));
    }

    /// <remarks>
    /// The create's event is published inside its own <c>SaveChangesAsync</c> - there is no ambient
    /// transaction on this path, so <c>EntityEventInterceptor</c> publishes on <c>SavedChanges</c> - and
    /// <c>PushApplication</c> throws afterwards. So every connected client is told an application was
    /// created on a request that answered 500. Wrapping the create and the push together turns this red.
    /// </remarks>
    [Fact]
    public async Task Push_WhenThePushFails_HasAlreadyBroadcastTheCreate()
    {
        var msel = BlueprintAppFactory.Msel();
        await Seed(msel);
        var actor = await Actor().OnMsel(msel, Data.Enumerations.MselRole.Owner).SeedAsync();

        var response = await Client(actor).PostAsJsonAsync(Push, Application(msel.Id), JsonOptions, Ct);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.NotEmpty(Hub.Of(MainHubMethods.PlayerApplicationCreated));
    }

    /// <remarks>
    /// Ownership of the MSEL, or <c>EditMsels</c>, and nothing else - the authorization is
    /// <c>CreateAsync</c>'s, because <c>CreateAndPushAsync</c> adds none of its own and says so in a
    /// comment. So the four roles that are not ownership are all refused, as they are on every other
    /// create in the application.
    /// </remarks>
    [Theory]
    [InlineData(Data.Enumerations.MselRole.Editor)]
    [InlineData(Data.Enumerations.MselRole.Approver)]
    [InlineData(Data.Enumerations.MselRole.MoveEditor)]
    [InlineData(Data.Enumerations.MselRole.Viewer)]
    [InlineData(Data.Enumerations.MselRole.Evaluator)]
    public async Task Push_WithARoleThatIsNotOwnership_Is403(Data.Enumerations.MselRole role)
    {
        var msel = await Deployed();
        var actor = await Actor().OnMsel(msel, role).SeedAsync();

        var body = Application(msel.Id);
        var response = await Client(actor).PostAsJsonAsync(Push, body, JsonOptions, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        await using var context = NewContext();

        Assert.Empty(await context.PlayerApplications.Where(x => x.MselId == msel.Id).ToListAsync(Ct));
        AssertNothingQueued();
    }

    [Fact]
    public async Task Push_WithEditMselsAndNoRoleOnTheMsel_QueuesIt()
    {
        var msel = await Deployed();
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();

        var body = Application(msel.Id);
        var response = await Client(actor).PostAsJsonAsync(Push, body, JsonOptions, Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(msel.PlayerViewId, Queued(body.Name).Application.ViewId);
    }

    [Fact]
    public async Task Push_Anonymously_Is401()
    {
        var response = await AnonymousClient.PostAsJsonAsync(
            Push, Application(Guid.NewGuid()), JsonOptions, Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        AssertNothingQueued();
    }

    /// <remarks>
    /// The create's own <c>Location</c> header, lowercased by <c>RouteOptions.LowercaseUrls</c>, and
    /// pointing at <c>getPlayerApplication</c> - the row, not the push. There is nothing to poll for the
    /// push's outcome, which is the finding this assertion carries: the 201 is the last thing the
    /// caller ever learns.
    /// </remarks>
    [Fact]
    public async Task Push_AnswersTheStoredRowAndItsLocation()
    {
        var msel = await Deployed();
        var actor = await Actor().OnMsel(msel, Data.Enumerations.MselRole.Owner).SeedAsync();

        var body = Application(msel.Id);
        var response = await Client(actor).PostAsJsonAsync(Push, body, JsonOptions, Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var created = await response.Content
            .ReadFromJsonAsync<ViewModels.PlayerApplication>(JsonOptions, Ct);

        Assert.Equal(body.Name, created.Name);
        Assert.Equal(msel.Id, created.MselId);
        Assert.EndsWith(
            $"/api/playerapplications/{created.Id}", response.Headers.Location.ToString());

        Queued(body.Name);
    }

    /// <remarks>
    /// Nothing on this path uses the injected <c>IPlayerApiClient</c>: the request's whole job is to
    /// write a row and put an item on a queue, and the HTTP to Player happens later on
    /// <c>AddApplicationService</c>'s thread. Worth pinning because it is what makes the 201 meaningless
    /// - the caller is told the application was created and pushed, and at that moment Player has not
    /// been asked anything.
    /// </remarks>
    [Fact]
    public async Task Push_AsksPlayerNothing()
    {
        var msel = await Deployed();
        var actor = await Actor().OnMsel(msel, Data.Enumerations.MselRole.Owner).SeedAsync();

        var body = Application(msel.Id);
        await Client(actor).PostAsJsonAsync(Push, body, JsonOptions, Ct);

        Queued(body.Name);

        Assert.Empty(Factory.PlayerApi.ReceivedCalls());
    }

    // ---------------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------------

    private const string Templates = "/api/applicationTemplates";
    private const string Push = "/api/playerApplications/push";

    /// <summary>
    /// A signed-in caller holding nothing, which is all <c>GET applicationTemplates</c> asks for.
    /// </summary>
    private async Task<HttpClient> AnyCaller() => Client(await Actor().SeedAsync());

    /// <summary>A MSEL that has been pushed to Player, so it has a view to add applications to.</summary>
    private async Task<MselEntity> Deployed()
    {
        var msel = BlueprintAppFactory.Msel();
        msel.PlayerViewId = Guid.NewGuid();
        await Seed(msel);

        return msel;
    }

    /// <summary>
    /// The request body. The name is unique per call because the queue is a host singleton shared by
    /// every test in this class, and the name is the only thing that identifies an item on it.
    /// </summary>
    private static Body Application(Guid mselId) =>
        new(mselId, $"application-{Guid.NewGuid()}", "http://console.example/");

    /// <remarks>
    /// <c>ViewModels.Base</c> declares <c>DateCreated</c> and <c>CreatedBy</c> non-nullable, so a body
    /// record must leave them out rather than declare them nullable - sending an explicit null is a 400
    /// that never reaches the controller.
    /// </remarks>
    private sealed record Body(Guid MselId, string Name, string Url)
    {
        public string Icon { get; init; } = "star.png";

        public bool? Embeddable { get; init; } = true;

        public bool? LoadInBackground { get; init; } = false;
    }

    /// <summary>
    /// The queue item for <paramref name="name"/>, failing the test if the queue empties first.
    /// </summary>
    /// <remarks>
    /// Items for other applications are discarded rather than put back: the queue is drained before
    /// each test, so one being there at all means an earlier test in this class left it.
    /// </remarks>
    private AddApplicationInformation Queued(string name)
    {
        while (TryTake(TimeSpan.FromSeconds(5), out var taken))
        {
            if (taken.Application?.Name == name)
            {
                return taken;
            }
        }

        throw new XunitException(
            $"Nothing was enqueued for application '{name}'. The endpoint answered without handing it " +
            "to IAddApplicationQueue, so it would never reach Player.");
    }

    private void AssertNothingQueued() => Assert.False(
        TryTake(TimeSpan.FromMilliseconds(150), out var taken),
        $"Application '{taken?.Application?.Name}' was enqueued, and nothing should have been.");

    /// <summary>
    /// The shape every failure of <c>PushApplication</c> leaves behind: the row saved by
    /// <c>CreateAsync</c>, and nothing on the queue.
    /// </summary>
    private async Task AssertStoredButNotQueued(Guid mselId, string name)
    {
        await using var context = NewContext();

        var stored = await context.PlayerApplications
            .Where(x => x.MselId == mselId)
            .ToListAsync(Ct);

        Assert.Equal([name], stored.ConvertAll(x => x.Name));
        AssertNothingQueued();
    }

    /// <remarks>
    /// <c>IAddApplicationQueue.Take</c> wraps a <c>BlockingCollection</c>, so it blocks until something
    /// arrives and the only way out of an empty queue is a cancelled token. The patience is paid in
    /// full whenever the answer is "nothing", which is why the negative assertion above uses a short
    /// one.
    /// </remarks>
    private bool TryTake(TimeSpan patience, out AddApplicationInformation taken)
    {
        using var timeout = new CancellationTokenSource(patience);

        try
        {
            taken = Queue.Take(timeout.Token);

            return true;
        }
        catch (OperationCanceledException)
        {
            taken = null;

            return false;
        }
    }

    /// <summary>A failure from the Player client, standing in for anything that can go wrong.</summary>
    private sealed class PlayerIsDown : System.Exception
    {
        public PlayerIsDown()
            : base("player.api is not answering")
        {
        }
    }
}
