// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Security.Claims;
using Blueprint.Api.Infrastructure.Identity;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Blueprint.Api.Tests;

/// <summary>
/// Decides which SignalR connections <c>Clients.User(id)</c> reaches. Blueprint's event handlers
/// broadcast to groups rather than users, so nothing depends on it today - but it is registered, it is
/// three lines, and a wrong answer here would silently deliver notifications to nobody.
/// </summary>
public class SubUserIdProviderTests
{
    [Fact]
    public void GetUserId_ReturnsTheSubClaim()
    {
        var id = Guid.NewGuid();

        var userId = new SubUserIdProvider().GetUserId(Connection(new Claim("sub", id.ToString())));

        Assert.Equal(id.ToString(), userId);
    }

    /// <summary>
    /// The raw claim value, not a parsed guid - so it is whatever the token said, byte for byte. That is
    /// what makes it match the identifier <c>Clients.User(...)</c> is called with.
    /// </summary>
    [Fact]
    public void GetUserId_DoesNotNormalizeTheValue()
    {
        var userId = new SubUserIdProvider().GetUserId(Connection(new Claim("sub", "not-a-guid")));

        Assert.Equal("not-a-guid", userId);
    }

    /// <summary>
    /// Null rather than a throw: SignalR treats a null user identifier as "this connection belongs to no
    /// user", which is the right outcome for a token with no subject.
    /// </summary>
    [Fact]
    public void GetUserId_WithNoSubClaim_ReturnsNull()
    {
        Assert.Null(new SubUserIdProvider().GetUserId(Connection()));
    }

    [Fact]
    public void GetUserId_WithNoUser_ReturnsNull()
    {
        Assert.Null(new SubUserIdProvider().GetUserId(new FakeHubConnectionContext(null)));
    }

    /// <summary>
    /// The SOAP-era name identifier is <em>not</em> a fallback here, unlike
    /// <c>ClaimsPrincipalExtensions.GetId</c>. Worth pinning: the two ways blueprint identifies a caller
    /// disagree, so a principal carrying only the name identifier has an id everywhere except SignalR.
    /// </summary>
    [Fact]
    public void GetUserId_DoesNotFallBackToTheNameIdentifier()
    {
        var connection = Connection(
            new Claim("http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier",
                Guid.NewGuid().ToString()));

        Assert.Null(new SubUserIdProvider().GetUserId(connection));
    }

    [Fact]
    public void GetUserId_WithSeveralSubClaims_TakesTheFirst()
    {
        var connection = Connection(new Claim("sub", "first"), new Claim("sub", "second"));

        Assert.Equal("first", new SubUserIdProvider().GetUserId(connection));
    }

    private static HubConnectionContext Connection(params Claim[] claims) =>
        new FakeHubConnectionContext(new ClaimsPrincipal(new ClaimsIdentity(claims, "Bearer")));

    /// <remarks>
    /// <c>HubConnectionContext.User</c> has no public setter - SignalR assigns it while negotiating - so
    /// the only way to supply one is to override the virtual property. The base constructor needs a real
    /// <see cref="ConnectionContext"/>, and <see cref="DefaultConnectionContext"/> is one; nothing here
    /// reads the transport.
    /// </remarks>
    private sealed class FakeHubConnectionContext(ClaimsPrincipal user) : HubConnectionContext(
        new DefaultConnectionContext(Guid.NewGuid().ToString()),
        new HubConnectionContextOptions(),
        NullLoggerFactory.Instance)
    {
        public override ClaimsPrincipal User { get; } = user;
    }
}
