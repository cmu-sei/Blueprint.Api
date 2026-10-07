// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

// App-specific beyond the template: the per-class factory reset, the hub recording, the host's JSON options,
// and a client that carries an email claim.

using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Blueprint.Api.Data;
using Blueprint.Api.Hubs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Blueprint.Api.Tests.Support;

/// <summary>
/// Base class for tests that drive the application over HTTP: the real routes, the real middleware, the
/// real MVC-wide authorization filter, the real claims transformer, the real controllers and services, over
/// a database no other test can see.
/// </summary>
/// <remarks>
/// <para>
/// Derived classes forward both fixtures and take the per-class factory:
/// <c>MyTests(DatabaseFixture fixture, BlueprintAppFactory factory) : ApiTestBase(fixture, factory), IClassFixture&lt;BlueprintAppFactory&gt;</c>.
/// </para>
/// <para>
/// Blueprint decides authorization twice: a coarse <c>SystemPermission</c> in the controller, then an MSEL
/// role read from the database in the service. So the interesting tests are the ones where two callers with
/// different seeded rows send the same request; see <see cref="TestActorBuilder"/>.
/// </para>
/// </remarks>
public abstract class ApiTestBase(DatabaseFixture fixture, BlueprintAppFactory factory)
    : ApiTestBase<BlueprintContext>(fixture, factory)
{
    private readonly List<(Guid SessionId, HttpClient Client)> _emailClients = [];

    protected DatabaseFixture Fixture { get; } = fixture;

    protected BlueprintAppFactory Factory { get; } = factory;

    /// <summary>
    /// An actor holding every system permission, for the tests that are about what an endpoint does rather
    /// than who may call it. Seeded before each test.
    /// </summary>
    protected TestActor Root { get; private set; }

    /// <summary>A client that acts as <see cref="Root"/>.</summary>
    protected HttpClient RootClient => Client(Root);

    /// <summary>What the application broadcast through <c>MainHub</c> during this test.</summary>
    protected HubRecorder<MainHub> Hub => Factory.Hub;

    /// <summary>
    /// The options the application serializes its responses with, taken from the running host: <c>Startup</c>
    /// adds a <c>JsonStringEnumConverter</c>, the integer-as-string converters and
    /// <c>ReferenceHandler.IgnoreCycles</c>. A test asserting on the wire format itself reads the raw JSON.
    /// </summary>
    protected JsonSerializerOptions JsonOptions { get; private set; }

    /// <summary>Starts describing an actor to seed: <c>await Actor().WithSystemPermissions(...).SeedAsync()</c>.</summary>
    protected TestActorBuilder Actor() => new(Db, Ct);

    /// <summary>A client that acts as <paramref name="actor"/>, cached per actor.</summary>
    protected HttpClient Client(TestActor actor)
    {
        ArgumentNullException.ThrowIfNull(actor);

        return ClientFor(actor.Id, actor.Name);
    }

    /// <summary>
    /// A client that acts as <paramref name="actor"/> and also carries an <c>email</c> claim. Not cached: an
    /// invitation restricted to a domain is matched against whatever the token carries, so one actor may
    /// arrive with two addresses in one test. Only <c>MselService</c>'s join and launch paths read the claim.
    /// </summary>
    protected HttpClient ClientWithEmail(TestActor actor, string email)
    {
        ArgumentNullException.ThrowIfNull(actor);

        // A route of its own to this test's database, so the client is independent of the cached ones.
        var sessionId = Guid.NewGuid();
        TestDatabaseScope.Register(sessionId, Session);

        var client = Factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestDatabaseScope.HeaderName, sessionId.ToString());
        client.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, actor.Id.ToString());
        client.DefaultRequestHeaders.Add(TestAuthHandler.NameHeader, actor.Name);
        client.DefaultRequestHeaders.Add(TestAuthHandler.EmailHeader, email);
        _emailClients.Add((sessionId, client));

        return client;
    }

    public override async ValueTask InitializeAsync()
    {
        Factory.ResetForTest();

        await base.InitializeAsync();

        Root = await Actor().WithName("Root").WithAllSystemPermissions().SeedAsync();

        JsonOptions = Factory.Services
            .GetRequiredService<IOptions<Microsoft.AspNetCore.Mvc.JsonOptions>>()
            .Value.JsonSerializerOptions;
    }

    public override async ValueTask DisposeAsync()
    {
        foreach (var (sessionId, client) in _emailClients)
        {
            TestDatabaseScope.Release(sessionId);
            client.Dispose();
        }

        await base.DisposeAsync();
    }
}
