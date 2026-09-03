// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Net.Http;
using System.Threading.Tasks;
using Blueprint.Api.Data;
using Blueprint.Api.Data.Enumerations;
using Blueprint.Api.Data.Models;
using Blueprint.Api.Hubs;
using Cite.Api.Client;
using Gallery.Api.Client;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NSubstitute;
using Player.Api.Client;
using Steamfitter.Api.Client;
using Xunit;

namespace Blueprint.Api.Tests.Infrastructure;

/// <summary>
/// Hosts the real <see cref="Startup"/> in-process. Everything between the HTTP request and the four
/// sibling Crucible APIs is the production wiring: routing, model binding, the MVC-wide authorization
/// filter, the claims transformer, the controllers, the services, the static requirement helpers,
/// AutoMapper, MediatR, the entity-event interceptor and EF Core against real PostgreSQL. Only the edges
/// are replaced.
///
/// Substituted, and why:
///   ICiteApiClient / IGalleryApiClient / IPlayerApiClient / ISteamfitterApiClient - the four APIs
///     blueprint integrates with, all reached over HTTP. Substituting them is not optional even for a
///     test that has nothing to do with integration: the production registrations build their own
///     HttpClient from ClientSettings:*ApiUrl and a bearer token lifted off the current request, and two
///     of the three dereference HttpContext.Request without a null check.
///   IHubContext&lt;MainHub&gt; - the notification seam, replaced with <see cref="HubRecorder"/> rather
///     than removed, because it is the only place a broadcast is observable without a live connection.
///
/// Removed: every IHostedService. XApiBackgroundService, IntegrationService, JoinService and
/// AddApplicationService each start a `while(true)` over a blocking queue in StartAsync, and three of
/// them dial the identity provider for a token and then Player, Gallery, CITE and Steamfitter. The
/// singleton queues behind them (IIntegrationQueue, IJoinQueue, IAddApplicationQueue) are left real:
/// they are the seam that lets a request-path test assert work was *enqueued* without a worker draining
/// it. IIntegrationService stays resolvable, because it is registered separately from its hosted
/// registration.
///
/// Replaced: the BlueprintContext registration, so each request reaches the database of the test that
/// made it. See <see cref="TestDatabaseScope"/>.
/// </summary>
/// <remarks>
/// <para>
/// This is a class fixture, not an assembly fixture, and deliberately so. Tests both arrange return
/// values on and assert <c>Received()</c> against the NSubstitute doubles above; NSubstitute keeps its
/// assertion state per thread, so one set of substitutes shared by every test class would lose and
/// cross-attribute calls once classes ran in parallel. The cost is about a second of host startup per
/// endpoint test class. Classes that share a host still have to reset between tests, which
/// <see cref="ApiTestBase"/> does.
/// </para>
/// <para>
/// The host gets a throwaway database of its own, separate from any test's. <c>Program.Main</c> matches
/// neither convention <c>HostFactoryResolver</c> looks for - <c>CreateWebHostBuilder</c> returns an
/// <c>IHostBuilder</c>, not an <c>IWebHostBuilder</c> - so <c>WebApplicationFactory</c> falls back to
/// invoking <c>Main</c> on a background thread, and <c>Main</c> calls <c>InitializeDatabase</c> on the
/// built host. In practice the build is interrupted before that line runs, so the seed data in
/// <c>seed-data.json</c> is never loaded; the host database exists so that nothing depends on which way
/// that goes. A clone of the already-migrated template makes any migrate a no-op, and pointing the host
/// at the template itself would hold a connection there and break every later clone.
/// <c>DatabaseHarnessTests</c> pins the property that matters: no request ever reaches this database.
/// </para>
/// <para>
/// Two known limitations, both of them consequences of one host per test class rather than one per test.
/// <c>CurrentHttpContext</c> holds a process-wide static <c>IHttpContextAccessor</c>, set by
/// <c>app.UseHttpContext()</c>, so with several hosts running the last one to start wins it - keep
/// anything that reads <c>CurrentHttpContext.Current</c> in a single test class.
/// <c>ICompetencyFrameworkImportProgressService</c> is a singleton by design, because the import POST and
/// the progress GET are separate requests, so it is shared by every test in a host - key those tests on
/// an id no other test uses.
/// </para>
/// </remarks>
public class BlueprintAppFactory(DatabaseFixture database) : WebApplicationFactory<Startup>, IAsyncLifetime
{
    /// <summary>
    /// The scopes every authenticated test request carries, also written into configuration so the
    /// principal <see cref="TestAuthHandler"/> mints and the filter <c>Startup</c> builds out of
    /// <c>Authorization:AuthorizationScope</c> cannot drift apart.
    /// </summary>
    /// <remarks>
    /// These are the six <c>appsettings.json</c> ships. Stated here rather than inherited because the
    /// filter requires <em>every</em> one of them: a scope added to configuration and not to the handler
    /// would 403 the entire API surface, with nothing to say why.
    /// </remarks>
    private static readonly string[] Scopes =
        ["blueprint", "player", "player-vm", "cite", "gallery", "steamfitter"];

    private TestDatabaseSession _hostSession;

    /// <summary>CITE, as <c>CiteService</c> and the MSEL integration paths consume it.</summary>
    public ICiteApiClient Cite { get; } = Substitute.For<ICiteApiClient>();

    /// <summary>Gallery, reached by the MSEL integration paths.</summary>
    public IGalleryApiClient Gallery { get; } = Substitute.For<IGalleryApiClient>();

    /// <summary>
    /// Player. Note the production registration returns <c>null</c> when there is no
    /// <c>HttpContext</c>, so anything resolving it off a request would get a null reference rather than
    /// a client; substituting it is what makes the request path testable at all.
    /// </summary>
    public IPlayerApiClient PlayerApi { get; } = Substitute.For<IPlayerApiClient>();

    /// <summary>Steamfitter, reached by the MSEL integration paths.</summary>
    public ISteamfitterApiClient Steamfitter { get; } = Substitute.For<ISteamfitterApiClient>();

    /// <summary>
    /// Everything the application broadcast over SignalR. Blueprint's notifications all flow
    /// SaveChanges → EntityEventInterceptor → MediatR → an <c>EventHandlers</c> handler →
    /// <c>IHubContext&lt;MainHub&gt;</c>, so this records the far end of the real pipeline.
    /// </summary>
    public HubRecorder Hub { get; } = new();

    /// <summary>
    /// The database the host itself owns. Exposed so the harness's own tests can prove no request ever
    /// reads or writes it.
    /// </summary>
    internal string HostDatabaseName => _hostSession.DatabaseName;

    /// <remarks>
    /// xUnit initializes a class fixture before constructing the test class, and
    /// <c>WebApplicationFactory</c> does not build the host until it is first used, so the database
    /// exists by the time <see cref="ConfigureWebHost"/> reads its connection string.
    /// </remarks>
    public async ValueTask InitializeAsync() => _hostSession = await database.BeginSessionAsync();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Development, because JsonExceptionFilter answers controller exceptions either way - it is an
        // IExceptionFilter, so the developer exception page only ever sees middleware-level failures -
        // and in Development it puts the real message in `title` and the stack trace in `detail` on a
        // 500. That is what a failing test needs to read.
        builder.UseEnvironment("Development");

        // WebApplicationFactory sets the content root to Blueprint.Api, so appsettings.json supplies
        // everything else Startup needs: PathBase, the Authorization URLs it parses into Uris,
        // ClientSettings, XApiOptions:Enabled=false, ResourceOwnerAuthorization, HtmlSanitizer, and
        // OpenTelemetry off. Only these four are the harness's business.
        builder.UseSetting("Database:Provider", "PostgreSQL");
        builder.UseSetting("ConnectionStrings:PostgreSQL", _hostSession.ConnectionString);
        builder.UseSetting("Authorization:AuthorizationScope", string.Join(' ', Scopes));
        // UserClaimsService caches a user's claims in the host-wide singleton IMemoryCache, keyed by user
        // id. One host serves a whole test class, so caching would let one test's permissions answer
        // another test's request.
        builder.UseSetting("ClaimsTransformation:EnableCaching", "false");

        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IHostedService>();

            services.Replace(ServiceDescriptor.Singleton(Cite));
            services.Replace(ServiceDescriptor.Singleton(Gallery));
            services.Replace(ServiceDescriptor.Singleton(PlayerApi));
            services.Replace(ServiceDescriptor.Singleton(Steamfitter));
            services.Replace(ServiceDescriptor.Singleton<IHubContext<MainHub>>(Hub));

            AddPerTestDatabase(services);
            AddTestAuthentication(services);
        });
    }

    /// <summary>
    /// Points <see cref="BlueprintContext"/> at the database of the test making the request.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both descriptors <c>AddEventPublishingDbContextFactory</c> registers have to go: a pooled
    /// <c>IDbContextFactory&lt;BlueprintContext&gt;</c> and the scoped context that comes out of it. The
    /// factory bakes one connection string into one pooled set of options when the container is built, so
    /// leaving it would let a stray resolution reach the host's database.
    /// </para>
    /// <para>
    /// The request scope is passed as the context's <c>ServiceProvider</c>, which is what the
    /// application's own registration does, so <c>PublishEventsAsync</c> resolves the request's real
    /// mediator and entity events reach the real handlers - and so <see cref="Hub"/>.
    /// </para>
    /// </remarks>
    private void AddPerTestDatabase(IServiceCollection services)
    {
        services.RemoveAll<BlueprintContext>();
        services.RemoveAll<IDbContextFactory<BlueprintContext>>();

        services.AddScoped(provider => SessionFor(provider).CreateContext(provider));
    }

    /// <summary>
    /// Replaces JWT bearer with <see cref="TestAuthHandler"/>, under the same scheme name.
    /// </summary>
    /// <remarks>
    /// The <c>RemoveAll</c> is what makes this possible, and is where blueprint needs more than the other
    /// Crucible suites. They name their test scheme <c>Test</c> and rely on the last
    /// <c>AddAuthentication</c> winning the default scheme. Here the scheme has to <em>be</em>
    /// <c>Bearer</c>, because <c>MainHub</c> demands that name by attribute - and
    /// <c>AuthenticationOptions.AddScheme</c> throws "Scheme already exists: Bearer" if <c>AddJwtBearer</c>
    /// has already claimed it. Every registration that configures <c>AuthenticationOptions</c> is a
    /// single <c>IConfigureOptions&lt;AuthenticationOptions&gt;</c>, so dropping them drops both
    /// <c>Startup</c>'s default-scheme setting and JWT bearer's claim on the name. What is left behind -
    /// the <c>JwtBearerOptions</c> post-configure and the handler type itself - is never resolved once no
    /// scheme names it.
    /// </remarks>
    private static void AddTestAuthentication(IServiceCollection services)
    {
        services.RemoveAll<IConfigureOptions<Microsoft.AspNetCore.Authentication.AuthenticationOptions>>();

        services.AddAuthentication(TestAuthHandler.SchemeName)
            .AddScheme<TestAuthOptions, TestAuthHandler>(
                TestAuthHandler.SchemeName, o => o.Scopes = Scopes);
    }

    /// <summary>
    /// The database session a resolution belongs to: the test that made the request, or the host's own
    /// when there is no request.
    /// </summary>
    /// <remarks>
    /// Falling back rather than throwing because startup legitimately resolves a context outside a
    /// request - <c>DatabaseExtensions.InitializeDatabase</c> is written to do exactly that. A test
    /// wanting a context should take it from <c>DatabaseTestBase.Db</c> or <c>NewContext()</c>, and
    /// <c>DatabaseHarnessTests</c> asserts that requests never land here.
    /// </remarks>
    private TestDatabaseSession SessionFor(IServiceProvider provider)
    {
        var httpContext = provider.GetRequiredService<IHttpContextAccessor>().HttpContext;

        return httpContext is null
            ? _hostSession
            : TestDatabaseScope.Resolve(httpContext);
    }

    /// <summary>
    /// A client whose requests authenticate as <paramref name="userId"/>. Use <c>CreateClient()</c>
    /// directly for the anonymous case.
    /// </summary>
    public HttpClient CreateClientFor(Guid userId, string name = null, string email = null)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.UserIdHeader, userId.ToString());

        if (name is not null)
        {
            client.DefaultRequestHeaders.Add(TestAuthHandler.UserNameHeader, name);
        }

        if (email is not null)
        {
            client.DefaultRequestHeaders.Add(TestAuthHandler.EmailHeader, email);
        }

        return client;
    }

    /// <summary>
    /// An MSEL created by somebody else, so an actor's role on it is what decides what they may do.
    /// </summary>
    /// <remarks>
    /// <c>CreatedBy</c> defaults to a fresh guid rather than to <see cref="Guid.Empty"/> on purpose:
    /// <c>MselViewRequirement</c> and <c>MselOwnerRequirement</c> both short-circuit on
    /// <c>msel.CreatedBy == userId</c>, and an unset <c>CreatedBy</c> would grant both to any caller
    /// whose id also happened to be unset.
    /// </remarks>
    public static MselEntity Msel(
        Guid? createdBy = null,
        bool isTemplate = false,
        MselItemStatus status = MselItemStatus.Pending)
    {
        var id = Guid.NewGuid();

        return new MselEntity
        {
            Id = id,
            Name = $"msel-{id}",
            Description = "Seeded by BlueprintAppFactory.Msel",
            Status = status,
            IsTemplate = isTemplate,
            CreatedBy = createdBy ?? Guid.NewGuid()
        };
    }

    /// <summary>
    /// An organization. With no <paramref name="mselId"/> it is a template, which is the only kind
    /// <c>GET organizations/templates</c> returns.
    /// </summary>
    public static OrganizationEntity Organization(
        Guid? mselId = null,
        Guid? createdBy = null,
        string name = null)
    {
        var id = Guid.NewGuid();

        return new OrganizationEntity
        {
            Id = id,
            Name = name ?? $"org-{id}",
            ShortName = "org",
            Description = "Seeded by BlueprintAppFactory.Organization",
            Summary = "Seeded",
            Email = $"{id}@organization.test",
            IsTemplate = mselId is null,
            MselId = mselId,
            CreatedBy = createdBy ?? Guid.NewGuid()
        };
    }

    /// <summary>
    /// A team on <paramref name="mselId"/>. <c>TeamEntity.MselId</c> is a required foreign key, so a team
    /// always belongs to one.
    /// </summary>
    public static TeamEntity Team(Guid mselId, Guid? createdBy = null)
    {
        var id = Guid.NewGuid();

        return new TeamEntity
        {
            Id = id,
            Name = $"team-{id}",
            ShortName = "team",
            MselId = mselId,
            CreatedBy = createdBy ?? Guid.NewGuid()
        };
    }

    /// <summary>
    /// An inject type. Every catalog needs one - <c>CatalogEntity.InjectTypeId</c> is a non-nullable
    /// foreign key - so seed this before <see cref="Catalog"/>.
    /// </summary>
    public static InjectTypeEntity InjectType(Guid? createdBy = null)
    {
        var id = Guid.NewGuid();

        return new InjectTypeEntity
        {
            Id = id,
            Name = $"injectType-{id}",
            Description = "Seeded by BlueprintAppFactory.InjectType",
            CreatedBy = createdBy ?? Guid.NewGuid()
        };
    }

    /// <summary>
    /// A private catalog by default, so an actor's units are what decide whether they can see it.
    /// <c>CatalogViewRequirement</c> grants any caller a public one.
    /// </summary>
    public static CatalogEntity Catalog(
        Guid injectTypeId,
        Guid? createdBy = null,
        bool isPublic = false)
    {
        var id = Guid.NewGuid();

        return new CatalogEntity
        {
            Id = id,
            Name = $"catalog-{id}",
            Description = "Seeded by BlueprintAppFactory.Catalog",
            InjectTypeId = injectTypeId,
            IsPublic = isPublic,
            CreatedBy = createdBy ?? Guid.NewGuid()
        };
    }

    /// <summary>
    /// A competency framework. <paramref name="idNumber"/> defaults to a fresh value rather than to null
    /// because the column is uniquely indexed, and two frameworks sharing an ID number is the one thing
    /// the create and import paths are supposed to refuse.
    /// </summary>
    public static CompetencyFrameworkEntity CompetencyFramework(
        Guid? createdBy = null,
        string idNumber = null,
        string source = null,
        string version = null)
    {
        var id = Guid.NewGuid();

        return new CompetencyFrameworkEntity
        {
            Id = id,
            Name = $"framework-{id}",
            IdNumber = idNumber ?? $"FW-{id}",
            Description = "Seeded by BlueprintAppFactory.CompetencyFramework",
            Source = source ?? "SEEDED",
            Version = version ?? "1.0",
            CreatedBy = createdBy ?? Guid.NewGuid()
        };
    }

    /// <summary>
    /// A competency in <paramref name="frameworkId"/>. <c>IdNumber</c> is what every relationship in this
    /// area is expressed in - the service resolves related competencies by ID number, never by id - so a
    /// test that cares about relationships should name it.
    /// </summary>
    public static CompetencyEntity Competency(
        Guid frameworkId,
        string idNumber = null,
        Guid? createdBy = null,
        Guid? parentId = null)
    {
        var id = Guid.NewGuid();

        return new CompetencyEntity
        {
            Id = id,
            CompetencyFrameworkId = frameworkId,
            IdNumber = idNumber ?? $"C-{id}",
            ShortName = $"competency-{id}",
            Description = "Seeded by BlueprintAppFactory.Competency",
            ParentId = parentId,
            Path = $"/{id}",
            CreatedBy = createdBy ?? Guid.NewGuid()
        };
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
