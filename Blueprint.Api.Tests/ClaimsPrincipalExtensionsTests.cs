// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Linq;
using System.Security.Claims;
using Blueprint.Api.Infrastructure.Extensions;
using Xunit;

namespace Blueprint.Api.Tests;

/// <summary>
/// <c>GetId</c> is the single most-called method in the codebase - every service reads the caller's id
/// through it - and <c>NormalizeScopeClaims</c> decides whether the MVC-wide scope policy can be
/// satisfied at all. Neither touches a database, so both are tested directly.
/// </summary>
public class ClaimsPrincipalExtensionsTests
{
    private const string NameIdentifier =
        "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier";

    [Fact]
    public void GetId_ReadsTheSubClaim()
    {
        var id = Guid.NewGuid();

        Assert.Equal(id, Principal(new Claim("sub", id.ToString())).GetId());
    }

    /// <summary>
    /// The fallback exists for tokens that carry the SOAP-era name identifier instead of <c>sub</c>,
    /// which is what a principal built by some of the Microsoft handlers looks like.
    /// </summary>
    [Fact]
    public void GetId_WithNoSubClaim_FallsBackToTheNameIdentifier()
    {
        var id = Guid.NewGuid();

        Assert.Equal(id, Principal(new Claim(NameIdentifier, id.ToString())).GetId());
    }

    /// <summary>
    /// The fallback is reached by <em>exception</em>, not by a null check, so it also catches a <c>sub</c>
    /// that is present and unparseable rather than only one that is absent.
    /// </summary>
    [Fact]
    public void GetId_WithAnUnparseableSubClaim_FallsBackToTheNameIdentifier()
    {
        var id = Guid.NewGuid();

        var principal = Principal(
            new Claim("sub", "not-a-guid"),
            new Claim(NameIdentifier, id.ToString()));

        Assert.Equal(id, principal.GetId());
    }

    [Fact]
    public void GetId_PrefersSubOverTheNameIdentifier()
    {
        var sub = Guid.NewGuid();

        var principal = Principal(
            new Claim("sub", sub.ToString()),
            new Claim(NameIdentifier, Guid.NewGuid().ToString()));

        Assert.Equal(sub, principal.GetId());
    }

    [Fact]
    public void GetId_WithSeveralSubClaims_TakesTheFirst()
    {
        var first = Guid.NewGuid();

        var principal = Principal(
            new Claim("sub", first.ToString()),
            new Claim("sub", Guid.NewGuid().ToString()));

        Assert.Equal(first, principal.GetId());
    }

    /// <remarks>
    /// Characterization. Neither claim is present, so both <c>Guid.Parse</c> calls receive null and the
    /// second one's <see cref="ArgumentNullException"/> escapes. Callers see a parse failure rather than
    /// "this principal has no id", which is why an unauthenticated request that reaches a service
    /// surfaces as a 500. Turns red when the method returns <see cref="Guid.Empty"/> or throws something
    /// that names the problem.
    /// </remarks>
    [Fact]
    public void GetId_WithNeitherClaim_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => Principal().GetId());
    }

    /// <remarks>
    /// Characterization, and the reason <c>UserClaimsService.GetClaimsPrincipal</c> can fail on a null
    /// <c>_currentClaimsPrincipal</c>: the <c>catch</c> re-dereferences the same null the <c>try</c> just
    /// failed on. See <see cref="UserClaimsServiceTests"/>.
    /// </remarks>
    [Fact]
    public void GetId_OnANullPrincipal_ThrowsNullReferenceException()
    {
        ClaimsPrincipal principal = null;

        Assert.Throws<NullReferenceException>(() => principal.GetId());
    }

    /// <remarks>
    /// Characterization. Both claims are present and neither parses, so the exception the caller sees is
    /// the <em>second</em> failure - the first is discarded by the bare <c>catch</c>. A malformed
    /// <c>sub</c> is therefore reported as a malformed name identifier.
    /// </remarks>
    [Fact]
    public void GetId_WithBothClaimsUnparseable_ReportsTheSecondFailure()
    {
        var principal = Principal(
            new Claim("sub", "not-a-guid"),
            new Claim(NameIdentifier, "also-not-a-guid"));

        var ex = Assert.Throws<FormatException>(() => principal.GetId());

        Assert.DoesNotContain("not-a-guid", ex.Message);
    }

    [Theory]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    [InlineData("{6101BFD5-3113-4F1B-964E-0F79C2D8073F}")]
    [InlineData("6101bfd5-3113-4f1b-964e-0f79c2d8073f")]
    public void GetId_AcceptsEveryFormatGuidParseDoes(string value)
    {
        Assert.Equal(Guid.Parse(value), Principal(new Claim("sub", value)).GetId());
    }

    [Fact]
    public void NormalizeScopeClaims_SplitsASpaceDelimitedScopeClaim()
    {
        var principal = Principal(new Claim("scope", "blueprint player cite"));

        var scopes = Scopes(principal.NormalizeScopeClaims());

        Assert.Equal(["blueprint", "cite", "player"], scopes.Order());
    }

    /// <summary>
    /// The shape the default policy needs: <c>AuthorizationPolicyExtension</c> requires one
    /// <c>scope</c> claim per configured scope, and an IdP that packs them all into one claim value
    /// would satisfy none of them without this.
    /// </summary>
    [Fact]
    public void NormalizeScopeClaims_ProducesOneClaimPerScope()
    {
        var principal = Principal(new Claim("scope", "a b c d e f")).NormalizeScopeClaims();

        Assert.Equal(6, Scopes(principal).Length);
    }

    [Fact]
    public void NormalizeScopeClaims_LeavesASingleScopeAlone()
    {
        var principal = Principal(new Claim("scope", "blueprint"));

        Assert.Equal(["blueprint"], Scopes(principal.NormalizeScopeClaims()));
    }

    [Fact]
    public void NormalizeScopeClaims_DropsTheEmptyEntriesRepeatedSpacesWouldProduce()
    {
        var principal = Principal(new Claim("scope", "  blueprint   player  "));

        Assert.Equal(["blueprint", "player"], Scopes(principal.NormalizeScopeClaims()).Order());
    }

    [Fact]
    public void NormalizeScopeClaims_KeepsNonScopeClaims()
    {
        var id = Guid.NewGuid();

        var principal = Principal(
            new Claim("sub", id.ToString()),
            new Claim("name", "Someone"),
            new Claim("scope", "blueprint player")).NormalizeScopeClaims();

        Assert.Equal(id, principal.GetId());
        Assert.Equal("Someone", principal.FindFirst("name").Value);
        Assert.Equal(2, Scopes(principal).Length);
    }

    /// <summary>
    /// A split scope keeps the original's value type and issuer, so a policy or handler that filters on
    /// either still sees what the token said.
    /// </summary>
    [Fact]
    public void NormalizeScopeClaims_CarriesTheValueTypeAndIssuerOntoEachSplitScope()
    {
        var original = new Claim("scope", "blueprint player", ClaimValueTypes.String, "https://idp.test");

        var split = Principal(original).NormalizeScopeClaims()
            .Claims.Where(x => x.Type == "scope").ToArray();

        Assert.All(split, claim =>
        {
            Assert.Equal(ClaimValueTypes.String, claim.ValueType);
            Assert.Equal("https://idp.test", claim.Issuer);
        });
    }

    [Fact]
    public void NormalizeScopeClaims_PreservesTheAuthenticationType()
    {
        var identity = new ClaimsIdentity([new Claim("scope", "blueprint player")], "Bearer");

        var normalized = new ClaimsPrincipal(identity).NormalizeScopeClaims();

        Assert.Equal("Bearer", normalized.Identity.AuthenticationType);
        Assert.True(normalized.Identity.IsAuthenticated);
    }

    /// <summary>
    /// An identity with no authentication type stays unauthenticated. If normalizing invented one, an
    /// anonymous request would start satisfying <c>[Authorize]</c>.
    /// </summary>
    [Fact]
    public void NormalizeScopeClaims_LeavesAnUnauthenticatedIdentityUnauthenticated()
    {
        var normalized = new ClaimsPrincipal(new ClaimsIdentity()).NormalizeScopeClaims();

        Assert.False(normalized.Identity.IsAuthenticated);
    }

    [Fact]
    public void NormalizeScopeClaims_PreservesTheNameAndRoleClaimTypes()
    {
        var identity = new ClaimsIdentity([], "Bearer", "my-name", "my-role");

        var normalized = (ClaimsIdentity)new ClaimsPrincipal(identity).NormalizeScopeClaims().Identity;

        Assert.Equal("my-name", normalized.NameClaimType);
        Assert.Equal("my-role", normalized.RoleClaimType);
    }

    [Fact]
    public void NormalizeScopeClaims_KeepsEveryIdentity()
    {
        var principal = new ClaimsPrincipal(
        [
            new ClaimsIdentity([new Claim("scope", "blueprint player")], "Bearer"),
            new ClaimsIdentity([new Claim("sub", Guid.NewGuid().ToString())], "Cookies")
        ]);

        var normalized = principal.NormalizeScopeClaims();

        Assert.Equal(2, normalized.Identities.Count());
        Assert.Equal(2, Scopes(normalized).Length);
    }

    /// <summary>
    /// A new principal, and the original untouched - the transformer keeps using the return value, and a
    /// method that mutated its argument would leave the two disagreeing.
    /// </summary>
    [Fact]
    public void NormalizeScopeClaims_DoesNotMutateTheOriginal()
    {
        var principal = Principal(new Claim("scope", "blueprint player"));

        var normalized = principal.NormalizeScopeClaims();

        Assert.NotSame(principal, normalized);
        Assert.Equal(["blueprint player"], Scopes(principal));
    }

    [Fact]
    public void NormalizeScopeClaims_WithNoClaimsAtAll_ReturnsAnEmptyPrincipal()
    {
        var normalized = Principal().NormalizeScopeClaims();

        Assert.Empty(normalized.Claims);
    }

    private static ClaimsPrincipal Principal(params Claim[] claims) =>
        new(new ClaimsIdentity(claims, "Bearer"));

    private static string[] Scopes(ClaimsPrincipal principal) =>
        [.. principal.Claims.Where(x => x.Type == "scope").Select(x => x.Value)];
}
