// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Blueprint.Api.Data.Enumerations;
using Blueprint.Api.Hubs;
using Blueprint.Api.Tests.Support;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Blueprint.Api.Tests.Hubs;

/// <summary><c>MainHub</c> as the application mounts it: the path clients dial, the endpoints <c>MapHub</c>
/// produced, what they demand of a caller, and one invocation over a real <c>HubConnection</c>.</summary>
public class MainHubConnectionTests(DatabaseFixture fixture, BlueprintAppFactory factory)
    : ApiTestBase(fixture, factory), IClassFixture<BlueprintAppFactory>
{
    /// <summary>Where <c>Startup.cs:350</c> maps the hub, and what blueprint.ui dials.</summary>
    private const string HubPath = "/hubs/main";

    // ---------------------------------------------------------------------------------------------
    // Where the hub is, and what it demands
    // ---------------------------------------------------------------------------------------------

    /// <remarks>
    /// The hub is mapped at the site root, not under <c>PathBase</c> or the <c>api</c> prefix every
    /// controller sits behind, so the path is spelt here and in blueprint.ui with nothing keeping the
    /// two copies honest. <see cref="Negotiate_WhenAuthenticated_HandsOutAConnection"/> negotiates at the
    /// path; this negotiates under the prefix.
    /// </remarks>
    [Fact]
    public async Task TheHub_IsNotMappedUnderTheApiPrefix()
    {
        var response = await Negotiate(ClientFor(Guid.NewGuid(), null), $"/api{HubPath}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Negotiate_WithoutCredentials_Is401()
    {
        var response = await Negotiate(Client(), HubPath);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Negotiate_WhenAuthenticated_HandsOutAConnection()
    {
        var response = await Negotiate(ClientFor(Guid.NewGuid(), null), HubPath);

        response.EnsureSuccessStatusCode();
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        Assert.False(string.IsNullOrEmpty(body.RootElement.GetProperty("connectionId").GetString()));
    }

    /// <remarks>
    /// <c>MapHub</c> produces several endpoints for one hub - the negotiate, and one per transport -
    /// and <c>[Authorize]</c> on the class is metadata each of them carries separately. Asserting on
    /// the attribute would prove nothing about what the application mounted; this asserts that every
    /// endpoint the hub actually has requires a caller.
    /// </remarks>
    [Fact]
    public void EveryEndpointOfTheHub_RequiresAuthorization()
    {
        var endpoints = HubEndpoints();

        Assert.NotEmpty(endpoints);
        Assert.All(
            endpoints,
            endpoint => Assert.NotEmpty(endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>()));
    }

    /// <summary>The application maps one hub.</summary>
    [Fact]
    public void TheApplication_MapsNoHubButThisOne()
    {
        Assert.All(
            HubEndpoints(),
            endpoint => Assert.Equal(
                typeof(MainHub), endpoint.Metadata.GetMetadata<HubMetadata>().HubType));
    }

    /// <remarks>
    /// Presence is kept in a singleton rather than in SignalR's own group state, so the hub's answer to
    /// "who else is on this MSEL" is only right while there is one tracker per application. Nothing in the
    /// hub would notice a scoped registration; every presence list would just be empty. (Nothing notices a
    /// second *host* either - blueprint keeps no cross-instance presence at all, so a scaled-out
    /// deployment shows each replica's own users.)
    /// </remarks>
    [Fact]
    public void ThePresenceTracker_IsOneInstanceForTheWholeApplication()
    {
        using var first = Factory.Services.CreateScope();
        using var second = Factory.Services.CreateScope();

        Assert.Same(
            first.ServiceProvider.GetRequiredService<IPresenceTracker>(),
            second.ServiceProvider.GetRequiredService<IPresenceTracker>());
    }

    // ---------------------------------------------------------------------------------------------
    // An invocation over the wire
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Presence over real connections: a member already on the MSEL hears the second arrive, is in the
    /// second's answer, and hears the list without them when their connection closes without leaving, the
    /// disconnect path.
    /// </summary>
    [Fact]
    public async Task A_member_on_the_msel_sees_another_arrive_and_depart_on_disconnect()
    {
        var msel = TestData.Msel();
        await Seed(msel);
        var bob = await Actor().WithName("Bob").WithSystemPermissions(SystemPermission.ViewMsels).SeedAsync();
        var alice = await Actor().WithName("Alice").WithSystemPermissions(SystemPermission.ViewMsels).SeedAsync();
        var afterAlice = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        var afterDeparture = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var bobs = Connect(bob);
        bobs.On<JsonElement>(MainHubMethods.PresenceChanged, x =>
        {
            if (Users(x).Count == 2)
                afterAlice.TrySetResult(x);
            else if (afterAlice.Task.IsCompleted)
                afterDeparture.TrySetResult(x);
        });
        await bobs.StartAsync(Ct);
        var first = await bobs.InvokeAsync<JsonElement>(nameof(MainHub.SelectMsel), new[] { msel.Id }, Ct);
        Assert.Equal(["Bob"], Users(first));

        var alices = Connect(alice);
        await alices.StartAsync(Ct);
        var present = await alices.InvokeAsync<JsonElement>(nameof(MainHub.SelectMsel), new[] { msel.Id }, Ct);

        Assert.Equal(["Alice", "Bob"], Users(present));
        Assert.Equal(["Alice", "Bob"], Users(await afterAlice.Task.WaitAsync(Waits.Budget, Ct)));

        await alices.DisposeAsync();

        var departed = await afterDeparture.Task.WaitAsync(Waits.Budget, Ct);
        Assert.Equal(["Bob"], Users(departed));
        Assert.True(departed.GetProperty("version").GetInt64() > present.GetProperty("version").GetInt64());
    }

    /// <summary>
    /// Over a real connection, a caller holding no role gets nothing for a MSEL they can't reach, which the
    /// earlier <c>GetPresence</c> answered with the MSEL's occupants.
    /// </summary>
    [Fact]
    public async Task SelectMsel_OverARealConnection_AnswersAStrangerNothing()
    {
        var msel = TestData.Msel();
        await Seed(msel);
        var stranger = await Actor().WithName("a-stranger").SeedAsync();
        await using var connection = Connect(stranger);
        await connection.StartAsync(Ct);

        var answer = await connection.InvokeAsync<JsonElement?>(nameof(MainHub.SelectMsel), new[] { msel.Id }, Ct);

        Assert.True(answer is null || answer.Value.ValueKind == JsonValueKind.Null);
    }

    private Task<HttpResponseMessage> Negotiate(HttpClient client, string path) =>
        client.PostAsync($"{path}/negotiate?negotiateVersion=1", content: null, Ct);

    /// <summary>The endpoints <c>MapHub</c> produced, whichever hub each belongs to.</summary>
    private IReadOnlyList<Endpoint> HubEndpoints() =>
        Factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .Where(x => x.Metadata.GetMetadata<HubMetadata>() is not null)
            .ToList();

    /// <summary>
    /// A connection to the in-memory host over a WebSocket, authenticated as <paramref name="actor"/> and
    /// routed to this test's database.
    /// </summary>
    /// <remarks>
    /// WebSockets, with the negotiate skipped, because the hub invocation then runs inside the upgrade
    /// request, which carries the actor's and the session's headers, so <c>TestDatabaseScope</c> resolves this
    /// test's database for anything the hub reads. Under long polling an invocation arrives outside any
    /// request, where <c>BlueprintAppFactory</c>'s registration falls back to the host's own database.
    /// </remarks>
    private HubConnection Connect(TestActor actor)
    {
        var session = SessionHeader();

        return new HubConnectionBuilder()
            .WithUrl(
                new Uri(Factory.Server.BaseAddress, HubPath.TrimStart('/')),
                options =>
                {
                    options.Transports = HttpTransportType.WebSockets;
                    options.SkipNegotiation = true;
                    options.WebSocketFactory = async (context, ct) =>
                    {
                        var client = Factory.Server.CreateWebSocketClient();
                        client.ConfigureRequest = request =>
                        {
                            request.Headers[TestAuthHandler.UserHeader] = actor.Id.ToString();
                            request.Headers[TestAuthHandler.NameHeader] = actor.Name;
                            request.Headers[TestDatabaseScope.HeaderName] = session;
                        };

                        return await client.ConnectAsync(context.Uri, ct);
                    };
                })
            .Build();
    }

    /// <remarks>
    /// <see cref="ApiTestBase"/> keeps the session id private and stamps it on the clients it hands
    /// out, so reading it back off one is how a connection built here joins the same database.
    /// </remarks>
    private string SessionHeader() =>
        Client().DefaultRequestHeaders.GetValues(TestDatabaseScope.HeaderName).First();

    /// <summary>The names in a presence state's <c>users</c>, in the order the hub sent them.</summary>
    private static List<string> Users(JsonElement state) =>
        [.. state.GetProperty("users").EnumerateArray().Select(x => x.GetProperty("name").GetString())];
}
