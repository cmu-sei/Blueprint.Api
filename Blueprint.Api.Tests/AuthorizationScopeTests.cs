// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Blueprint.Api.Data.Enumerations;
using Blueprint.Api.Tests.Support;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Blueprint.Api.Tests;

/// <summary>
/// The scopes <c>Startup</c> requires of every token: the MVC-wide <c>AuthorizeFilter</c>
/// (<c>RequireScope</c> for each of <c>Authorization:AuthorizationScope</c>) in front of every controller,
/// and the default policy (<c>RequireClaim("scope", x)</c>) behind <c>MainHub</c>'s <c>[Authorize]</c>.
/// Each caller here holds the permission its route asks for, and each denied row lacks exactly one
/// configured scope, so that scope is the only thing missing.
/// </summary>
public class AuthorizationScopeTests(DatabaseFixture fixture, BlueprintAppFactory factory)
    : ApiTestBase(fixture, factory), IClassFixture<BlueprintAppFactory>
{
    private const string SystemRoles = "api/system-roles";

    private const string HubNegotiate = "/hubs/main/negotiate?negotiateVersion=1";

    [Theory]
    [InlineData("blueprint")]
    [InlineData("player")]
    [InlineData("player-vm")]
    [InlineData("cite")]
    [InlineData("gallery")]
    [InlineData("steamfitter")]
    public async Task AControllerRoute_is_forbidden_for_a_caller_holding_its_permission_whose_token_lacks_one_required_scope(string missing)
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewRoles).SeedAsync();

        var response = await WithScopes(Client(actor), ConfiguredScopesBut(missing)).GetAsync(SystemRoles, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task AControllerRoute_with_every_configured_scope_on_the_request_is_allowed()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewRoles).SeedAsync();

        var response = await WithScopes(Client(actor), ConfiguredScopes()).GetAsync(SystemRoles, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("blueprint")]
    [InlineData("player")]
    [InlineData("player-vm")]
    [InlineData("cite")]
    [InlineData("gallery")]
    [InlineData("steamfitter")]
    public async Task TheHub_is_forbidden_for_an_authenticated_caller_whose_token_lacks_one_required_scope(string missing)
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewMsels).SeedAsync();

        var response = await WithScopes(Client(actor), ConfiguredScopesBut(missing)).PostAsync(HubNegotiate, content: null, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task TheHub_with_every_configured_scope_on_the_request_negotiates()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.ViewMsels).SeedAsync();

        var response = await WithScopes(Client(actor), ConfiguredScopes()).PostAsync(HubNegotiate, content: null, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private string ConfiguredScopes() =>
        Factory.Services.GetRequiredService<IConfiguration>()["Authorization:AuthorizationScope"];

    /// <summary>Every configured scope but <paramref name="missing"/>: a token short of exactly one.</summary>
    private string ConfiguredScopesBut(string missing)
    {
        var scopes = ConfiguredScopes().Split(' ').ToList();

        Assert.True(scopes.Remove(missing), $"'{missing}' is not one of the configured scopes");

        return string.Join(' ', scopes);
    }

    private static HttpClient WithScopes(HttpClient client, string scopes)
    {
        client.DefaultRequestHeaders.Add(TestAuthHandler.ScopeHeader, scopes);
        return client;
    }
}
