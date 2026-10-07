// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Blueprint.Api.Data.Enumerations;
using Blueprint.Api.Data.Models;
using Blueprint.Api.Infrastructure.Authorization;
using Blueprint.Api.Infrastructure.Extensions;
using Blueprint.Api.Infrastructure.Options;
using Blueprint.Api.Services;
using Blueprint.Api.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.IdentityModel.JsonWebTokens;
using Xunit;

namespace Blueprint.Api.Tests.Services;

/// <summary>Turns a token into the permission claims every authorization decision is then made from. It is
/// the meatiest unit in the codebase and it writes to the database - it provisions the user row on first
/// sight - so these run against a real one.</summary>
public class UserClaimsServiceTests(DatabaseFixture fixture) : DatabaseTestBase(fixture)
{
    /// <summary>Starts describing an actor to seed over this test's database.</summary>
    private TestActorBuilder Actor() => new(Db, Ct);

    private readonly MemoryCache _cache = new(new MemoryCacheOptions());

    [Fact]
    public async Task AddUserClaims_ForAUserWithNoRole_AddsNoPermissions()
    {
        var actor = await Actor().SeedAsync();

        var principal = await Service().AddUserClaims(Token(actor.Id), update: false);

        Assert.Empty(Permissions(principal));
    }

    [Fact]
    public async Task AddUserClaims_AddsThePermissionsOfTheUsersRole()
    {
        var actor = await Actor()
            .WithSystemPermissions(SystemPermission.CreateMsels, SystemPermission.ViewMsels)
            .SeedAsync();

        var principal = await Service().AddUserClaims(Token(actor.Id), update: false);

        Assert.Equal(
            [SystemPermission.CreateMsels.ToString(), SystemPermission.ViewMsels.ToString()],
            Permissions(principal).Order());
    }

    /// <summary>
    /// <c>AllPermissions</c> is a flag, not a list - the Administrator row seeded by
    /// <c>SystemRoleConfiguration</c> carries an <em>empty</em> <c>Permissions</c> collection - so the
    /// expansion to every enum value happens here and nowhere else. It is also what makes a root actor
    /// free in the harness.
    /// </summary>
    [Fact]
    public async Task AddUserClaims_ExpandsAnAllPermissionsRoleToEveryPermission()
    {
        var actor = await Actor().WithAllSystemPermissions().SeedAsync();

        var principal = await Service().AddUserClaims(Token(actor.Id), update: false);

        Assert.Equal(
            Enum.GetValues<SystemPermission>().Select(x => x.ToString()).Order(),
            Permissions(principal).Order());
    }

    [Fact]
    public async Task AddUserClaims_ForTheSeededContentDeveloperRole_AddsItsFourPermissions()
    {
        var actor = await Actor().WithRole(SystemRoleDefaults.ContentDeveloperRoleId).SeedAsync();

        var principal = await Service().AddUserClaims(Token(actor.Id), update: false);

        Assert.Equal(
            [
                SystemPermission.CreateMsels.ToString(),
                SystemPermission.EditMsels.ToString(),
                SystemPermission.ManageMsels.ToString(),
                SystemPermission.ViewMsels.ToString()
            ],
            Permissions(principal).Order());
    }

    [Fact]
    public async Task AddUserClaims_ForTheSeededObserverRole_AddsOnlyTheViewPermissions()
    {
        var actor = await Actor().WithRole(SystemRoleDefaults.ObserverRoleId).SeedAsync();

        var principal = await Service().AddUserClaims(Token(actor.Id), update: false);

        Assert.All(Permissions(principal), x => Assert.StartsWith("View", x));
        Assert.NotEmpty(Permissions(principal));
    }

    /// <summary>
    /// A role with an empty permission list and no <c>AllPermissions</c> grants nothing. Worth pinning
    /// because the null-coalescing on <c>role.Permissions</c> makes an empty list and a null one behave
    /// the same, and a role is easy to create without noticing which one it has.
    /// </summary>
    [Fact]
    public async Task AddUserClaims_ForARoleWithNoPermissions_AddsNone()
    {
        var actor = await Actor().WithSystemPermissions().SeedAsync();

        var principal = await Service().AddUserClaims(Token(actor.Id), update: false);

        Assert.Empty(Permissions(principal));
    }

    /// <summary>
    /// The <c>jti</c> is carried into the claim set so a later call can tell one token from another. It is
    /// the only claim copied off the incoming token.
    /// </summary>
    [Fact]
    public async Task AddUserClaims_CarriesTheTokenIdIntoTheClaims()
    {
        var actor = await Actor().WithAllSystemPermissions().SeedAsync();

        var principal = await Service().AddUserClaims(Token(actor.Id, jti: "token-1"), update: false);

        Assert.Equal("token-1", principal.FindFirst(JwtRegisteredClaimNames.Jti).Value);
    }

    [Fact]
    public async Task AddUserClaims_ReturnsTheSamePrincipalItWasGiven()
    {
        var actor = await Actor().WithAllSystemPermissions().SeedAsync();
        var token = Token(actor.Id);

        Assert.Same(token, await Service().AddUserClaims(token, update: false));
    }

    /// <summary>
    /// First login. Nothing in the codebase creates a user row except this - there is no user-provisioning
    /// endpoint - so an unknown subject in a valid token becomes a user here.
    /// </summary>
    [Fact]
    public async Task AddUserClaims_WithUpdate_ProvisionsAnUnknownUser()
    {
        var userId = Guid.NewGuid();

        await Service().AddUserClaims(Token(userId, name: "New Person"), update: true);

        await using var context = NewContext();
        var stored = await context.Users.SingleAsync(x => x.Id == userId, Ct);

        Assert.Equal("New Person", stored.Name);
        Assert.Null(stored.RoleId);
        Assert.Equal(userId, stored.CreatedBy);
    }

    /// <summary>
    /// A token with no <c>name</c> claim - a service account, or an IdP mapper that was never configured -
    /// still gets a row, under a placeholder.
    /// </summary>
    [Fact]
    public async Task AddUserClaims_WithUpdateAndNoNameClaim_ProvisionsThemAsAnonymous()
    {
        var userId = Guid.NewGuid();

        await Service().AddUserClaims(Token(userId), update: true);

        await using var context = NewContext();

        Assert.Equal("Anonymous", (await context.Users.SingleAsync(x => x.Id == userId, Ct)).Name);
    }

    /// <summary>
    /// A provisioned user has no role, so they authenticate and are then refused by every endpoint. That is
    /// the 403-not-401 case the harness relies on.
    /// </summary>
    [Fact]
    public async Task AddUserClaims_AProvisionedUser_HasNoPermissions()
    {
        var principal = await Service().AddUserClaims(Token(Guid.NewGuid()), update: true);

        Assert.Empty(Permissions(principal));
    }

    /// <summary>
    /// The user's display name follows the IdP on every request, which is how a rename in Keycloak reaches
    /// blueprint's own tables.
    /// </summary>
    [Fact]
    public async Task AddUserClaims_WithUpdate_WritesBackAChangedName()
    {
        var actor = await Actor().WithName("Old Name").SeedAsync();

        await Service().AddUserClaims(Token(actor.Id, name: "New Name"), update: true);

        await using var context = NewContext();

        Assert.Equal("New Name", (await context.Users.SingleAsync(x => x.Id == actor.Id, Ct)).Name);
    }

    /// <summary>
    /// A token with no name does not blank the stored one.
    /// </summary>
    [Fact]
    public async Task AddUserClaims_WithUpdateAndNoNameClaim_LeavesAnExistingNameAlone()
    {
        var actor = await Actor().WithName("Old Name").SeedAsync();

        await Service().AddUserClaims(Token(actor.Id), update: true);

        await using var context = NewContext();

        Assert.Equal("Old Name", (await context.Users.SingleAsync(x => x.Id == actor.Id, Ct)).Name);
    }

    /// <summary>With <c>update: false</c> an unknown subject's principal comes back unchanged and no row is
    /// created.</summary>
    [Fact]
    public async Task AddUserClaims_WithoutUpdate_LeavesAnUnknownUsersTokenUntouched()
    {
        var userId = Guid.NewGuid();
        var token = Token(userId, name: "Nobody", jti: "token-1");
        var before = token.Claims.Count();

        var principal = await Service().AddUserClaims(token, update: false);

        Assert.Empty(Permissions(principal));
        Assert.Equal(before, principal.Claims.Count());

        await using var context = NewContext();
        Assert.False(await context.Users.AnyAsync(x => x.Id == userId, Ct));
    }

    /// <summary>Add user claims when provisioning the user fails says nothing and carries on.</summary>
    [Fact]
    public async Task AddUserClaims_WhenProvisioningTheUserFails_SaysNothingAndCarriesOn()
    {
        var userId = Guid.NewGuid();

        var principal = await Service().AddUserClaims(Token(userId, name: "Bad\0Name"), update: true);

        Assert.Equal(userId, principal.GetId());

        await using var context = NewContext();
        Assert.False(await context.Users.AnyAsync(x => x.Id == userId, Ct));
    }

    [Fact]
    public async Task AddUserClaims_WithCachingDisabled_ReflectsARoleChangeImmediately()
    {
        var actor = await Actor().SeedAsync();
        var service = Service();

        Assert.Empty(Permissions(await service.AddUserClaims(Token(actor.Id), update: false)));

        await GiveRole(actor.Id, SystemRoleDefaults.AdministratorRoleId);

        Assert.NotEmpty(Permissions(await Service().AddUserClaims(Token(actor.Id), update: false)));
    }

    /// <summary>
    /// With caching on, a second service instance - a second request - is served from the cache, which is
    /// the point: the claim build is four queries.
    /// </summary>
    [Fact]
    public async Task AddUserClaims_WithCachingEnabled_ServesASecondRequestFromTheCache()
    {
        var actor = await Actor().WithAllSystemPermissions().SeedAsync();

        await Service(Caching()).AddUserClaims(Token(actor.Id), update: false);
        await GiveRole(actor.Id, null);

        var principal = await Service(Caching()).AddUserClaims(Token(actor.Id), update: false);

        Assert.NotEmpty(Permissions(principal));
    }

    /// <summary>Add user claims with caching and no id p roles ignores a new token id.</summary>
    [Fact]
    public async Task AddUserClaims_WithCachingAndNoIdPRoles_IgnoresANewTokenId()
    {
        var actor = await Actor().WithAllSystemPermissions().SeedAsync();

        await Service(Caching()).AddUserClaims(Token(actor.Id, jti: "token-1"), update: false);
        await GiveRole(actor.Id, null);

        var principal = await Service(Caching())
            .AddUserClaims(Token(actor.Id, jti: "token-2"), update: false);

        Assert.NotEmpty(Permissions(principal));
    }

    /// <summary>
    /// With IdP roles in play the guard is satisfied, and a new token does rebuild the claims. This is the
    /// behaviour the shipped configuration misses.
    /// </summary>
    [Fact]
    public async Task AddUserClaims_WithCachingAndIdPRoles_RebuildsOnANewTokenId()
    {
        var actor = await Actor().WithAllSystemPermissions().SeedAsync();
        var options = Caching();
        options.UseRolesFromIdP = true;

        await Service(options).AddUserClaims(Token(actor.Id, jti: "token-1"), update: false);
        await GiveRole(actor.Id, null);

        var principal = await Service(options)
            .AddUserClaims(Token(actor.Id, jti: "token-2"), update: false);

        Assert.Empty(Permissions(principal));
    }

    [Fact]
    public async Task AddUserClaims_WithCachingAndIdPRoles_KeepsTheCacheForTheSameTokenId()
    {
        var actor = await Actor().WithAllSystemPermissions().SeedAsync();
        var options = Caching();
        options.UseRolesFromIdP = true;

        await Service(options).AddUserClaims(Token(actor.Id, jti: "token-1"), update: false);
        await GiveRole(actor.Id, null);

        var principal = await Service(options)
            .AddUserClaims(Token(actor.Id, jti: "token-1"), update: false);

        Assert.NotEmpty(Permissions(principal));
    }

    /// <summary>
    /// The cache is keyed by user, so one user's claims are never handed to another. Trivial, and the kind
    /// of thing that must not regress silently.
    /// </summary>
    [Fact]
    public async Task AddUserClaims_CachesPerUser()
    {
        var privileged = await Actor().WithAllSystemPermissions().SeedAsync();
        var unprivileged = await Actor().SeedAsync();

        await Service(Caching()).AddUserClaims(Token(privileged.Id), update: false);
        var principal = await Service(Caching()).AddUserClaims(Token(unprivileged.Id), update: false);

        Assert.Empty(Permissions(principal));
    }

    /// <summary>
    /// An unknown user is not cached - the caching lives inside the <c>user != null</c> branch - so the
    /// claims are rebuilt once the row exists.
    /// </summary>
    [Fact]
    public async Task AddUserClaims_DoesNotCacheTheEmptyClaimsOfAnUnknownUser()
    {
        var userId = Guid.NewGuid();

        await Service(Caching()).AddUserClaims(Token(userId), update: false);
        await Actor().WithId(userId).WithAllSystemPermissions().SeedAsync();

        var principal = await Service(Caching()).AddUserClaims(Token(userId), update: false);

        Assert.NotEmpty(Permissions(principal));
    }

    [Fact]
    public async Task RefreshClaims_DropsTheCachedClaims()
    {
        var actor = await Actor().WithAllSystemPermissions().SeedAsync();
        var service = Service(Caching());
        service.SetCurrentClaimsPrincipal(Token(Guid.NewGuid()));

        await service.AddUserClaims(Token(actor.Id), update: false);
        await GiveRole(actor.Id, null);

        Assert.Empty(Permissions(await service.RefreshClaims(actor.Id)));
    }

    /// <summary>
    /// It is the only way to drop them: nothing else calls <c>_cache.Remove</c>, so a permission change
    /// made through the API must run through here to take effect before the entry expires.
    /// </summary>
    [Fact]
    public async Task RefreshClaims_ReturnsTheRebuiltPrincipal()
    {
        var actor = await Actor().SeedAsync();
        var service = Service(Caching());
        service.SetCurrentClaimsPrincipal(Token(Guid.NewGuid()));

        await service.AddUserClaims(Token(actor.Id), update: false);
        await GiveRole(actor.Id, SystemRoleDefaults.ObserverRoleId);

        Assert.NotEmpty(Permissions(await service.RefreshClaims(actor.Id)));
    }

    /// <summary>Refresh claims with no current principal throws null reference exception.</summary>
    [Fact]
    public async Task RefreshClaims_WithNoCurrentPrincipal_ThrowsNullReferenceException()
    {
        var actor = await Actor().SeedAsync();

        await Assert.ThrowsAsync<NullReferenceException>(() => Service().RefreshClaims(actor.Id));
    }

    [Fact]
    public async Task GetClaimsPrincipal_BuildsAPrincipalCarryingTheSubjectAndPermissions()
    {
        var actor = await Actor().WithRole(SystemRoleDefaults.ObserverRoleId).SeedAsync();

        var principal = await Service().GetClaimsPrincipal(actor.Id, setAsCurrent: true);

        Assert.Equal(actor.Id, principal.GetId());
        Assert.NotEmpty(Permissions(principal));
    }

    [Fact]
    public async Task GetClaimsPrincipal_WithSetAsCurrent_BecomesTheCurrentPrincipal()
    {
        var actor = await Actor().SeedAsync();
        var service = Service();

        var principal = await service.GetClaimsPrincipal(actor.Id, setAsCurrent: true);

        Assert.Same(principal, service.GetCurrentClaimsPrincipal());
    }

    /// <summary>
    /// Building another user's principal - which is what the notification paths do - must not replace the
    /// caller's own.
    /// </summary>
    [Fact]
    public async Task GetClaimsPrincipal_ForAnotherUser_LeavesTheCurrentPrincipalAlone()
    {
        var caller = await Actor().SeedAsync();
        var other = await Actor().SeedAsync();

        var service = Service();
        var callerPrincipal = Token(caller.Id);
        service.SetCurrentClaimsPrincipal(callerPrincipal);

        await service.GetClaimsPrincipal(other.Id, setAsCurrent: false);

        Assert.Same(callerPrincipal, service.GetCurrentClaimsPrincipal());
    }

    /// <summary>
    /// Rebuilding the <em>caller's</em> principal does replace it, even without asking - which is how a
    /// permission change made by the caller to themselves takes effect for the rest of the request.
    /// </summary>
    [Fact]
    public async Task GetClaimsPrincipal_ForTheCurrentUser_ReplacesTheCurrentPrincipal()
    {
        var actor = await Actor().WithAllSystemPermissions().SeedAsync();

        var service = Service();
        service.SetCurrentClaimsPrincipal(Token(actor.Id));

        var principal = await service.GetClaimsPrincipal(actor.Id, setAsCurrent: false);

        Assert.Same(principal, service.GetCurrentClaimsPrincipal());
    }

    /// <summary>
    /// It never provisions. <c>GetClaimsPrincipal</c> passes <c>update: false</c>, so this path cannot
    /// create a user row - only the claims transformer's <c>update: true</c> can.
    /// </summary>
    [Fact]
    public async Task GetClaimsPrincipal_ForAnUnknownUser_ProvisionsNothing()
    {
        var userId = Guid.NewGuid();
        var service = Service();
        service.SetCurrentClaimsPrincipal(Token(Guid.NewGuid()));

        var principal = await service.GetClaimsPrincipal(userId, setAsCurrent: false);

        Assert.Equal(userId, principal.GetId());
        Assert.Empty(Permissions(principal));

        await using var context = NewContext();
        Assert.False(await context.Users.AnyAsync(x => x.Id == userId, Ct));
    }

    /// <summary>Without <c>setAsCurrent</c> and with no current principal the call throws.</summary>
    [Fact]
    public async Task GetClaimsPrincipal_WithoutSetAsCurrentAndNoCurrentPrincipal_Throws()
    {
        var actor = await Actor().SeedAsync();

        await Assert.ThrowsAsync<NullReferenceException>(
            () => Service().GetClaimsPrincipal(actor.Id, setAsCurrent: false));
    }

    /// <summary>
    /// <c>setAsCurrent: true</c> short-circuits the comparison, so it is safe with no current principal.
    /// That is why the claims transformer's path works and the others do not.
    /// </summary>
    [Fact]
    public async Task GetClaimsPrincipal_WithSetAsCurrent_DoesNotNeedACurrentPrincipal()
    {
        var actor = await Actor().SeedAsync();

        Assert.NotNull(await Service().GetClaimsPrincipal(actor.Id, setAsCurrent: true));
    }

    [Fact]
    public void GetCurrentClaimsPrincipal_BeforeAnythingSetsIt_IsNull()
    {
        Assert.Null(Service().GetCurrentClaimsPrincipal());
    }

    [Fact]
    public void SetCurrentClaimsPrincipal_IsWhatGetCurrentClaimsPrincipalReturns()
    {
        var principal = Token(Guid.NewGuid());
        var service = Service();

        service.SetCurrentClaimsPrincipal(principal);

        Assert.Same(principal, service.GetCurrentClaimsPrincipal());
    }

    /// <summary>Add user claims called twice on one identity adds no new permissions.</summary>
    [Fact]
    public async Task AddUserClaims_CalledTwiceOnOneIdentity_AddsNoNewPermissions()
    {
        var actor = await Actor().WithRole(SystemRoleDefaults.ContentDeveloperRoleId).SeedAsync();
        var token = Token(actor.Id);

        await Service().AddUserClaims(token, update: false);
        var before = Permissions(token).Order().ToArray();

        await GiveRole(actor.Id, SystemRoleDefaults.AdministratorRoleId);
        await Service().AddUserClaims(token, update: false);

        Assert.Equal(before, Permissions(token).Order());
    }

    /// <summary>
    /// The same filter is what stops the <c>jti</c> being duplicated onto a token that already carries one.
    /// </summary>
    [Fact]
    public async Task AddUserClaims_DoesNotDuplicateTheTokenIdAlreadyOnTheIdentity()
    {
        var actor = await Actor().WithAllSystemPermissions().SeedAsync();

        var principal = await Service().AddUserClaims(Token(actor.Id, jti: "token-1"), update: false);

        Assert.Single(principal.Claims, x => x.Type == JwtRegisteredClaimNames.Jti);
    }

    /// <summary>Add user claims a legacy user permission grants no system permission.</summary>
    [Fact]
    public async Task AddUserClaims_ALegacyUserPermission_GrantsNoSystemPermission()
    {
        var actor = await Actor().SeedAsync();
        await GiveLegacyPermission(actor.Id, "SystemAdmin");

        var principal = await Service().AddUserClaims(Token(actor.Id), update: false);

        Assert.Empty(Permissions(principal));
        Assert.Equal("true", principal.FindFirst(BlueprintClaimTypes.SystemAdmin.ToString()).Value);
    }

    [Fact]
    public async Task AddUserClaims_ALegacyPermissionKeyThatIsNotAClaimType_IsIgnored()
    {
        var actor = await Actor().SeedAsync();
        await GiveLegacyPermission(actor.Id, "NotAClaimType");

        var principal = await Service().AddUserClaims(Token(actor.Id), update: false);

        Assert.Single(principal.Claims);
        Assert.Equal("sub", principal.Claims.Single().Type);
    }

    /// <summary>A legacy key differing only in case is dropped.</summary>
    [Fact]
    public async Task AddUserClaims_ALegacyPermissionKeyIsCaseSensitive()
    {
        var actor = await Actor().SeedAsync();
        await GiveLegacyPermission(actor.Id, "systemadmin");

        var principal = await Service().AddUserClaims(Token(actor.Id), update: false);

        Assert.Null(principal.FindFirst(BlueprintClaimTypes.SystemAdmin.ToString()));
    }

    /// <summary>A numeric legacy key is parsed as the enum value of that number.</summary>
    [Fact]
    public async Task AddUserClaims_ALegacyPermissionKeyMayBeANumber()
    {
        var actor = await Actor().SeedAsync();
        await GiveLegacyPermission(actor.Id, "1");

        var principal = await Service().AddUserClaims(Token(actor.Id), update: false);

        Assert.Equal("true", principal.FindFirst(BlueprintClaimTypes.ContentDeveloper.ToString()).Value);
    }

    [Fact]
    public async Task AddUserClaims_ALegacyPermissionAndARole_YieldBoth()
    {
        var actor = await Actor().WithRole(SystemRoleDefaults.ObserverRoleId).SeedAsync();
        await GiveLegacyPermission(actor.Id, "BaseUser");

        var principal = await Service().AddUserClaims(Token(actor.Id), update: false);

        Assert.NotEmpty(Permissions(principal));
        Assert.NotNull(principal.FindFirst(BlueprintClaimTypes.BaseUser.ToString()));
    }

    /// <summary>
    /// Roles named in the token are matched against <c>SystemRoles.Name</c> case-insensitively, which is
    /// what lets a Keycloak realm role called <c>observer</c> line up with the seeded <c>Observer</c> row.
    /// </summary>
    [Fact]
    public async Task AddUserClaims_WithIdPRoles_GrantsAMatchingSystemRolesPermissions()
    {
        var actor = await Actor().SeedAsync();
        var options = NoCaching();
        options.UseRolesFromIdP = true;
        options.RolesClaimPath = "realm_access.roles";

        var token = Token(actor.Id, extra: Json("realm_access", """{"roles":["observer"]}"""));
        var principal = await Service(options).AddUserClaims(token, update: false);

        Assert.All(Permissions(principal), x => Assert.StartsWith("View", x));
        Assert.NotEmpty(Permissions(principal));
    }

    /// <summary>
    /// A token role naming nothing in the database grants nothing - roles are not created on sight, unlike
    /// users.
    /// </summary>
    [Fact]
    public async Task AddUserClaims_WithAnIdPRoleThatMatchesNoSystemRole_GrantsNothing()
    {
        var actor = await Actor().SeedAsync();
        var options = NoCaching();
        options.UseRolesFromIdP = true;
        options.RolesClaimPath = "realm_access.roles";

        var token = Token(actor.Id, extra: Json("realm_access", """{"roles":["not-a-role"]}"""));

        Assert.Empty(Permissions(await Service(options).AddUserClaims(token, update: false)));
    }

    /// <summary>
    /// An IdP role and an assigned role are unioned, and the permission claims are de-duplicated by value
    /// as they are built - so overlapping roles do not produce repeated claims.
    /// </summary>
    [Fact]
    public async Task AddUserClaims_UnionsTheIdPRoleWithTheAssignedRoleWithoutDuplicating()
    {
        var actor = await Actor().WithRole(SystemRoleDefaults.ObserverRoleId).SeedAsync();
        var options = NoCaching();
        options.UseRolesFromIdP = true;
        options.RolesClaimPath = "realm_access.roles";

        var token = Token(actor.Id, extra: Json("realm_access", """{"roles":["Observer","Content Developer"]}"""));
        var permissions = Permissions(await Service(options).AddUserClaims(token, update: false));

        Assert.Distinct(permissions);
        Assert.Contains(SystemPermission.CreateMsels.ToString(), permissions);
        Assert.Contains(SystemPermission.ViewMsels.ToString(), permissions);
    }

    /// <summary>
    /// Turning IdP roles off makes the token's roles inert even when they are present, so the flag is what
    /// decides whether the IdP can grant blueprint permissions at all.
    /// </summary>
    [Fact]
    public async Task AddUserClaims_WithIdPRolesDisabled_IgnoresTheTokensRoles()
    {
        var actor = await Actor().SeedAsync();
        var options = NoCaching();
        options.RolesClaimPath = "realm_access.roles";

        var token = Token(actor.Id, extra: Json("realm_access", """{"roles":["Administrator"]}"""));

        Assert.Empty(Permissions(await Service(options).AddUserClaims(token, update: false)));
    }

    [Fact]
    public async Task AddUserClaims_WithNoRolesClaimPath_IgnoresTheTokensRoles()
    {
        var actor = await Actor().SeedAsync();
        var options = NoCaching();
        options.UseRolesFromIdP = true;

        var token = Token(actor.Id, extra: Json("realm_access", """{"roles":["Administrator"]}"""));

        Assert.Empty(Permissions(await Service(options).AddUserClaims(token, update: false)));
    }

    /// <summary>Add user claims for a string claim ignores the rest of the claim path.</summary>
    [Fact]
    public async Task AddUserClaims_ForAStringClaim_IgnoresTheRestOfTheClaimPath()
    {
        var actor = await Actor().SeedAsync();
        var options = NoCaching();
        options.UseRolesFromIdP = true;
        options.RolesClaimPath = "realm_access.roles";

        var token = Token(actor.Id, extra: new Claim("realm_access", "Administrator"));
        var permissions = Permissions(await Service(options).AddUserClaims(token, update: false));

        Assert.Equal(Enum.GetValues<SystemPermission>().Length, permissions.Length);
    }

    /// <summary>Add user claims for an unparseable JSON claim grants nothing silently.</summary>
    [Fact]
    public async Task AddUserClaims_ForAnUnparseableJsonClaim_GrantsNothingSilently()
    {
        var actor = await Actor().SeedAsync();
        var options = NoCaching();
        options.UseRolesFromIdP = true;
        options.RolesClaimPath = "realm_access.roles";

        var token = Token(actor.Id, extra: Json("realm_access", "{not json"));

        Assert.Empty(Permissions(await Service(options).AddUserClaims(token, update: false)));
    }

    [Fact]
    public async Task AddUserClaims_ForAJsonClaimMissingThePathSegment_GrantsNothing()
    {
        var actor = await Actor().SeedAsync();
        var options = NoCaching();
        options.UseRolesFromIdP = true;
        options.RolesClaimPath = "realm_access.roles";

        var token = Token(actor.Id, extra: Json("realm_access", """{"groups":["Administrator"]}"""));

        Assert.Empty(Permissions(await Service(options).AddUserClaims(token, update: false)));
    }

    /// <summary>
    /// A single JSON string, rather than an array, is accepted as one role - which some IdPs emit for a
    /// user with exactly one.
    /// </summary>
    [Fact]
    public async Task AddUserClaims_AcceptsAJsonClaimWhoseValueIsASingleString()
    {
        var actor = await Actor().SeedAsync();
        var options = NoCaching();
        options.UseRolesFromIdP = true;
        options.RolesClaimPath = "realm_access.roles";

        var token = Token(actor.Id, extra: Json("realm_access", """{"roles":"Observer"}"""));

        Assert.NotEmpty(Permissions(await Service(options).AddUserClaims(token, update: false)));
    }

    /// <summary>
    /// Non-string entries in the array are skipped rather than throwing, so a mixed array still yields its
    /// usable names.
    /// </summary>
    [Fact]
    public async Task AddUserClaims_SkipsNonStringEntriesInAJsonRoleArray()
    {
        var actor = await Actor().SeedAsync();
        var options = NoCaching();
        options.UseRolesFromIdP = true;
        options.RolesClaimPath = "realm_access.roles";

        var token = Token(actor.Id, extra: Json("realm_access", """{"roles":[1,null,"Observer"]}"""));

        Assert.NotEmpty(Permissions(await Service(options).AddUserClaims(token, update: false)));
    }

    /// <summary>
    /// A claim type containing a literal dot is reachable by escaping it - <c>realm\.access</c> is one
    /// segment, not two. That is what the negative lookbehind in the path split is for.
    /// </summary>
    [Fact]
    public async Task AddUserClaims_TreatsAnEscapedDotAsPartOfTheClaimName()
    {
        var actor = await Actor().SeedAsync();
        var options = NoCaching();
        options.UseRolesFromIdP = true;
        options.RolesClaimPath = @"realm\.access";

        var token = Token(actor.Id, extra: new Claim("realm.access", "Observer"));

        Assert.NotEmpty(Permissions(await Service(options).AddUserClaims(token, update: false)));
    }

    /// <summary>
    /// A deeper path is walked segment by segment.
    /// </summary>
    [Fact]
    public async Task AddUserClaims_WalksANestedJsonPath()
    {
        var actor = await Actor().SeedAsync();
        var options = NoCaching();
        options.UseRolesFromIdP = true;
        options.RolesClaimPath = "resource_access.blueprint.roles";

        var token = Token(actor.Id, extra: Json(
            "resource_access", """{"blueprint":{"roles":["Observer"]}}"""));

        Assert.NotEmpty(Permissions(await Service(options).AddUserClaims(token, update: false)));
    }

    /// <summary>Add user claims group membership grants nothing.</summary>
    [Fact]
    public async Task AddUserClaims_GroupMembershipGrantsNothing()
    {
        var actor = await Actor().SeedAsync();
        var group = new GroupEntity { Id = Guid.NewGuid(), Name = "Analysts" };
        await Seed(group);
        await Seed(new GroupMembershipEntity(group.Id, actor.Id));

        var principal = await Service().AddUserClaims(Token(actor.Id), update: false);

        Assert.Single(principal.Claims);
    }

    /// <summary>A group named in the token grants nothing.</summary>
    [Fact]
    public async Task AddUserClaims_AnIdPGroupGrantsNothing()
    {
        var actor = await Actor().SeedAsync();
        await Seed(new GroupEntity { Id = Guid.NewGuid(), Name = "Analysts" });

        var options = NoCaching();
        options.UseGroupsFromIdP = true;
        options.GroupsClaimPath = "groups";

        var token = Token(actor.Id, extra: new Claim("groups", "Analysts"));

        Assert.Empty(Permissions(await Service(options).AddUserClaims(token, update: false)));
    }

    private UserClaimsService Service(ClaimsTransformationOptions options = null) =>
        new(Db, _cache, options ?? NoCaching());

    /// <summary>What the harness configures, and what blueprint ships bar the caching.</summary>
    private static ClaimsTransformationOptions NoCaching() =>
        new() { EnableCaching = false, CacheExpirationSeconds = 60 };

    private static ClaimsTransformationOptions Caching() =>
        new() { EnableCaching = true, CacheExpirationSeconds = 60 };

    /// <summary>
    /// A principal shaped like one the JWT handler produces: a <c>sub</c>, optionally a <c>name</c> and a
    /// <c>jti</c>, plus whatever the test needs.
    /// </summary>
    private static ClaimsPrincipal Token(
        Guid userId, string name = null, string jti = null, params Claim[] extra)
    {
        var identity = new ClaimsIdentity([new Claim("sub", userId.ToString())], "Bearer");

        if (name is not null)
        {
            identity.AddClaim(new Claim("name", name));
        }

        if (jti is not null)
        {
            identity.AddClaim(new Claim(JwtRegisteredClaimNames.Jti, jti));
        }

        identity.AddClaims(extra);

        return new ClaimsPrincipal(identity);
    }

    /// <summary>
    /// A claim carrying a JSON document, as the JWT handler produces for a nested token claim. The
    /// <c>"JSON"</c> value type is what makes <c>GetClaimsFromToken</c> walk the path rather than return
    /// the raw string.
    /// </summary>
    private static Claim Json(string type, string value) => new(type, value, "JSON");

    private static string[] Permissions(ClaimsPrincipal principal) =>
    [
        .. principal.Claims
            .Where(x => x.Type == AuthorizationConstants.PermissionClaimType)
            .Select(x => x.Value)
    ];

    private async Task GiveRole(Guid userId, Guid? roleId)
    {
        var user = await Db.Users.SingleAsync(x => x.Id == userId, Ct);
        user.RoleId = roleId;
        await Db.SaveChangesAsync(Ct);

        // The service shares this context, so the role navigation loaded by an earlier claim build would
        // otherwise be served from the change tracker.
        Db.ChangeTracker.Clear();
    }

    private async Task GiveLegacyPermission(Guid userId, string key)
    {
        var permission = new PermissionEntity
        {
            Id = Guid.NewGuid(),
            Key = key,
            Value = "true",
            Description = "Legacy",
            CreatedBy = userId
        };

        await Seed(permission);
        await Seed(new UserPermissionEntity(userId, permission.Id));
    }
}
