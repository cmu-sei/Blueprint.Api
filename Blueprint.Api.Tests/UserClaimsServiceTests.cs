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
using Blueprint.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.IdentityModel.JsonWebTokens;
using Xunit;

namespace Blueprint.Api.Tests;

/// <summary>
/// Turns a token into the permission claims every authorization decision is then made from. It is the
/// meatiest unit in the codebase and it writes to the database - it provisions the user row on first
/// sight - so these run against a real one.
/// </summary>
/// <remarks>
/// <para>
/// The cache is a field on the test class rather than a fresh one per service, because in production it
/// is a host-wide singleton and several of these behaviours only exist across two service instances.
/// Options are per-test, since almost every branch here is selected by one.
/// </para>
/// <para>
/// One dead line is not covered by anything below, deliberately: <c>ValidateUser</c> issues
/// <c>await _context.Users.AnyAsync()</c> and discards the result. It costs a query per uncached
/// request and has no observable effect, so there is nothing to assert. It is on Phase 5's fix list.
/// </para>
/// </remarks>
public class UserClaimsServiceTests(DatabaseFixture fixture) : DatabaseTestBase(fixture)
{
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

    /// <remarks>
    /// Characterization. <c>update: false</c> is the path <c>GetClaimsPrincipal</c> and so
    /// <c>RefreshClaims</c> take, and for a subject with no user row <c>ValidateUser</c> returns
    /// <c>null</c> - which skips the whole claim block, so the token comes back exactly as it went in and
    /// no row is created. A caller cannot distinguish "this user has no permissions" from "this user does
    /// not exist"; both are an untouched principal. Turns red when the two are separated.
    /// </remarks>
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

    /// <remarks>
    /// <para>
    /// Characterization of the silent <c>catch (Exception) { }</c> around <c>ValidateUser</c>'s save. A
    /// <c>name</c> claim containing a NUL byte cannot be stored in a PostgreSQL <c>text</c> column, so the
    /// insert fails - and the failure is swallowed. The caller is handed a fully populated principal for a
    /// user that was never persisted, and the request proceeds as if login succeeded. Anything it then
    /// writes with a foreign key onto the user row fails much later, somewhere unrelated.
    /// </para>
    /// <para>
    /// Turns red when the catch either logs or rethrows. The value of the test is not the NUL byte - it is
    /// that <em>any</em> failure to persist the user is invisible.
    /// </para>
    /// </remarks>
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

    /// <remarks>
    /// Characterization, and the reason the harness disables claims caching. The <c>jti</c> comparison that
    /// invalidates stale claims is guarded by <c>UseGroupsFromIdP || UseRolesFromIdP</c>, so in blueprint's
    /// shipped configuration - both false - a <em>new</em> token is still served the old claim set. A user
    /// whose role was revoked keeps their permissions until the entry expires, even if they log out and
    /// back in. Turns red when the invalidation is unconditional.
    /// </remarks>
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

    /// <remarks>
    /// Characterization. <c>RefreshClaims</c> forwards to <c>GetClaimsPrincipal(userId, false)</c>, whose
    /// <c>setAsCurrent || _currentClaimsPrincipal.GetId() == userId</c> dereferences a null current
    /// principal. So refreshing claims off-request - a background job, or a queue worker reacting to a
    /// permission change - throws. Turns red when line 98 short-circuits on null.
    /// </remarks>
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

    /// <remarks>
    /// Characterization of the same null dereference, reached directly. Every caller that has not first
    /// set a current principal hits it - see
    /// <see cref="RefreshClaims_WithNoCurrentPrincipal_ThrowsNullReferenceException"/>.
    /// </remarks>
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

    /// <remarks>
    /// Characterization, and the subtlest thing in this file. <c>addNewClaims</c> filters by claim
    /// <em>type</em>, not by type and value, so a second call on an identity that already carries a
    /// <c>Permission</c> claim adds none of the new ones - the identity already has "a Permission claim".
    /// Within one call it is harmless, because the claims are added after the loop. Across two it means a
    /// role change cannot take effect on an identity that already has permissions, which is exactly what
    /// <c>RefreshClaims</c> looks like it should achieve. Turns red when the filter compares values too.
    /// </remarks>
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

    /// <remarks>
    /// Characterization. Legacy <c>UserPermissions</c> rows become claims whose <em>type</em> is the
    /// <c>BlueprintClaimTypes</c> name and whose value is <c>"true"</c> - not <c>Permission</c> claims. No
    /// policy, requirement or handler in the codebase reads those types, so a user granted
    /// <c>SystemAdmin</c> the legacy way holds no system permission at all. Turns red when the legacy rows
    /// are either mapped onto <see cref="SystemPermission"/> or removed.
    /// </remarks>
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

    /// <remarks>
    /// Characterization. <c>Enum.TryParse</c> is case-sensitive by default, so a legacy key differing only
    /// in case is dropped without a word. The keys are free text in the database.
    /// </remarks>
    [Fact]
    public async Task AddUserClaims_ALegacyPermissionKeyIsCaseSensitive()
    {
        var actor = await Actor().SeedAsync();
        await GiveLegacyPermission(actor.Id, "systemadmin");

        var principal = await Service().AddUserClaims(Token(actor.Id), update: false);

        Assert.Null(principal.FindFirst(BlueprintClaimTypes.SystemAdmin.ToString()));
    }

    /// <remarks>
    /// Characterization. <c>Enum.TryParse</c> also accepts a numeric string, so a legacy key of <c>"1"</c>
    /// silently becomes <c>ContentDeveloper</c>. A key of <c>"99"</c> becomes a claim type of <c>"99"</c> -
    /// the enum's own <c>ToString</c> for an undefined value.
    /// </remarks>
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

    /// <remarks>
    /// Characterization. The path is only walked for a claim whose <c>ValueType</c> is <c>"JSON"</c>. For a
    /// plain string claim - the default <c>ValueType</c> of <c>new Claim(type, value)</c>, and what several
    /// IdPs emit - the raw value is returned and <em>every remaining path segment is ignored</em>. So with
    /// <c>RolesClaimPath = "realm_access.roles"</c>, a string claim called <c>realm_access</c> whose value
    /// happens to be a role name grants that role. Turns red when a string claim with unconsumed segments
    /// returns nothing.
    /// </remarks>
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

    /// <remarks>
    /// Characterization. Invalid JSON in the claim is swallowed by <c>catch (JsonException)</c>, so a
    /// misconfigured IdP mapper produces a caller with no permissions and no diagnostic anywhere.
    /// </remarks>
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

    /// <remarks>
    /// Characterization of dead code. <c>GetPermissionClaims</c> queries the groups a user belongs to and
    /// then discards the result - the comment there says group-based logic "can be added here". So group
    /// membership grants nothing, and the query runs on every uncached request. Turns red when groups
    /// contribute to the claim set, at which point this test should say what they contribute.
    /// </remarks>
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

    /// <remarks>
    /// Same dead code, reached through the IdP path: <c>UseGroupsFromIdP</c> and <c>GroupsClaimPath</c> are
    /// read, the matching group is found, and nothing comes of it.
    /// </remarks>
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
