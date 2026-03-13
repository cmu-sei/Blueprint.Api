// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using Blueprint.Api.Data;
using Crucible.Common.EntityEvents.Extensions;
using Crucible.Common.Testing.Auth;
using Crucible.Common.Testing.Extensions;
using Ganss.Xss;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;
using Xunit;

namespace Blueprint.Api.Tests.Integration.Fixtures;

/// <summary>
/// WebApplicationFactory-based test context for Blueprint API integration tests.
/// Uses Testcontainers to spin up a real PostgreSQL instance per test class.
/// </summary>
public class BlueprintTestContext : WebApplicationFactory<Program>, IAsyncLifetime
{
    private PostgreSqlContainer? _container;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder
            .UseEnvironment("Test")
            .UseSetting("Database:Provider", "PostgreSQL")
            .UseSetting("Authorization:Authority", "https://test-authority.example.com")
            .UseSetting("Authorization:AuthorizationScope", "blueprint-api")
            .UseSetting("Authorization:ClientId", "blueprint-test")
            .ConfigureServices(services =>
            {
                if (_container is null)
                {
                    throw new InvalidOperationException(
                        "Cannot initialize BlueprintTestContext: the PostgreSQL container has not been started.");
                }

                var connectionString = _container.GetConnectionString();

                // Remove the production DbContext registrations (pooled factory + scoped)
                RemoveAllServices<BlueprintContext>(services);
                RemoveAllServices<IDbContextFactory<BlueprintContext>>(services);

                // Register a fresh DbContext factory pointing at the test container
                services.AddEventPublishingDbContextFactory<BlueprintContext>((sp, optionsBuilder) =>
                {
                    optionsBuilder
                        .AddInterceptors(sp.GetRequiredService<Blueprint.Api.Infrastructure.EventHandlers.SanitizerInterceptor>())
                        .UseNpgsql(connectionString);
                });

                // Replace authentication with the test handler
                services
                    .ReplaceService<IClaimsTransformation, TestClaimsTransformation>(allowMultipleReplace: true);

                // Replace authorization to allow everything through
                services
                    .ReplaceService<IAuthorizationService, TestAuthorizationService>();

                // Ensure HtmlSanitizer is registered (needed by SanitizerInterceptor)
                if (services.FindService<IHtmlSanitizer>() is null)
                {
                    services.AddSingleton<IHtmlSanitizer>(new HtmlSanitizer());
                }

                // Override authentication scheme
                services.AddAuthentication(TestAuthenticationHandler.AuthenticationSchemeName)
                    .AddScheme<TestAuthenticationHandlerOptions, TestAuthenticationHandler>(
                        TestAuthenticationHandler.AuthenticationSchemeName, options => { });
            });
    }

    /// <summary>
    /// Creates a scoped DbContext for direct database verification in tests.
    /// </summary>
    public BlueprintContext GetDbContext()
    {
        return Services.GetRequiredService<BlueprintContext>();
    }

    /// <summary>
    /// Runs a validation action against a scoped DbContext.
    /// </summary>
    public async Task ValidateDbStateAsync(Func<BlueprintContext, Task> validationAction)
    {
        using var scope = Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<BlueprintContext>();
        await validationAction.Invoke(dbContext);
    }

    public async Task InitializeAsync()
    {
        _container = new PostgreSqlBuilder()
            .WithHostname("localhost")
            .WithUsername("blueprint_test")
            .WithPassword("blueprint_test")
            .WithImage("postgres:latest")
            .WithAutoRemove(true)
            .WithCleanUp(true)
            .Build();

        await _container.StartAsync();
    }

    public new async Task DisposeAsync()
    {
        if (_container is not null)
            await _container.DisposeAsync();
    }

    private static void RemoveAllServices<T>(IServiceCollection services)
    {
        var descriptors = services.Where(d => d.ServiceType == typeof(T)).ToList();
        foreach (var descriptor in descriptors)
        {
            services.Remove(descriptor);
        }
    }
}
