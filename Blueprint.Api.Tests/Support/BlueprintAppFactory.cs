// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

// The per-class factory variant (PerClassAppFactory.template.cs). App-specific: the four sibling API
// clients and IXApiService substituted; Development rather than Production; the shared test scheme
// registered as Bearer; the hub recorder renewed per test; outside a request the context falls back to the
// host's own database, also after start-up, because CompositionTests resolve every service off
// Factory.Services.

using System;
using System.Net.Http;
using System.Threading.Tasks;
using Blueprint.Api.Data;
using Blueprint.Api.Hubs;
using Blueprint.Api.Services;
using Cite.Api.Client;
using Gallery.Api.Client;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NSubstitute.ClearExtensions;
using Player.Api.Client;
using Steamfitter.Api.Client;

namespace Blueprint.Api.Tests.Support;

/// <summary>
/// Hosts <c>Blueprint.Api</c> in process, once per test class, with the sibling-API clients substituted.
/// Everything between the HTTP request and the four sibling Crucible APIs is the production wiring: routing,
/// model binding, the MVC-wide authorization filter, the claims transformer, the controllers, the services,
/// the static requirement helpers, AutoMapper, MediatR, the entity-event interceptor and EF Core against real
/// PostgreSQL.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why per class.</b> Tests arrange return values on and assert <c>Received()</c> against
/// <see cref="Cite"/>, <see cref="Gallery"/>, <see cref="PlayerApi"/>, <see cref="Steamfitter"/> and
/// <see cref="XApi"/>. NSubstitute keeps its assertion state per thread, so substitutes shared by test classes
/// running in parallel would lose calls; one class's tests run one after another, so a class fixture is safe.
/// <see cref="ResetForTest"/>, called by <see cref="ApiTestBase"/> before each test, clears them and renews
/// <see cref="Hub"/>. Each test class declares
/// <c>XTests(DatabaseFixture fixture, BlueprintAppFactory factory) : ApiTestBase(fixture, factory), IClassFixture&lt;BlueprintAppFactory&gt;</c>.
/// </para>
/// <para>
/// <b>The substitutes.</b> The production registrations of the four clients build their own HttpClient from
/// <c>ClientSettings:*ApiUrl</c> and a bearer token lifted off the current request. <see cref="XApi"/> is
/// substituted for observability: <c>XApiOptions:Enabled</c> ships false, so the real service returns early
/// from every method, and the substitute answers <c>IsConfigured()</c> with false as the real one does.
/// <c>XApiEnabledFactory</c> puts the real service back.
/// </para>
/// <para>
/// <b>The database gate (step 1B).</b> <c>Program.Main</c> has no switch that skips
/// <c>InitializeDatabase</c>, so the host gets a throwaway database of its own, cloned from the migrated
/// template, for <c>Main</c> to migrate (a no-op) and seed, on a background thread, while tests run. Requests
/// reach the database of the test that sent them through <see cref="TestDatabaseScope"/>. A context resolved
/// outside any request (that seeding, and the composition tests, which resolve every service off
/// <c>Factory.Services</c>) gets the host's database rather than the shared registration's exception, and
/// publishes its entity events to the host session's own substituted mediator, so the seed never reaches a
/// test's hub recording. No request ever reaches the host's database, which <c>HttpHarnessTests</c> pins.
/// </para>
/// <para>
/// <b>Development</b>, not Production: <c>JsonExceptionFilter</c> answers controller exceptions in either,
/// and in Development it puts the exception message in the 500 body, which is what a test reads.
/// </para>
/// <para>
/// <b>The Bearer scheme.</b> <c>MainHub</c> carries <c>[Authorize(AuthenticationSchemes = "Bearer")]</c>,
/// so the shared <see cref="TestAuthHandler"/> is registered under the name <c>Bearer</c>, after the JWT
/// registration is unpicked, because <c>AddScheme</c> throws on a duplicate. Its <c>iss</c> claim and the
/// user read from <c>Authorization: Bearer &lt;id&gt;</c> are switched on in <see cref="TestConfiguration"/>.
/// </para>
/// <para>
/// Removed: every <see cref="IHostedService"/>. <c>XApiBackgroundService</c>, <c>IntegrationService</c>,
/// <c>JoinService</c> and <c>AddApplicationService</c> each start a loop over a blocking queue and dial the
/// identity provider and the sibling APIs. The singleton queues behind them stay real, so a request-path test
/// asserts that work was enqueued; the workers are driven directly by <c>IntegrationServiceHarness</c> and
/// <c>QueueWorkerHarness</c>.
/// </para>
/// <para>
/// One limitation of a host per class: <c>CurrentHttpContext</c> holds a process-wide static
/// <c>IHttpContextAccessor</c> set by <c>app.UseHttpContext()</c>, so the last host to start wins it.
/// <c>ICompetencyFrameworkImportProgressService</c> is a singleton by design, shared by the tests of a class;
/// key those tests on an id no other test uses.
/// </para>
/// </remarks>
public class BlueprintAppFactory(DatabaseFixture database)
    : WebApplicationFactory<Startup>, ITestHttpHost, IAsyncLifetime
{
    private ITestDatabaseSession<BlueprintContext> _hostSession;

    /// <summary>CITE, as <c>CiteService</c> and the MSEL integration paths consume it.</summary>
    public ICiteApiClient Cite { get; } = Substitute.For<ICiteApiClient>();

    /// <summary>Gallery, reached by the MSEL integration paths.</summary>
    public IGalleryApiClient Gallery { get; } = Substitute.For<IGalleryApiClient>();

    /// <summary>
    /// Player. The production registration returns <c>null</c> when there is no <c>HttpContext</c>.
    /// </summary>
    public IPlayerApiClient PlayerApi { get; } = Substitute.For<IPlayerApiClient>();

    /// <summary>Steamfitter, reached by the MSEL integration paths.</summary>
    public ISteamfitterApiClient Steamfitter { get; } = Substitute.For<ISteamfitterApiClient>();

    /// <summary>The xAPI statement seam. <c>IsConfigured()</c> answers false unless a test says otherwise.</summary>
    public IXApiService XApi { get; } = Substitute.For<IXApiService>();

    /// <summary>
    /// What the application broadcast through <c>MainHub</c> during the current test, per audience.
    /// Blueprint's notifications flow SaveChanges, EntityEventInterceptor, MediatR, an <c>EventHandlers</c>
    /// handler, <c>IHubContext&lt;MainHub&gt;</c>, so this records the far end of the real pipeline. Renewed
    /// by <see cref="ResetForTest"/>.
    /// </summary>
    public HubRecorder<MainHub> Hub { get; private set; } = new();

    /// <summary>Answers every request the application makes through <see cref="IHttpClientFactory"/>.</summary>
    public StubHttpMessageHandler OutboundHttp { get; } = new();

    /// <summary>The database the host itself owns, for the harness's own tests.</summary>
    internal string HostDatabaseName => _hostSession.DatabaseName;

    /// <remarks>
    /// xUnit initializes a class fixture before constructing the test class, and <c>WebApplicationFactory</c>
    /// builds the host on first use, so the database exists by the time <see cref="ConfigureWebHost"/> reads
    /// its connection string.
    /// </remarks>
    public async ValueTask InitializeAsync() => _hostSession = await database.BeginSessionAsync();

    /// <summary>
    /// Clears the substitutes and starts a new hub recording. Called before each test: one class's tests
    /// share the host, and run one after another.
    /// </summary>
    internal void ResetForTest()
    {
        Hub = new();
        Cite.ClearSubstitute();
        Gallery.ClearSubstitute();
        PlayerApi.ClearSubstitute();
        Steamfitter.ClearSubstitute();
        XApi.ClearSubstitute();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.UseSetting("Database:Provider", "PostgreSQL");
        builder.UseSetting("ConnectionStrings:PostgreSQL", _hostSession.ConnectionString);

        builder.ConfigureAppConfiguration(configuration =>
            configuration.AddInMemoryCollection(TestConfiguration.Values));

        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IHostedService>();

            // Every registration that configures AuthenticationOptions is one IConfigureOptions, so dropping
            // them drops Startup's default scheme and JWT bearer's claim on the name "Bearer".
            services.RemoveAll<IConfigureOptions<AuthenticationOptions>>();
            services
                .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(JwtBearerDefaults.AuthenticationScheme, null);

            // Outside a request, the host's own database, over the host session's own services: Main's
            // InitializeDatabase seeds it on a background thread, and its entity events then go to that
            // session's substituted mediator rather than to the real handlers and a test's hub recording.
            // Also after start-up, unlike the template: CompositionTests resolve every service off
            // Factory.Services, outside any request.
            TestDatabaseScope.ReplaceRegistration<BlueprintContext>(services, () => _hostSession);

            services.Replace(ServiceDescriptor.Singleton(Cite));
            services.Replace(ServiceDescriptor.Singleton(Gallery));
            services.Replace(ServiceDescriptor.Singleton(PlayerApi));
            services.Replace(ServiceDescriptor.Singleton(Steamfitter));
            services.Replace(ServiceDescriptor.Singleton(XApi));

            // Transient over the current recording, so ResetForTest takes effect for every handler resolved
            // afterwards. SignalR's own registration is an open generic, which a closed one outranks.
            services.AddTransient<IHubContext<MainHub>>(_ => Hub);

            services.RemoveAll<IHttpClientFactory>();
            services.AddSingleton<IHttpClientFactory>(new StubHttpClientFactory(OutboundHttp));
        });
    }

    public override async ValueTask DisposeAsync()
    {
        // The host first: it holds pooled connections to the database the session is about to drop.
        await base.DisposeAsync();

        if (_hostSession is not null)
        {
            await _hostSession.DisposeAsync();
        }
    }
}
