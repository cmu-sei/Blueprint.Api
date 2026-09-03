// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Blueprint.Api.Data.Enumerations;
using Blueprint.Api.Infrastructure.Authorization;
using Microsoft.AspNetCore.Authorization;
using Xunit;

namespace Blueprint.Api.Tests;

/// <summary>
/// The one handler behind every <c>SystemPermission</c> check in every controller. It is 15 lines and
/// has four branches, one of which succeeds without checking anything.
/// </summary>
public class SystemPermissionHandlerTests
{
    [Fact]
    public async Task Succeeds_WhenTheUserHoldsTheRequiredPermission()
    {
        var context = Context(
            Principal(SystemPermission.CreateMsels),
            SystemPermission.CreateMsels);

        await new SystemPermissionHandler().HandleAsync(context);

        Assert.True(context.HasSucceeded);
    }

    [Fact]
    public async Task Fails_WhenTheUserHoldsNoPermissions()
    {
        var context = Context(Principal(), SystemPermission.CreateMsels);

        await new SystemPermissionHandler().HandleAsync(context);

        Assert.False(context.HasSucceeded);
    }

    [Fact]
    public async Task Fails_WhenTheUserHoldsADifferentPermission()
    {
        var context = Context(
            Principal(SystemPermission.ViewMsels),
            SystemPermission.CreateMsels);

        await new SystemPermissionHandler().HandleAsync(context);

        Assert.False(context.HasSucceeded);
    }

    /// <summary>
    /// Any, not all. Controllers pass several permissions when more than one role should reach an
    /// action, so an actor holding just one of them must get through.
    /// </summary>
    [Fact]
    public async Task SeveralRequiredPermissions_AreSatisfiedByAnyOne()
    {
        var context = Context(
            Principal(SystemPermission.ManageOrganizations),
            SystemPermission.CreateMsels,
            SystemPermission.ManageOrganizations,
            SystemPermission.ViewMsels);

        await new SystemPermissionHandler().HandleAsync(context);

        Assert.True(context.HasSucceeded);
    }

    [Fact]
    public async Task Fails_WhenTheUserHoldsNoneOfSeveralRequiredPermissions()
    {
        var context = Context(
            Principal(SystemPermission.ViewMsels),
            SystemPermission.CreateMsels,
            SystemPermission.ManageOrganizations);

        await new SystemPermissionHandler().HandleAsync(context);

        Assert.False(context.HasSucceeded);
    }

    [Fact]
    public async Task Fails_WhenTheUserIsNull()
    {
        var context = Context(null, SystemPermission.CreateMsels);

        await new SystemPermissionHandler().HandleAsync(context);

        Assert.False(context.HasSucceeded);
        Assert.True(context.HasFailed);
    }

    /// <remarks>
    /// Documented on purpose, because it is the branch that decides what an *unconditional*
    /// <c>[Authorize]</c>-only action means: an empty requirement succeeds for anyone authenticated. Every
    /// controller that means "any signed-in user" relies on it, so it is deliberate rather than a defect -
    /// but a caller that computed its permission array and got an empty one would be silently allowed
    /// through, which is why it is pinned here.
    /// </remarks>
    [Fact]
    public async Task AnEmptyRequirement_SucceedsForAnyUser()
    {
        var context = Context(Principal());

        await new SystemPermissionHandler().HandleAsync(context);

        Assert.True(context.HasSucceeded);
    }

    /// <remarks>
    /// Same branch as above, reached by <c>null</c> rather than an empty array - which is what
    /// <c>new SystemPermissionRequirement(null)</c> produces.
    /// </remarks>
    [Fact]
    public async Task ANullRequirementList_SucceedsForAnyUser()
    {
        var requirement = new SystemPermissionRequirement(null);
        var context = new AuthorizationHandlerContext([requirement], Principal(), null);

        await new SystemPermissionHandler().HandleAsync(context);

        Assert.True(context.HasSucceeded);
    }

    /// <summary>
    /// The null-user check runs first, so an empty requirement does not let an absent principal through.
    /// </summary>
    [Fact]
    public async Task ANullUser_FailsEvenForAnEmptyRequirement()
    {
        var context = Context(null);

        await new SystemPermissionHandler().HandleAsync(context);

        Assert.False(context.HasSucceeded);
        Assert.True(context.HasFailed);
    }

    /// <summary>
    /// The claim value is the enum's *name*: <c>UserClaimsService</c> writes
    /// <c>permission.ToString()</c> and the handler compares against <c>p.ToString()</c>. A numeric
    /// claim value - what a hand-rolled token or a changed serializer would produce - matches nothing.
    /// </summary>
    [Fact]
    public async Task ANumericPermissionClaim_DoesNotMatch()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(AuthorizationConstants.PermissionClaimType, ((int)SystemPermission.CreateMsels).ToString())],
            "Bearer"));

        var context = Context(principal, SystemPermission.CreateMsels);

        await new SystemPermissionHandler().HandleAsync(context);

        Assert.False(context.HasSucceeded);
    }

    /// <summary>
    /// Claim matching is case-sensitive, because <c>HasClaim</c>'s value comparison is ordinal. An IdP
    /// that lower-cased its claim values would silently grant nothing.
    /// </summary>
    [Fact]
    public async Task PermissionClaimValuesAreCaseSensitive()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(AuthorizationConstants.PermissionClaimType, "createmsels")],
            "Bearer"));

        var context = Context(principal, SystemPermission.CreateMsels);

        await new SystemPermissionHandler().HandleAsync(context);

        Assert.False(context.HasSucceeded);
    }

    /// <summary>
    /// The claim *type* matters as much as the value. A permission carried under any other type - a role
    /// claim, say - is not a permission.
    /// </summary>
    [Fact]
    public async Task APermissionUnderAnotherClaimType_DoesNotMatch()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.Role, SystemPermission.CreateMsels.ToString())],
            "Bearer"));

        var context = Context(principal, SystemPermission.CreateMsels);

        await new SystemPermissionHandler().HandleAsync(context);

        Assert.False(context.HasSucceeded);
    }

    /// <summary>
    /// Every value of the enum behaves the same way. This is the sweep that would catch a permission
    /// whose name collides or whose <c>ToString</c> does not round-trip.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllPermissions))]
    public async Task EveryPermission_IsMatchedByItsOwnClaim(SystemPermission permission)
    {
        var context = Context(Principal(permission), permission);

        await new SystemPermissionHandler().HandleAsync(context);

        Assert.True(context.HasSucceeded);
    }

    public static TheoryData<SystemPermission> AllPermissions()
    {
        var data = new TheoryData<SystemPermission>();

        foreach (var permission in Enum.GetValues<SystemPermission>())
        {
            data.Add(permission);
        }

        return data;
    }

    private static AuthorizationHandlerContext Context(
        ClaimsPrincipal user, params SystemPermission[] required) =>
        new([new SystemPermissionRequirement(required)], user, null);

    private static ClaimsPrincipal Principal(params SystemPermission[] permissions) =>
        new(new ClaimsIdentity(
            permissions.Select(x =>
                new Claim(AuthorizationConstants.PermissionClaimType, x.ToString())).ToList(),
            "Bearer"));
}
