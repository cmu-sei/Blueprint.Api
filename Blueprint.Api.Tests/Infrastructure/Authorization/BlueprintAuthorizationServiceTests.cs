// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Blueprint.Api.Data.Enumerations;
using Blueprint.Api.Infrastructure.Authorization;
using Blueprint.Api.Infrastructure.Identity;
using Blueprint.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

namespace Blueprint.Api.Tests.Infrastructure.Authorization;

/// <summary>The seam all 41 controllers call before doing anything. Two collaborators are substituted - the
/// two sources of a caller's identity - and the authorization service underneath is the framework's real
/// one with the real <see cref="SystemPermissionHandler"/> registered, so what these tests assert is the
/// whole decision rather than a hand-drawn sketch of it.</summary>
public class BlueprintAuthorizationServiceTests
{
    private readonly IUserClaimsService _userClaims = Substitute.For<IUserClaimsService>();
    private readonly IIdentityResolver _identity = Substitute.For<IIdentityResolver>();
    private readonly BlueprintAuthorizationService _service;

    public BlueprintAuthorizationServiceTests()
    {
        var authorization = new ServiceCollection()
            .AddLogging()
            .AddAuthorization()
            .AddSingleton<IAuthorizationHandler, SystemPermissionHandler>()
            .BuildServiceProvider()
            .GetRequiredService<IAuthorizationService>();

        _service = new BlueprintAuthorizationService(authorization, _userClaims, _identity);
    }

    [Fact]
    public async Task AuthorizeAsync_WithTheRequiredPermission_IsTrue()
    {
        _userClaims.GetCurrentClaimsPrincipal().Returns(Principal(SystemPermission.CreateMsels));

        Assert.True(await _service.AuthorizeAsync([SystemPermission.CreateMsels], Ct));
    }

    [Fact]
    public async Task AuthorizeAsync_WithoutTheRequiredPermission_IsFalse()
    {
        _userClaims.GetCurrentClaimsPrincipal().Returns(Principal(SystemPermission.ViewMsels));

        Assert.False(await _service.AuthorizeAsync([SystemPermission.CreateMsels], Ct));
    }

    [Fact]
    public async Task AuthorizeAsync_WithAnyOfSeveralRequiredPermissions_IsTrue()
    {
        _userClaims.GetCurrentClaimsPrincipal().Returns(Principal(SystemPermission.ManageOrganizations));

        Assert.True(await _service.AuthorizeAsync(
            [SystemPermission.CreateMsels, SystemPermission.ManageOrganizations], Ct));
    }

    /// <summary>
    /// The claims service is asked first and the resolver is not consulted at all - which is what keeps a
    /// request from paying for a second claims lookup.
    /// </summary>
    [Fact]
    public async Task AuthorizeAsync_PrefersTheCurrentPrincipalOverTheResolver()
    {
        _userClaims.GetCurrentClaimsPrincipal().Returns(Principal(SystemPermission.CreateMsels));
        _identity.GetClaimsPrincipal().Returns(Principal());

        Assert.True(await _service.AuthorizeAsync([SystemPermission.CreateMsels], Ct));

        _identity.DidNotReceive().GetClaimsPrincipal();
    }

    /// <summary>
    /// The fallback exists for the SignalR hub connect, where the claims transformer has not run and so
    /// nothing has been set as current. Without it, a hub connection could authorize nothing.
    /// </summary>
    [Fact]
    public async Task AuthorizeAsync_WithNoCurrentPrincipal_FallsBackToTheResolver()
    {
        _userClaims.GetCurrentClaimsPrincipal().Returns((ClaimsPrincipal)null);
        _identity.GetClaimsPrincipal().Returns(Principal(SystemPermission.CreateMsels));

        Assert.True(await _service.AuthorizeAsync([SystemPermission.CreateMsels], Ct));

        _identity.Received(1).GetClaimsPrincipal();
    }

    [Fact]
    public async Task AuthorizeAsync_WithTheResolversPrincipalLackingThePermission_IsFalse()
    {
        _userClaims.GetCurrentClaimsPrincipal().Returns((ClaimsPrincipal)null);
        _identity.GetClaimsPrincipal().Returns(Principal(SystemPermission.ViewMsels));

        Assert.False(await _service.AuthorizeAsync([SystemPermission.CreateMsels], Ct));
    }

    /// <summary>
    /// Both sources empty - an anonymous request that got past the pipeline, or a background call - and
    /// the answer is a plain false rather than a throw. <see cref="SystemPermissionHandler"/>'s null-user
    /// branch is what produces it.
    /// </summary>
    [Fact]
    public async Task AuthorizeAsync_WithNoPrincipalAnywhere_IsFalse()
    {
        _userClaims.GetCurrentClaimsPrincipal().Returns((ClaimsPrincipal)null);
        _identity.GetClaimsPrincipal().Returns((ClaimsPrincipal)null);

        Assert.False(await _service.AuthorizeAsync([SystemPermission.CreateMsels], Ct));
    }

    /// <summary>An empty permission array authorizes any signed-in caller.</summary>
    [Fact]
    public async Task AuthorizeAsync_WithNoRequiredPermissions_IsTrueForAnyone()
    {
        _userClaims.GetCurrentClaimsPrincipal().Returns(Principal());

        Assert.True(await _service.AuthorizeAsync([], Ct));
    }

    /// <summary>
    /// Even so, the null-user branch still wins: no caller means no, regardless of the requirement.
    /// </summary>
    [Fact]
    public async Task AuthorizeAsync_WithNoRequiredPermissionsAndNoPrincipal_IsFalse()
    {
        _userClaims.GetCurrentClaimsPrincipal().Returns((ClaimsPrincipal)null);
        _identity.GetClaimsPrincipal().Returns((ClaimsPrincipal)null);

        Assert.False(await _service.AuthorizeAsync([], Ct));
    }

    [Fact]
    public async Task AuthorizeAsync_WithARootActorsClaims_IsTrueForEveryPermission()
    {
        _userClaims.GetCurrentClaimsPrincipal().Returns(Principal(Enum.GetValues<SystemPermission>()));

        foreach (var permission in Enum.GetValues<SystemPermission>())
        {
            Assert.True(await _service.AuthorizeAsync([permission], Ct));
        }
    }

    [Fact]
    public void GetSystemPermissions_ReturnsThePermissionClaims()
    {
        _identity.GetClaimsPrincipal().Returns(
            Principal(SystemPermission.CreateMsels, SystemPermission.ViewMsels));

        Assert.Equal(
            [SystemPermission.CreateMsels, SystemPermission.ViewMsels],
            _service.GetSystemPermissions().Order());
    }

    [Fact]
    public void GetSystemPermissions_WithNoPermissionClaims_IsEmpty()
    {
        _identity.GetClaimsPrincipal().Returns(Principal());

        Assert.Empty(_service.GetSystemPermissions());
    }

    /// <summary>
    /// Claims of other types are ignored rather than misread - the filter is on the claim type, so a
    /// <c>scope</c> or <c>role</c> claim never becomes a permission.
    /// </summary>
    [Fact]
    public void GetSystemPermissions_IgnoresOtherClaimTypes()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("sub", Guid.NewGuid().ToString()),
            new Claim("scope", "blueprint"),
            new Claim(AuthorizationConstants.PermissionClaimType, SystemPermission.CreateMsels.ToString())
        ], "Bearer"));

        _identity.GetClaimsPrincipal().Returns(principal);

        Assert.Equal([SystemPermission.CreateMsels], _service.GetSystemPermissions());
    }

    /// <summary>
    /// A value that is not an enum name is dropped, not thrown on. That matters because the same claim
    /// collection is built from legacy <c>UserPermissions</c> rows whose <c>Key</c> is free text.
    /// </summary>
    [Fact]
    public void GetSystemPermissions_DropsUnparseableValues()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(AuthorizationConstants.PermissionClaimType, "NotAPermission"),
            new Claim(AuthorizationConstants.PermissionClaimType, SystemPermission.ViewMsels.ToString())
        ], "Bearer"));

        _identity.GetClaimsPrincipal().Returns(principal);

        Assert.Equal([SystemPermission.ViewMsels], _service.GetSystemPermissions());
    }

    /// <summary>Get system permissions accepts numeric values outside the enum.</summary>
    [Fact]
    public void GetSystemPermissions_AcceptsNumericValuesOutsideTheEnum()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(AuthorizationConstants.PermissionClaimType, "999")], "Bearer"));

        _identity.GetClaimsPrincipal().Returns(principal);

        Assert.Equal([(SystemPermission)999], _service.GetSystemPermissions());
    }

    /// <summary>A claim value differing from the enum name only in case is dropped.</summary>
    [Fact]
    public void GetSystemPermissions_DropsClaimValuesThatDifferOnlyInCase()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(AuthorizationConstants.PermissionClaimType, "createmsels")], "Bearer"));

        _identity.GetClaimsPrincipal().Returns(principal);

        Assert.Empty(_service.GetSystemPermissions());
    }

    /// <summary>A permission held twice is reported twice.</summary>
    [Fact]
    public void GetSystemPermissions_DoesNotDeduplicate()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(AuthorizationConstants.PermissionClaimType, SystemPermission.CreateMsels.ToString()),
            new Claim(AuthorizationConstants.PermissionClaimType, SystemPermission.CreateMsels.ToString())
        ], "Bearer"));

        _identity.GetClaimsPrincipal().Returns(principal);

        Assert.Equal(2, _service.GetSystemPermissions().Count());
    }

    /// <summary>Get system permissions with no principal throws null reference exception.</summary>
    [Fact]
    public void GetSystemPermissions_WithNoPrincipal_ThrowsNullReferenceException()
    {
        _identity.GetClaimsPrincipal().Returns((ClaimsPrincipal)null);

        Assert.Throws<NullReferenceException>(() => _service.GetSystemPermissions());
    }

    /// <summary>
    /// It reads the resolver, never the claims service - the mirror image of <c>AuthorizeAsync</c>'s
    /// preference, and the reason the two can disagree about who the caller is.
    /// </summary>
    [Fact]
    public void GetSystemPermissions_DoesNotConsultTheClaimsService()
    {
        _identity.GetClaimsPrincipal().Returns(Principal(SystemPermission.CreateMsels));

        _service.GetSystemPermissions().ToArray();

        _userClaims.DidNotReceive().GetCurrentClaimsPrincipal();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ClaimsPrincipal Principal(params SystemPermission[] permissions) =>
        new(new ClaimsIdentity(
        [
            new Claim("sub", Guid.NewGuid().ToString()),
            .. permissions.Select(x =>
                new Claim(AuthorizationConstants.PermissionClaimType, x.ToString()))
        ], "Bearer"));
}
