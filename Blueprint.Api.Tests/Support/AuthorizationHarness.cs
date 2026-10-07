// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

// App-specific: blueprint registers one authorization handler, SystemPermissionHandler, behind
// BlueprintAuthorizationService. The MSEL, unit, team and catalog checks are static requirement helpers
// over BlueprintContext, tested against a database rather than through this harness.

using System.Security.Claims;
using System.Threading.Tasks;
using Blueprint.Api.Infrastructure.Authorization;
using Blueprint.Api.Infrastructure.Identity;
using Blueprint.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;

namespace Blueprint.Api.Tests.Support;

/// <summary>The authorization stack wired as production wires it, for testing handlers directly.</summary>
public static class AuthorizationHarness
{
    /// <summary>The framework authorization service with the app's handler registered.</summary>
    public static IAuthorizationService CreateFrameworkAuthorizationService()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthorization();
        services.AddSingleton<IAuthorizationHandler, SystemPermissionHandler>();

        return services.BuildServiceProvider().GetRequiredService<IAuthorizationService>();
    }

    /// <summary>
    /// <see cref="BlueprintAuthorizationService"/> over the real framework service and handler, with the two
    /// sources of the caller's identity it consults given by the test.
    /// </summary>
    public static BlueprintAuthorizationService CreateBlueprintAuthorizationService(
        IUserClaimsService userClaims,
        IIdentityResolver identity) =>
        new(CreateFrameworkAuthorizationService(), userClaims, identity);

    /// <summary>
    /// Runs a requirement through a handler directly and returns the resulting context.
    /// </summary>
    public static async Task<AuthorizationHandlerContext> HandleAsync<TRequirement>(
        IAuthorizationHandler handler,
        TRequirement requirement,
        ClaimsPrincipal user,
        object resource = null)
        where TRequirement : IAuthorizationRequirement
    {
        var context = new AuthorizationHandlerContext([requirement], user, resource);
        await handler.HandleAsync(context);

        return context;
    }
}
