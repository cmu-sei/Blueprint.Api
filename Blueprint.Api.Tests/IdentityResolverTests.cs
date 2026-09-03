// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Security.Claims;
using Blueprint.Api.Infrastructure.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using NSubstitute;
using Xunit;

namespace Blueprint.Api.Tests;

/// <summary>
/// Twelve lines of production code, and the fallback path of every authorization decision: when
/// <c>UserClaimsService</c> has no current principal - a SignalR connect, or a background call - this is
/// where <c>BlueprintAuthorizationService</c> looks instead.
/// </summary>
public class IdentityResolverTests
{
    [Fact]
    public void GetClaimsPrincipal_ReturnsTheRequestsUser()
    {
        var principal = Principal(Guid.NewGuid());

        Assert.Same(principal, Resolver(new DefaultHttpContext { User = principal }).GetClaimsPrincipal());
    }

    /// <summary>
    /// Off-request - a hosted service, or the queue workers - there is no <c>HttpContext</c> and the
    /// null-conditional chain returns null rather than throwing. That null is what
    /// <c>BlueprintAuthorizationService</c> then hands to the authorization service, where
    /// <c>SystemPermissionHandler</c> fails it.
    /// </summary>
    [Fact]
    public void GetClaimsPrincipal_WithNoHttpContext_ReturnsNull()
    {
        Assert.Null(Resolver(null).GetClaimsPrincipal());
    }

    /// <summary>
    /// <c>DefaultHttpContext.User</c> is never null - it materializes an empty principal - but a context
    /// built by something else can be. The chain tolerates it.
    /// </summary>
    [Fact]
    public void GetClaimsPrincipal_WithNoUserOnTheContext_ReturnsNull()
    {
        var context = Substitute.For<HttpContext>();
        context.User.Returns((ClaimsPrincipal)null);

        Assert.Null(Resolver(context).GetClaimsPrincipal());
    }

    /// <summary>
    /// The accessor itself is null-conditional, so a resolver built without one is inert rather than
    /// broken. Worth pinning because it is the difference between a null id and a crash in
    /// <c>GetSystemPermissions</c>.
    /// </summary>
    [Fact]
    public void GetClaimsPrincipal_WithNoAccessor_ReturnsNull()
    {
        var resolver = new IdentityResolver(null, Substitute.For<IAuthorizationService>());

        Assert.Null(resolver.GetClaimsPrincipal());
    }

    [Fact]
    public void GetId_ReadsTheSubClaimOfTheRequestsUser()
    {
        var id = Guid.NewGuid();

        Assert.Equal(id, Resolver(new DefaultHttpContext { User = Principal(id) }).GetId());
    }

    /// <remarks>
    /// Characterization. <c>GetId</c> forwards straight to <c>ClaimsPrincipalExtensions.GetId</c> with no
    /// null check, so off-request it throws <see cref="NullReferenceException"/> - see
    /// <see cref="ClaimsPrincipalExtensionsTests.GetId_OnANullPrincipal_ThrowsNullReferenceException"/>.
    /// Every service that reads <c>_user.GetId()</c> inherits this. Turns red when the resolver returns
    /// <see cref="Guid.Empty"/> or throws something that says there is no caller.
    /// </remarks>
    [Fact]
    public void GetId_WithNoHttpContext_ThrowsNullReferenceException()
    {
        Assert.Throws<NullReferenceException>(() => Resolver(null).GetId());
    }

    /// <remarks>
    /// Characterization. An authenticated caller whose token carries no <c>sub</c> - possible with a
    /// client-credentials token - produces <see cref="ArgumentNullException"/> from a <c>Guid.Parse</c>
    /// deep inside the extension, which surfaces as a 500 rather than a 401.
    /// </remarks>
    [Fact]
    public void GetId_WithAUserCarryingNoSubClaim_ThrowsArgumentNullException()
    {
        var context = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity([], "Bearer")) };

        Assert.Throws<ArgumentNullException>(() => Resolver(context).GetId());
    }

    /// <summary>
    /// The accessor is read on every call rather than captured, so a resolver resolved once per scope
    /// still sees the right request. NSubstitute returning a different context proves it is not cached.
    /// </summary>
    [Fact]
    public void GetClaimsPrincipal_ReadsTheAccessorOnEveryCall()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();

        var accessor = Substitute.For<IHttpContextAccessor>();
        accessor.HttpContext.Returns(
            new DefaultHttpContext { User = Principal(first) },
            new DefaultHttpContext { User = Principal(second) });

        var resolver = new IdentityResolver(accessor, Substitute.For<IAuthorizationService>());

        Assert.Equal(first, resolver.GetId());
        Assert.Equal(second, resolver.GetId());
    }

    private static IdentityResolver Resolver(HttpContext context)
    {
        var accessor = Substitute.For<IHttpContextAccessor>();
        accessor.HttpContext.Returns(context);

        // The authorization service is a constructor dependency this class never uses. Substituted, not
        // omitted, so a future use of it fails here rather than on a null.
        return new IdentityResolver(accessor, Substitute.For<IAuthorizationService>());
    }

    private static ClaimsPrincipal Principal(Guid id) =>
        new(new ClaimsIdentity([new Claim("sub", id.ToString())], "Bearer"));
}
