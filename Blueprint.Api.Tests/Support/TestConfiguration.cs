// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System.Collections.Generic;

namespace Blueprint.Api.Tests.Support;

/// <summary>
/// The configuration the app factory layers over the application's own <c>appsettings.json</c>.
/// </summary>
/// <remarks>
/// <para>
/// <c>WebApplicationFactory</c> resolves the content root to the API project directory, so the shipped
/// configuration is already in force and only keys whose shipped value breaks or weakens a test run belong
/// here. Every entry states which.
/// </para>
/// <para>
/// <c>Authorization:AuthorizationScope</c> is deliberately not here. The shipped value
/// (<c>"blueprint player player-vm cite gallery steamfitter"</c>) is what <c>Startup</c> builds its MVC-wide
/// authorization filter from, requiring every scope, and what the shared <c>TestAuthHandler</c> reads to mint
/// the <c>scope</c> claims, so the two cannot drift apart.
/// </para>
/// </remarks>
internal static class TestConfiguration
{
    /// <summary>The <c>iss</c> claim every authenticated test request carries, as Keycloak's tokens do.</summary>
    public const string Issuer = "https://localhost:8443/realms/crucible";

    public static Dictionary<string, string> Values => new()
    {
        // XApiService.EnsureAgentInitialized reads the caller's iss claim with First, so an xAPI-enabled
        // host needs it on every authenticated request (the shared TestAuthHandler mints it from this key).
        [TestAuthHandler.IssuerKey] = Issuer,

        // Startup.Configure promotes a ?bearer= query parameter into Authorization: Bearer <token>;
        // reading the user from that header is what lets MiddlewareTests observe the promotion. Every other
        // test addresses itself with X-Test-User, which wins.
        [TestAuthHandler.UserFromBearerKey] = "true",

        // One host serves a whole test class, and UserClaimsService caches a user's claims in the host-wide
        // IMemoryCache keyed on user id alone, so cached claims would let one test's permissions answer
        // another test's request. UserClaimsServiceTests drive the cache directly.
        ["ClaimsTransformation:EnableCaching"] = "false",
    };
}
