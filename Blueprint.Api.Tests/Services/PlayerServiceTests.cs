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
using Blueprint.Api.Tests.Support;
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

namespace Blueprint.Api.Tests.Services;

/// <summary><c>PlayerService</c> - the request-path half of blueprint's Player integration, and <c>POST
/// api/playerApplications/push</c>, the endpoint that hands one application to
/// <c>AddApplicationService</c>.</summary>
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

    /// <summary>Any signed-in caller is answered the application templates.</summary>
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
        var response = await Client().GetAsync(Templates, Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>An unreachable Player is answered 200 with an empty list.</summary>
    [Fact]
    public async Task ApplicationTemplates_WhenPlayerCannotBeReached_IsAnEmptyList()
    {
        Factory.PlayerApi.GetApplicationTemplatesAsync(Arg.Any<CancellationToken>())
            .ThrowsAsync(new PlayerIsDown());

        var response = await (await AnyCaller()).GetAsync(Templates, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("[]", await response.Content.ReadAsStringAsync(Ct));
    }

    /// <summary>An answer that is an array rather than a list is answered as an empty list.</summary>
    [Fact]
    public async Task ApplicationTemplates_WhenPlayerAnswersWithAnArrayRatherThanAList_IsAnEmptyList()
    {
        Factory.PlayerApi.GetApplicationTemplatesAsync(Arg.Any<CancellationToken>())
            .Returns(new[] { new ApplicationTemplate { Name = "unreachable" } });

        var response = await (await AnyCaller()).GetAsync(Templates, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("[]", await response.Content.ReadAsStringAsync(Ct));
    }

    /// <summary>The templates request forwards the request's cancellation token.</summary>
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
        var team = TestData.Team(msel.Id);
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

    /// <summary>The queued application carries no id; Player's answer supplies it.</summary>
    [Fact]
    public async Task Push_QueuesAnApplicationWithNoIdOfItsOwn()
    {
        var msel = await Deployed();
        var actor = await Actor().OnMsel(msel, Data.Enumerations.MselRole.Owner).SeedAsync();

        var body = Application(msel.Id);
        await Client(actor).PostAsJsonAsync(Push, body, JsonOptions, Ct);

        Assert.Equal(Guid.Empty, Queued(body.Name).Application.Id);
    }

    /// <summary>Push always asks for display order two.</summary>
    [Fact]
    public async Task Push_AlwaysAsksForDisplayOrderTwo()
    {
        var msel = await Deployed();
        await Seed(
            TestData.PlayerApplication(msel.Id),
            TestData.PlayerApplication(msel.Id),
            TestData.PlayerApplication(msel.Id));
        var actor = await Actor().OnMsel(msel, Data.Enumerations.MselRole.Owner).SeedAsync();

        var body = Application(msel.Id);
        await Client(actor).PostAsJsonAsync(Push, body, JsonOptions, Ct);

        Assert.Equal(2, Queued(body.Name).DisplayOrder);
    }

    /// <summary>The display order sent ignores the positions the team already has.</summary>
    [Fact]
    public async Task Push_IgnoresTheDisplayOrdersTheTeamAlreadyHas()
    {
        var msel = await Deployed();
        var team = TestData.Team(msel.Id);
        await Seed(team);

        var first = TestData.PlayerApplication(msel.Id);
        var second = TestData.PlayerApplication(msel.Id);
        await Seed(first, second);
        await Seed(
            TestData.PlayerApplicationTeam(first.Id, team.Id, 1),
            TestData.PlayerApplicationTeam(second.Id, team.Id, 2));

        var actor = await Actor().OnMsel(msel, Data.Enumerations.MselRole.Owner).OnTeam(team).SeedAsync();

        var body = Application(msel.Id);
        await Client(actor).PostAsJsonAsync(Push, body, JsonOptions, Ct);

        Assert.Equal(2, Queued(body.Name).DisplayOrder);
    }

    /// <summary>Push writes no team row for the application.</summary>
    [Fact]
    public async Task Push_WritesNoTeamRowForTheApplication()
    {
        var msel = await Deployed();
        var team = TestData.Team(msel.Id);
        await Seed(team);
        var actor = await Actor().OnMsel(msel, Data.Enumerations.MselRole.Owner).OnTeam(team).SeedAsync();

        var body = Application(msel.Id);
        var response = await Client(actor).PostAsJsonAsync(Push, body, JsonOptions, Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        await using var context = NewContext();

        Assert.Empty(await context.PlayerApplicationTeams.ToListAsync(Ct));
    }

    /// <summary>A caller on no team of the MSEL queues the application for the all-zeros team id.</summary>
    [Fact]
    public async Task Push_ForACallerOnNoTeamOfTheMsel_QueuesTheAllZerosTeamId()
    {
        var msel = await Deployed();
        await Seed(TestData.Team(msel.Id));
        var actor = await Actor().OnMsel(msel, Data.Enumerations.MselRole.Owner).SeedAsync();

        var body = Application(msel.Id);
        var response = await Client(actor).PostAsJsonAsync(Push, body, JsonOptions, Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(Guid.Empty, Queued(body.Name).TeamId);
    }

    /// <summary>Push for a caller on two teams of the MSEL is answered with a 500.</summary>
    [Fact]
    public async Task Push_ForACallerOnTwoTeamsOfTheMsel_Is500()
    {
        var msel = await Deployed();
        var first = TestData.Team(msel.Id);
        var second = TestData.Team(msel.Id);
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

    /// <summary>Push for a MSEL that was never deployed is answered with a 500 and keeps the row.</summary>
    [Fact]
    public async Task Push_ForAMselThatWasNeverDeployed_Is500_AndKeepsTheRow()
    {
        var msel = TestData.Msel();
        await Seed(msel);
        var actor = await Actor().OnMsel(msel, Data.Enumerations.MselRole.Owner).SeedAsync();

        var body = Application(msel.Id);
        var response = await Client(actor).PostAsJsonAsync(Push, body, JsonOptions, Ct);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        await AssertStoredButNotQueued(msel.Id, body.Name);
    }

    /// <summary>Push with a url that is not absolute is answered with a 500 and keeps the row.</summary>
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

    /// <summary>A push with no url is answered 500.</summary>
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

    /// <summary>One broadcast, from the create, to the MSEL's group and the admin group; nothing about the
    /// push.</summary>
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
            Hub.Sent(msel.Id).Select(x => x.Method).ToList());
        Assert.Equal(
            [msel.Id.ToString(), MainHub.ADMIN_DATA_GROUP],
            Hub.Recipients(MainHubMethods.PlayerApplicationCreated, msel.Id));
    }

    /// <summary>Push when the push fails has already broadcast the create.</summary>
    [Fact]
    public async Task Push_WhenThePushFails_HasAlreadyBroadcastTheCreate()
    {
        var msel = TestData.Msel();
        await Seed(msel);
        var actor = await Actor().OnMsel(msel, Data.Enumerations.MselRole.Owner).SeedAsync();

        var response = await Client(actor).PostAsJsonAsync(Push, Application(msel.Id), JsonOptions, Ct);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.NotEmpty(Hub.Of(MainHubMethods.PlayerApplicationCreated, msel.Id));
    }

    /// <summary>Only ownership of the MSEL or <c>EditMsels</c> may push; the other four roles are
    /// refused.</summary>
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
        var response = await Client().PostAsJsonAsync(
            Push, Application(Guid.NewGuid()), JsonOptions, Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        AssertNothingQueued();
    }

    /// <summary>The push answers the stored row and a location pointing at
    /// <c>getPlayerApplication</c>.</summary>
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

    /// <summary>The request makes no call to Player; the push happens later on the worker thread.</summary>
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
        var msel = TestData.Msel();
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
