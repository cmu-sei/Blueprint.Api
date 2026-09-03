// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using Blueprint.Api.Data;
using Blueprint.Api.Infrastructure.EventHandlers;
using Blueprint.Api.Infrastructure.Extensions;
using Crucible.Common.EntityEvents.Extensions;
using Crucible.Common.EntityEvents.Interceptors;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Blueprint.Api.Tests.Infrastructure;

/// <summary>
/// Builds <see cref="BlueprintContext"/> instances wired the way production wires them.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="BlueprintContext"/> extends <c>EventPublishingDbContext</c>, and its
/// <c>PublishEventsAsync</c> resolves both <see cref="IMediator"/> and
/// <c>ILogger&lt;BlueprintContext&gt;</c> off the settable <c>ServiceProvider</c> property with
/// <c>GetRequiredService</c>. Both have to be registered or the first save that publishes an event
/// throws.
/// </para>
/// <para>
/// Two interceptors are attached, in the order production attaches them. Production builds the pair
/// across two places - <c>Startup</c> adds <see cref="SanitizerInterceptor"/> inside the configure
/// callback and <c>AddEventPublishingDbContextFactory</c> appends
/// <see cref="EntityEventInterceptor"/> after it - so a context with only the event interceptor would
/// silently skip HTML sanitizing and let a test prove that a <c>[SanitizeHtml]</c> property stores
/// whatever it was sent.
/// </para>
/// </remarks>
internal static class BlueprintContextFactory
{
    /// <summary>
    /// Where <see cref="BlueprintContext"/>'s migrations live.
    /// </summary>
    /// <remarks>
    /// Not optional, and this is where blueprint differs from the other Crucible suites: the context is
    /// in <c>Blueprint.Api.Data</c> and the migrations are in a third assembly, so EF's default - the
    /// context's own assembly - finds nothing and <c>MigrateAsync</c> would create an empty schema.
    /// <c>DatabaseExtensions.UseConfiguredDatabase</c> names it the same way. <c>Blueprint.Api</c>
    /// project-references the migrations project, so referencing <c>Blueprint.Api</c> is enough to load
    /// it.
    /// </remarks>
    public const string MigrationsAssembly = "Blueprint.Api.Migrations.PostgreSQL";

    /// <summary>
    /// The service provider a session shares across all of its contexts, along with the substituted
    /// mediator tests assert entity events on.
    /// </summary>
    /// <remarks>
    /// The interceptors are registered rather than constructed so that
    /// <see cref="CreateContext(string, IServiceProvider)"/> resolves them the same way whether it is
    /// handed this provider or a request scope from the hosted application.
    /// <see cref="Blueprint.Api.Infrastructure.Extensions.ServiceCollectionExtensions.AddHtmlSanitizer"/>
    /// gets an empty configuration here, so the sanitizer runs on Ganss defaults; the application's
    /// <c>HtmlSanitizer</c> section only ever *widens* the allow-list, so "a script tag is stripped"
    /// behaves identically. A test that needs the configured allow-list has to drive a request.
    /// </remarks>
    public static (IServiceProvider Services, IMediator Mediator) CreateServices()
    {
        var mediator = Substitute.For<IMediator>();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(mediator);
        services.AddEntityEventInterceptor();
        services.AddTransient<SanitizerInterceptor>();
        services.AddHtmlSanitizer(new ConfigurationBuilder().Build());

        return (services.BuildServiceProvider(), mediator);
    }

    /// <summary>
    /// A context for the given connection string, wired as production wires it, whose
    /// <c>ServiceProvider</c> is <paramref name="provider"/>.
    /// </summary>
    public static BlueprintContext CreateContext(string connectionString, IServiceProvider provider)
    {
        var builder = new DbContextOptionsBuilder<BlueprintContext>();

        builder.UseNpgsql(connectionString, options => options.MigrationsAssembly(MigrationsAssembly));

        // Also from UseConfiguredDatabase. It turns a query that eagerly loads two collections at once
        // into an exception rather than a cartesian product, which several MselService includes are one
        // edit away from - so a test suite that dropped it would be proving things against a laxer
        // context than production has.
        builder.ConfigureWarnings(w => w.Throw(RelationalEventId.MultipleCollectionIncludeWarning));

        builder.AddInterceptors(
            provider.GetRequiredService<SanitizerInterceptor>(),
            provider.GetRequiredService<EntityEventInterceptor>());

        return new BlueprintContext(builder.Options) { ServiceProvider = provider };
    }
}
