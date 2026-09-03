// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Blueprint.Api.Tests.Infrastructure;

public class TestAuthOptions : AuthenticationSchemeOptions
{
    /// <summary>Scopes granted to every authenticated test request.</summary>
    public IEnumerable<string> Scopes { get; set; } = [];
}

/// <summary>
/// Stands in for the JWT bearer handler so tests do not need Keycloak. A request carrying the
/// <see cref="UserIdHeader"/> header authenticates as that user; a request without it presents no
/// credentials at all, which keeps the 401 path testable.
/// </summary>
/// <remarks>
/// <para>
/// This mints only what a real access token would carry - <c>sub</c>, optionally <c>name</c>, and the
/// scopes. Everything that decides what the caller may *do* still comes from the database, because
/// <c>AuthorizationClaimsTransformer</c> is an <c>IClaimsTransformation</c> and so runs for whatever
/// scheme authenticated the request: the real <c>UserClaimsService</c> reads the user's
/// <c>SystemRoleEntity</c> and adds the permission claims. See <see cref="TestActorBuilder"/>.
/// </para>
/// <para>
/// The scheme is named <c>Bearer</c>, and that is not cosmetic. It is <c>Startup</c>'s default scheme,
/// and <c>MainHub</c> carries <c>[Authorize(AuthenticationSchemes = "Bearer")]</c> - the only such
/// attribute in the codebase - so a scheme named anything else would leave every hub request
/// unauthenticated. <see cref="BlueprintAppFactory"/> has to unpick the JWT registration to claim the
/// name, because <c>AuthenticationOptions.AddScheme</c> throws when two handlers register one.
/// </para>
/// <para>
/// The scopes come from <see cref="BlueprintAppFactory"/> rather than being hardcoded here, because
/// <c>Startup</c> builds its MVC-wide authorization filter out of
/// <c>Authorization:AuthorizationScope</c> and requires <em>every</em> scope in it - a principal missing
/// any one of the six blueprint ships with never reaches a controller.
/// </para>
/// </remarks>
public class TestAuthHandler(
    IOptionsMonitor<TestAuthOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder) : AuthenticationHandler<TestAuthOptions>(options, logger, encoder)
{
    public const string SchemeName = JwtBearerDefaults.AuthenticationScheme;

    /// <summary>Set to the user's guid. Absent means "no credentials presented".</summary>
    public const string UserIdHeader = "X-Test-User";

    /// <summary>
    /// The <c>name</c> claim, optional. Present because it is not inert: <c>UserClaimsService.ValidateUser</c>
    /// writes it back to the user row, and falls back to "Anonymous" for a user it has to create.
    /// </summary>
    public const string UserNameHeader = "X-Test-Name";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(UserIdHeader, out var userId))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var claims = new List<Claim> { new("sub", userId.ToString()) };

        if (Request.Headers.TryGetValue(UserNameHeader, out var name))
        {
            claims.Add(new Claim("name", name.ToString()));
        }

        claims.AddRange(Options.Scopes.Select(x => new Claim("scope", x)));

        var identity = new ClaimsIdentity(claims, SchemeName);

        return Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }
}
