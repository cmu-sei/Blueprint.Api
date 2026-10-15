// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

// App-specific: BlueprintContext gets two interceptors, SanitizerInterceptor then EntityEventInterceptor, in
// the order production attaches them, and the warning production turns into an exception.

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

namespace Blueprint.Api.Tests.Support;

/// <summary>Builds <see cref="BlueprintContext"/> instances wired the way production wires them.</summary>
/// <remarks>
/// <para>
/// <see cref="BlueprintContext"/> extends <c>EventPublishingDbContext</c>, whose <c>PublishEventsAsync</c>
/// resolves <see cref="IMediator"/> and a logger off the settable <c>ServiceProvider</c> property with
/// <c>GetRequiredService</c>. Both must be registered or the first event-publishing save throws.
/// </para>
/// <para>
/// Production builds the interceptor pair in two places: <c>Startup</c> adds <see cref="SanitizerInterceptor"/>
/// inside the configure callback and <c>AddEventPublishingDbContextFactory</c> appends
/// <see cref="EntityEventInterceptor"/> after it. A context with only the event interceptor would skip HTML
/// sanitizing. Both are resolved from the provider the context is given, so a request's context (its scope)
/// and a test's own context (<see cref="CreateServices"/>) get them the same way.
/// </para>
/// </remarks>
internal static class BlueprintContextFactory
{
    /// <summary>
    /// The provider a session shares across its contexts, and the substituted mediator tests assert on.
    /// A substitute is right here: each session gets its own, and only its own test reads it.
    /// </summary>
    /// <remarks>
    /// <c>AddHtmlSanitizer</c> gets an empty configuration, so the sanitizer runs on its defaults; the
    /// application's <c>HtmlSanitizer</c> section only widens the allow-list. A test that needs the configured
    /// allow-list drives a request.
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
    /// A context over the given provider configuration, with production's interceptors attached so
    /// SaveChanges sanitizes and publishes events exactly as it does in production.
    /// </summary>
    public static BlueprintContext CreateContext(
        Action<DbContextOptionsBuilder<BlueprintContext>> configureProvider,
        IServiceProvider services)
    {
        var builder = new DbContextOptionsBuilder<BlueprintContext>();
        configureProvider(builder);

        // From UseConfiguredDatabase: a query that eagerly loads two collections at once throws rather than
        // producing a cartesian product, so a test sees what production does.
        builder.ConfigureWarnings(w => w.Throw(RelationalEventId.MultipleCollectionIncludeWarning));

        builder.AddInterceptors(
            services.GetRequiredService<SanitizerInterceptor>(),
            services.GetRequiredService<EntityEventInterceptor>());

        return new BlueprintContext(builder.Options) { ServiceProvider = services };
    }
}
