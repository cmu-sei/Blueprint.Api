// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute.ClearExtensions;

namespace Blueprint.Api.Tests.Infrastructure;

/// <summary>
/// Base class for tests that drive the application over HTTP: the real routes, the real middleware, the
/// real MVC-wide authorization filter, the real claims transformer, the real controllers and services,
/// over a database no other test can see.
/// </summary>
/// <remarks>
/// <para>
/// One host serves the test class (<see cref="BlueprintAppFactory"/>) and each test owns one database
/// (<see cref="DatabaseTestBase.Session"/>). The two are joined by a session id this class registers with
/// <see cref="TestDatabaseScope"/> and puts on every request its clients send.
/// </para>
/// <para>
/// A request runs in its own scope with its own <c>BlueprintContext</c>, so what a test reads through
/// <see cref="DatabaseTestBase.Db"/> after acting comes from a change tracker that never saw the write.
/// Re-read through <see cref="DatabaseTestBase.NewContext"/> when asserting on what was stored.
/// </para>
/// <para>
/// Clients are per-actor here, unlike the other Crucible suites' single fixed user. Blueprint decides
/// authorization twice - a coarse <c>SystemPermission</c> in the controller, then an MSEL role read from
/// the database in the service - so the interesting tests are the ones where two callers with different
/// seeded rows send the same request. See <see cref="TestActorBuilder"/>.
/// </para>
/// <para>
/// The database fixture arrives from <c>AssemblyFixtures.cs</c> and the factory from the derived class's
/// <c>IClassFixture&lt;BlueprintAppFactory&gt;</c>. Derived classes forward both:
/// <c>MyTests(DatabaseFixture fixture, BlueprintAppFactory factory) : ApiTestBase(fixture, factory)</c>.
/// </para>
/// </remarks>
public abstract class ApiTestBase(DatabaseFixture fixture, BlueprintAppFactory factory)
    : DatabaseTestBase(fixture)
{
    private readonly Guid _sessionId = Guid.NewGuid();
    private readonly List<HttpClient> _clients = [];
    private readonly Dictionary<Guid, HttpClient> _byUser = [];

    protected BlueprintAppFactory Factory { get; } = factory;

    /// <summary>
    /// What the application broadcast over SignalR during this test. Cleared before each test, because
    /// the recorder lives on the host and the host serves the whole class.
    /// </summary>
    protected HubRecorder Hub => Factory.Hub;

    /// <summary>
    /// A client carrying no identity, whose requests to an <c>/api/</c> route are answered with 401.
    /// Still routed to this test's database, so a test can tell a 401 from a routing failure.
    /// </summary>
    protected HttpClient AnonymousClient { get; private set; }

    /// <summary>
    /// The options the application serializes its responses with, taken from the running host rather than
    /// restated here.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Restating them means maintaining a second copy: <c>Startup</c> adds a <c>JsonStringEnumConverter</c>
    /// and sets <c>ReferenceHandler.IgnoreCycles</c>, so a status goes out as <c>"Approved"</c> rather than
    /// <c>20</c>, and a test deserializing with plain case-insensitive options fails on it for a reason
    /// that has nothing to do with what it was asserting.
    /// </para>
    /// <para>
    /// Note what this deliberately does not do: because these are the application's own options, a test
    /// using them follows the application if the wire format changes. Nothing here would notice the
    /// converter being removed, which would break the checked-in <c>blueprint.ui</c> client. That belongs
    /// in an assertion against the raw JSON - see
    /// <c>OrganizationEndpointTests.Get_SerializesPropertyNamesInCamelCase</c> - and in the contract
    /// snapshots.
    /// </para>
    /// </remarks>
    protected JsonSerializerOptions JsonOptions { get; private set; }

    /// <summary>
    /// A client whose requests authenticate as <paramref name="actor"/>. Repeated calls for one actor
    /// return the same client.
    /// </summary>
    protected HttpClient Client(TestActor actor)
    {
        ArgumentNullException.ThrowIfNull(actor);

        return Client(actor.Id, actor.Name);
    }

    /// <summary>
    /// A client whose requests authenticate as <paramref name="userId"/>, whether or not a user row
    /// exists - which is how a test reaches <c>UserClaimsService.ValidateUser</c>'s auto-provisioning
    /// path.
    /// </summary>
    protected HttpClient Client(Guid userId, string name = null)
    {
        if (_byUser.TryGetValue(userId, out var existing))
        {
            return existing;
        }

        var client = Track(Factory.CreateClientFor(userId, name));
        _byUser[userId] = client;

        return client;
    }

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();

        // Before any client is used: a request that arrives before its session is registered fails
        // naming the header it could not route.
        TestDatabaseScope.Register(_sessionId, Session);

        AnonymousClient = Track(Factory.CreateClient());

        // After the client, because resolving from Factory.Services is what builds the host.
        JsonOptions = Factory.Services
            .GetRequiredService<IOptions<Microsoft.AspNetCore.Mvc.JsonOptions>>()
            .Value.JsonSerializerOptions;

        // The host - and so everything on it - is shared by every test in the class.
        Factory.Hub.Clear();
        Factory.Cite.ClearSubstitute();
        Factory.Gallery.ClearSubstitute();
        Factory.PlayerApi.ClearSubstitute();
        Factory.Steamfitter.ClearSubstitute();
    }

    public override async ValueTask DisposeAsync()
    {
        // Released first: a request that outlives its test then fails naming the header it could not
        // route, rather than reaching a database being dropped underneath it.
        TestDatabaseScope.Release(_sessionId);

        foreach (var client in _clients)
        {
            client.Dispose();
        }

        await base.DisposeAsync();
    }

    private HttpClient Track(HttpClient client)
    {
        client.DefaultRequestHeaders.Add(TestDatabaseScope.HeaderName, _sessionId.ToString());
        _clients.Add(client);

        return client;
    }
}
