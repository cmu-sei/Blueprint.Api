// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
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

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();

        // The cache is a host-wide singleton and the host serves the whole class.
        Cache.Connections.Clear();
    }

    private HubCache Cache => Factory.Services.GetRequiredService<HubCache>();

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
    /// Presence is kept in a singleton dictionary rather than in SignalR's own group state, so the
    /// hub's answer to "who else is on this MSEL" is only right while there is one instance of the
    /// cache per application. Nothing in the hub would notice a scoped registration; every presence
    /// list would just be empty. (Nothing notices a second *host* either - blueprint keeps no
    /// cross-instance presence at all, so a scaled-out deployment shows each replica's own users.)
    /// </remarks>
    [Fact]
    public void TheHubCache_IsOneInstanceForTheWholeApplication()
    {
        using var first = Factory.Services.CreateScope();
        using var second = Factory.Services.CreateScope();

        Assert.Same(
            first.ServiceProvider.GetRequiredService<HubCache>(),
            second.ServiceProvider.GetRequiredService<HubCache>());
    }

    // ---------------------------------------------------------------------------------------------
    // An invocation over the wire
    // ---------------------------------------------------------------------------------------------

    /// <summary>Over a real connection, a caller holding no role is answered the occupants of any MSEL.</summary>
    [Fact]
    public async Task GetPresence_OverARealConnection_AnswersAStrangerTheOccupantsOfAnyMsel()
    {
        var mselId = Guid.NewGuid();
        Cache.Connections["somebody-elses-connection"] = new CachedConnection
        {
            ConnectionId = "somebody-elses-connection",
            MselId = mselId.ToString(),
            UserId = Guid.NewGuid().ToString(),
            UserName = "working-on-it"
        };
        var stranger = await Actor().WithName("a-stranger").SeedAsync();
        await using var connection = Connect(stranger);
        await connection.StartAsync(Ct);

        var presence = await connection.InvokeAsync<List<JsonElement>>(
            nameof(MainHub.GetPresence), mselId.ToString(), Ct);

        Assert.Equal("working-on-it", Name(Assert.Single(presence)));
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

    /// <remarks>
    /// The presence payload is an anonymous object, so the name of its <c>name</c> property on the
    /// wire is whatever SignalR's payload serializer made of it. Matched case-insensitively rather
    /// than asserted, because what this test is about is the value.
    /// </remarks>
    private static string Name(JsonElement entry) =>
        entry.EnumerateObject()
            .First(x => string.Equals(x.Name, "name", StringComparison.OrdinalIgnoreCase))
            .Value.GetString();
}
